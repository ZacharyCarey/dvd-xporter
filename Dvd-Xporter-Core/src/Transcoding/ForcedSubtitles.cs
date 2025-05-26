using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using DiscDecoders.Common;

namespace DvdXporter.Core.Transcoding;

/// <param name="Total">Captions shown.</param>
/// <param name="Forced">Of those, how many are forced: shown even with subtitles off.</param>
public sealed record SubtitleCount(int Total, int Forced);

/// <summary>Explanations of forced subtitles, and notes on subtitle tracks.</summary>
public static class SubtitleHelp
{
    public const string Forced =
        "Forced captions are ones meant to show even with subtitles off, such as the translation of a few lines in "
        + "another language. Many discs mark them inside the full subtitle track, where only disc players notice: "
        + "Emby and most players only see a whole track as forced or not, so with subtitles off those lines go missing.";

    // A track with this small a share of the busiest track's captions is likely just the forced lines
    private const double ForcedOnlyShare = 0.10;

    /// <summary>A note for a subtitle track's port, e.g. " · 14 forced of 812", or "" if there's nothing to say.</summary>
    public static string Note(SubtitleCount count, int mostCaptions)
    {
        if (count.Forced > 0)
            return count.Forced == count.Total ? $" · all {count.Total} forced" : $" · {count.Forced} forced of {count.Total}";
        if (count.Total > 0 && mostCaptions > 0 && count.Total < mostCaptions * ForcedOnlyShare)
            return $" · {count.Total} {(count.Total == 1 ? "caption" : "captions")} (likely forced only)";
        return "";
    }
}

/// <summary>Writes forced-only subtitle files that ffmpeg reads as ordinary subtitle tracks.</summary>
public static class ForcedSubtitleWriter
{
    /// <summary>
    /// A PGS .sup file of display sets, each moved to its time on the video's timeline, keeping the
    /// spacing of its own segments.
    /// </summary>
    /// <param name="sets">Each display set as .sup records with its original timestamps (see <see cref="ForcedCaption.Data"/>).</param>
    public static void WriteSup(string path, IEnumerable<(DiscTime Time, byte[] Set)> sets)
    {
        const int HeaderSize = 13; // "PG", PTS, DTS, segment type, segment size
        using var file = File.Create(path);
        foreach (var (time, set) in sets)
        {
            uint basePts = 0;
            for (int at = 0; at + HeaderSize <= set.Length;)
            {
                uint segmentPts = BinaryPrimitives.ReadUInt32BigEndian(set.AsSpan(at + 2));
                uint segmentDts = BinaryPrimitives.ReadUInt32BigEndian(set.AsSpan(at + 6));
                int size = BinaryPrimitives.ReadUInt16BigEndian(set.AsSpan(at + 11));
                if (at == 0)
                    basePts = segmentPts;

                uint pts = (uint)Math.Max(0, time.Ticks) + (segmentPts - basePts);
                uint dts = segmentDts <= segmentPts ? pts - Math.Min(pts, segmentPts - segmentDts) : pts;
                file.Write("PG"u8);
                WriteUInt32(file, pts);
                WriteUInt32(file, dts);
                file.Write(set.AsSpan(at + 10, 3 + Math.Min(size, set.Length - at - HeaderSize)));
                at += HeaderSize + size;
            }
        }
    }

    /// <summary>
    /// A VobSub pair (.idx and .sub) of DVD subtitle units at their times on the video's timeline (seconds).
    /// Returns the .idx path, which ffmpeg opens.
    /// </summary>
    /// <param name="palette">The DVD's subtitle colours, as "palette: rrggbb, ...".</param>
    public static string WriteVobSub(string basePath, int width, int height, string? palette, string? language,
        IEnumerable<(double Time, byte[] Unit)> units)
    {
        var idx = new StringBuilder("# VobSub index file, v7 (do not modify this line!)\n");
        idx.Append(CultureInfo.InvariantCulture, $"size: {width}x{height}\n");
        if (palette is not null)
            idx.Append(palette).Append('\n');
        idx.Append(CultureInfo.InvariantCulture, $"id: {(language is { Length: >= 2 } l ? l[..2] : "en")}, index: 0\n");

        using (var sub = File.Create(basePath + ".sub"))
        {
            foreach (var (time, unit) in units)
            {
                var span = TimeSpan.FromSeconds(Math.Max(0, time));
                idx.Append(CultureInfo.InvariantCulture,
                    $"timestamp: {(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}:{span.Milliseconds:000}, filepos: {sub.Position:X9}\n");
                WriteUnit(sub, unit, (uint)Math.Round(Math.Max(0, time) * 90000));
            }
        }

        File.WriteAllText(basePath + ".idx", idx.ToString());
        return basePath + ".idx";
    }

    private const int PackSize = 2048;

    /// <summary>A subtitle unit as 2048-byte program stream packs: private stream 1 packets, the first with the time.</summary>
    private static void WriteUnit(Stream sub, byte[] unit, uint pts)
    {
        int offset = 0;
        bool first = true;
        while (offset < unit.Length)
        {
            var pack = new byte[PackSize];
            // Pack header: start code, a zero clock, mux rate, no stuffing
            pack[0] = 0; pack[1] = 0; pack[2] = 1; pack[3] = 0xBA;
            pack[4] = 0x44; pack[5] = 0; pack[6] = 4; pack[7] = 0; pack[8] = 4; pack[9] = 1;
            pack[10] = 0x01; pack[11] = 0x89; pack[12] = 0xC3; pack[13] = 0xF8;

            int p = 14;
            int header = first ? 5 : 0;
            int room = PackSize - p - 9 - header - 1;
            int chunk = Math.Min(room, unit.Length - offset);
            // Leave room for a padding packet (6 bytes at least) or none at all
            int left = room - chunk;
            if (left is > 0 and < 6)
                chunk -= 6 - left;

            int length = 3 + header + 1 + chunk;
            pack[p] = 0; pack[p + 1] = 0; pack[p + 2] = 1; pack[p + 3] = 0xBD;
            pack[p + 4] = (byte)(length >> 8); pack[p + 5] = (byte)length;
            pack[p + 6] = 0x81;
            pack[p + 7] = (byte)(first ? 0x80 : 0x00);
            pack[p + 8] = (byte)header;
            p += 9;
            if (first)
            {
                pack[p] = (byte)(0x21 | (pts >> 29 & 0x0E));
                pack[p + 1] = (byte)(pts >> 22);
                pack[p + 2] = (byte)((pts >> 14 & 0xFE) | 1);
                pack[p + 3] = (byte)(pts >> 7);
                pack[p + 4] = (byte)(pts << 1 | 1);
                p += 5;
            }

            pack[p++] = 0x20;
            Array.Copy(unit, offset, pack, p, chunk);
            p += chunk;
            offset += chunk;
            first = false;

            int padding = PackSize - p;
            if (padding >= 6)
            {
                pack[p] = 0; pack[p + 1] = 0; pack[p + 2] = 1; pack[p + 3] = 0xBE;
                pack[p + 4] = (byte)((padding - 6) >> 8); pack[p + 5] = (byte)(padding - 6);
                for (int i = p + 6; i < PackSize; i++)
                    pack[i] = 0xFF;
            }

            sub.Write(pack);
        }
    }

    private static void WriteUInt32(Stream stream, uint value)
    {
        stream.WriteByte((byte)(value >> 24));
        stream.WriteByte((byte)(value >> 16));
        stream.WriteByte((byte)(value >> 8));
        stream.WriteByte((byte)value);
    }
}
