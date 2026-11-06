using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>Measures a clip's audio streams' bitrates from their first few seconds (see <see cref="BitrateMeter"/>).</summary>
internal static class TsBitrates
{
    private const int PacketSize = 192;

    // Far more than the few seconds each needs, even with audio muxed well ahead of the video
    private const long MostToRead = 256L << 20;

    /// <summary>Reads a .m2ts (192-byte packets) from its start until the streams with these PIDs are measured.</summary>
    public static IReadOnlyList<AudioBitrate> Measure(Stream m2ts, IReadOnlyCollection<int> pids, CancellationToken cancellation)
    {
        var meter = new BitrateMeter();
        var buffer = new byte[PacketSize * 4096];
        long read = 0;
        while (read < MostToRead && !meter.HasMeasured(pids))
        {
            cancellation.ThrowIfCancellationRequested();
            int filled = 0;
            while (filled < buffer.Length)
            {
                int n = m2ts.Read(buffer, filled, buffer.Length - filled);
                if (n <= 0)
                    break;
                filled += n;
            }

            for (int p = 0; p + PacketSize <= filled; p += PacketSize)
                ReadPacket(buffer.AsSpan(p + 4, PacketSize - 4), pids, meter);
            read += filled;
            if (filled < buffer.Length)
                break;
        }

        return meter.Measured;
    }

    // A 4-byte arrival time, then a 188-byte transport packet; a packet starting a PES has its header, maybe with a time
    private static void ReadPacket(ReadOnlySpan<byte> ts, IReadOnlyCollection<int> pids, BitrateMeter meter)
    {
        if (ts[0] != 0x47)
            return;
        int pid = (ts[1] & 0x1F) << 8 | ts[2];
        if (!pids.Contains(pid))
            return;

        int control = ts[3] >> 4 & 3;
        int offset = 4;
        if ((control & 2) != 0)
            offset += 1 + ts[4];
        if ((control & 1) == 0 || offset >= ts.Length)
            return;

        uint? pts = null;
        bool start = (ts[1] & 0x40) != 0;
        if (start && offset + 9 <= ts.Length && ts[offset] == 0 && ts[offset + 1] == 0 && ts[offset + 2] == 1)
        {
            if ((ts[offset + 7] & 0x80) != 0 && offset + 14 <= ts.Length)
                pts = ReadTimestamp(ts[(offset + 9)..]);
            offset = Math.Min(ts.Length, offset + 9 + ts[offset + 8]);
        }

        meter.Add(pid, ts.Length - offset, pts);
    }

    private static uint ReadTimestamp(ReadOnlySpan<byte> b) =>
        (uint)(((long)(b[0] & 0x0E) << 29 | (long)b[1] << 22 | (long)(b[2] & 0xFE) << 14 | (long)b[3] << 7 | (long)b[4] >> 1) & 0xFFFFFFFF);
}
