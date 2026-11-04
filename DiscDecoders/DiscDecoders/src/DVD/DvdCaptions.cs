using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>
/// Draws a DVD subtitle stream's captions. Each subtitle unit (SPU) carries a 2-bit run-length coded picture, stored as
/// two interlaced fields, and display commands: when it shows and goes (as delays after its time), where, and which
/// four of the disc's sixteen colours (and how see-through each) its pixel values are.
/// </summary>
internal static class DvdCaptions
{
    private const int SectorSize = 2048;
    // A delay counts 1024 ticks of the 90 kHz clock
    private const int DelayTicks = 1024;

    /// <summary>
    /// Reads a cell's program stream (see <see cref="DvdDisc.OpenClip"/>) for the captions of the subtitle stream
    /// <paramref name="streamId"/> (0x20–0x3F), in order.
    /// </summary>
    /// <param name="palette">The cell's sixteen subtitle colours (see <see cref="ClipInfo.SubtitlePalette"/>); grey shades without one.</param>
    public static List<CaptionPicture> Read(Stream cell, int streamId, IReadOnlyList<Rgb>? palette, CancellationToken cancellation)
    {
        var colours = palette is { Count: >= 16 }
            ? palette
            : Enumerable.Range(0, 16).Select(i => new Rgb((byte)(i * 17), (byte)(i * 17), (byte)(i * 17))).ToList();
        var captions = new List<CaptionPicture>();
        var unit = new List<byte>();
        uint? unitPts = null;
        var sector = new byte[SectorSize];
        while (ReadSector(cell, sector))
        {
            cancellation.ThrowIfCancellationRequested();
            if (sector[0] != 0 || sector[1] != 0 || sector[2] != 1 || sector[3] != 0xBA)
                continue;

            // MPEG-2 pack header: 14 bytes plus stuffing, then the packets
            int p = 14 + (sector[13] & 7);
            while (p + 9 <= SectorSize && sector[p] == 0 && sector[p + 1] == 0 && sector[p + 2] == 1)
            {
                int next = Math.Min(SectorSize, p + 6 + (sector[p + 4] << 8 | sector[p + 5]));
                int headerEnd = p + 9 + sector[p + 8];
                if (sector[p + 3] == 0xBD && headerEnd < next && sector[headerEnd] == streamId)
                {
                    // A packet with a time starts a unit; a unit starts with its size and may span packets
                    if ((sector[p + 7] & 0x80) != 0 && p + 14 <= next)
                    {
                        unit.Clear();
                        unitPts = ReadPts(sector.AsSpan(p + 9));
                    }

                    unit.AddRange(sector.AsSpan((headerEnd + 1)..next));
                    if (unit.Count >= 2 && unit.Count >= (unit[0] << 8 | unit[1]) && unitPts is not null)
                    {
                        var caption = Decode([.. unit.Take(unit[0] << 8 | unit[1])], unitPts.Value, colours);
                        if (caption is not null)
                            Add(captions, caption);
                        unit.Clear();
                        unitPts = null;
                    }
                }

                p = next;
            }
        }

        return captions;
    }

    // A caption goes when the next one shows, if not before
    private static void Add(List<CaptionPicture> captions, CaptionPicture caption)
    {
        if (captions.Count > 0 && (captions[^1].End is null || captions[^1].End > caption.Start))
            captions[^1] = captions[^1] with { End = caption.Start };
        captions.Add(caption);
    }

    private static bool ReadSector(Stream stream, byte[] sector)
    {
        int filled = 0;
        while (filled < SectorSize)
        {
            int read = stream.Read(sector, filled, SectorSize - filled);
            if (read <= 0)
                return false;
            filled += read;
        }

        return true;
    }

    /// <summary>A whole subtitle unit drawn, or null if it shows nothing (e.g. one that only clears the screen).</summary>
    private static CaptionPicture? Decode(byte[] unit, uint pts, IReadOnlyList<Rgb> palette)
    {
        if (unit.Length < 4)
            return null;
        int offset = unit[2] << 8 | unit[3];
        int? startDelay = null, stopDelay = null;
        bool forced = false;
        int x1 = 0, x2 = -1, y1 = 0, y2 = -1, topField = 0, bottomField = 0;
        // Pixel values 0–3: background, text, outline and anti-aliasing, as the commands map them
        var colourIndexes = new int[] { 0, 1, 2, 3 };
        var alphas = new int[] { 0, 15, 15, 15 };

        // Each control sequence: its delay, where the next is, then commands up to 0xFF; the last points at itself
        for (int sequences = 0; sequences < 64 && offset + 4 <= unit.Length; sequences++)
        {
            int delay = unit[offset] << 8 | unit[offset + 1];
            int next = unit[offset + 2] << 8 | unit[offset + 3];
            int p = offset + 4;
            while (p < unit.Length)
            {
                int command = unit[p++];
                if (command == 0xFF)
                    break;
                switch (command)
                {
                    case 0x00:
                        forced = true;
                        startDelay ??= delay;
                        break;
                    case 0x01:
                        startDelay ??= delay;
                        break;
                    case 0x02:
                        stopDelay ??= delay;
                        break;
                    case 0x03 when p + 2 <= unit.Length:
                        colourIndexes = [unit[p + 1] & 0x0F, unit[p + 1] >> 4, unit[p] & 0x0F, unit[p] >> 4];
                        p += 2;
                        break;
                    case 0x04 when p + 2 <= unit.Length:
                        alphas = [unit[p + 1] & 0x0F, unit[p + 1] >> 4, unit[p] & 0x0F, unit[p] >> 4];
                        p += 2;
                        break;
                    case 0x05 when p + 6 <= unit.Length:
                        x1 = unit[p] << 4 | unit[p + 1] >> 4;
                        x2 = (unit[p + 1] & 0x0F) << 8 | unit[p + 2];
                        y1 = unit[p + 3] << 4 | unit[p + 4] >> 4;
                        y2 = (unit[p + 4] & 0x0F) << 8 | unit[p + 5];
                        p += 6;
                        break;
                    case 0x06 when p + 4 <= unit.Length:
                        topField = unit[p] << 8 | unit[p + 1];
                        bottomField = unit[p + 2] << 8 | unit[p + 3];
                        p += 4;
                        break;
                    // Colour and contrast changes within lines: a size, then that many bytes; not drawn
                    case 0x07 when p + 2 <= unit.Length:
                        p += unit[p] << 8 | unit[p + 1];
                        break;
                    default:
                        p = unit.Length;
                        break;
                }
            }

            if (next == offset)
                break;
            offset = next;
        }

        int width = x2 - x1 + 1, height = y2 - y1 + 1;
        if (startDelay is null || width <= 0 || height <= 0 || width > 1920 || height > 1088)
            return null;

        var values = new byte[width * height];
        DecodeField(unit, topField, values, width, height, 0);
        DecodeField(unit, bottomField, values, width, height, 1);
        if (values.All(v => alphas[v] == 0))
            return null;

        var rgba = new byte[width * height * 4];
        for (int i = 0; i < values.Length; i++)
        {
            int alpha = alphas[values[i]];
            if (alpha == 0)
                continue;
            var colour = palette[colourIndexes[values[i]]];
            rgba[i * 4] = colour.R;
            rgba[i * 4 + 1] = colour.G;
            rgba[i * 4 + 2] = colour.B;
            rgba[i * 4 + 3] = (byte)(alpha * 17);
        }

        var start = new DiscTime((long)pts + startDelay.Value * DelayTicks);
        // The longest delay there is means "until the next", as some authoring tools write it
        DiscTime? end = stopDelay is not null && stopDelay > startDelay && stopDelay < 0xFFFF
            ? new DiscTime((long)pts + stopDelay.Value * DelayTicks)
            : null;
        return new CaptionPicture(start, end, forced, x1, y1, width, height, rgba);
    }

    /// <summary>
    /// One field's lines (every other line, from <paramref name="firstLine"/>), coded from <paramref name="at"/> in
    /// nibbles: a run's length and value take 1–4 nibbles (the more leading zeros, the longer), and a run of length 0
    /// fills the line. Each line starts on a whole byte.
    /// </summary>
    private static void DecodeField(byte[] unit, int at, byte[] values, int width, int height, int firstLine)
    {
        int nibble = at * 2;
        int Next()
        {
            if (nibble / 2 >= unit.Length)
                return 0;
            int b = unit[nibble / 2];
            int value = nibble % 2 == 0 ? b >> 4 : b & 0x0F;
            nibble++;
            return value;
        }

        for (int line = firstLine; line < height && nibble / 2 < unit.Length; line += 2)
        {
            int column = 0;
            while (column < width && nibble / 2 < unit.Length)
            {
                int code = Next();
                if (code < 0x4)
                {
                    code = code << 4 | Next();
                    if (code < 0x10)
                    {
                        code = code << 4 | Next();
                        if (code < 0x40)
                            code = code << 4 | Next();
                    }
                }

                int run = code >> 2;
                int end = run == 0 ? width : Math.Min(width, column + run);
                values.AsSpan(line * width + column, end - column).Fill((byte)(code & 3));
                column = end;
            }

            // Lines start on a byte
            if (nibble % 2 != 0)
                nibble++;
        }
    }

    private static uint ReadPts(ReadOnlySpan<byte> b) =>
        (uint)(((long)(b[0] & 0x0E) << 29 | (long)b[1] << 22 | (long)(b[2] & 0xFE) << 14 | (long)b[3] << 7 | (long)b[4] >> 1) & 0xFFFFFFFF);
}
