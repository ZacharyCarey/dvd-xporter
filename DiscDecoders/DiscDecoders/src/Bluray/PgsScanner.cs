namespace DiscDecoders;

/// <summary>A PGS segment: its type (0x16 composition, 0x17 window, 0x14 palette, 0x15 object, 0x80 end), times (90 kHz) and data.</summary>
internal sealed record PgsSegment(byte Type, uint Pts, uint Dts, byte[] Data);

/// <summary>How many captions a Blu-ray subtitle stream shows, how many are forced, and the forced ones themselves.</summary>
/// <param name="ForcedSets">Display sets for a forced-only track: each forced caption, and the set after it
/// emptied so it disappears on time.</param>
internal sealed record PgsStreamScan(int Pid, int Total, int Forced, IReadOnlyList<IReadOnlyList<PgsSegment>> ForcedSets);

/// <summary>
/// Finds forced captions in Blu-ray (PGS) subtitles: captions marked to show even with subtitles off, e.g. the
/// translation of foreign-language lines, kept inside the full track. Reads a clip's transport stream for its
/// subtitle streams' packets.
/// </summary>
internal static class PgsScanner
{
    private const int PacketSize = 192;
    private const byte CompositionSegment = 0x16;
    private const byte WindowSegment = 0x17;
    private const byte EndSegment = 0x80;
    private const byte ForcedFlag = 0x40;
    private const byte CroppedFlag = 0x80;

    /// <summary>Reads a whole .m2ts (192-byte packets) for the subtitle streams with these PIDs.</summary>
    public static Dictionary<int, PgsStreamScan> Scan(Stream m2ts, IReadOnlyCollection<int> pids, CancellationToken cancellation = default)
    {
        var streams = pids.ToDictionary(p => p, p => new StreamState());
        Read(m2ts, streams, cancellation);
        return streams.ToDictionary(s => s.Key, s => new PgsStreamScan(s.Key, s.Value.Total, s.Value.Forced, s.Value.ForcedSets));
    }

    /// <summary>Reads a whole .m2ts for every segment of one subtitle stream, in order.</summary>
    public static void ReadSegments(Stream m2ts, int pid, Action<PgsSegment> onSegment, CancellationToken cancellation = default) =>
        Read(m2ts, new Dictionary<int, StreamState> { [pid] = new StreamState { OnSegment = onSegment } }, cancellation);

    private static void Read(Stream m2ts, Dictionary<int, StreamState> streams, CancellationToken cancellation)
    {
        var buffer = new byte[PacketSize * 8192];
        int filled = 0;
        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            int read = m2ts.Read(buffer, filled, buffer.Length - filled);
            filled += read;
            int whole = filled / PacketSize * PacketSize;
            for (int p = 0; p < whole; p += PacketSize)
                ReadPacket(buffer.AsSpan(p, PacketSize), streams);

            // Keep any part packet for the next read
            Array.Copy(buffer, whole, buffer, 0, filled - whole);
            filled -= whole;
            if (read <= 0)
                break;
        }

        foreach (var state in streams.Values)
            state.EndPes();
    }

    private static void ReadPacket(ReadOnlySpan<byte> packet, Dictionary<int, StreamState> streams)
    {
        // 4-byte arrival time, then a 188-byte transport packet
        var ts = packet[4..];
        if (ts[0] != 0x47)
            return;
        int pid = (ts[1] & 0x1F) << 8 | ts[2];
        if (!streams.TryGetValue(pid, out var state))
            return;

        bool start = (ts[1] & 0x40) != 0;
        int control = ts[3] >> 4 & 3;
        int offset = 4;
        if ((control & 2) != 0)
            offset += 1 + ts[4];
        if ((control & 1) == 0 || offset >= ts.Length)
            return;

        if (start)
            state.EndPes();
        state.Pes.AddRange(ts[offset..].ToArray());
    }

    private sealed class StreamState
    {
        public readonly List<byte> Pes = [];
        public readonly List<IReadOnlyList<PgsSegment>> ForcedSets = [];
        public int Total;
        public int Forced;

        /// <summary>Given every segment instead of counting captions, when set.</summary>
        public Action<PgsSegment>? OnSegment;

        private List<PgsSegment>? _set;
        private bool _showingForced;

        /// <summary>A whole PES packet: its segments (normally one) go into the display set being read.</summary>
        public void EndPes()
        {
            var pes = Pes.ToArray();
            Pes.Clear();
            if (pes.Length < 9 || pes[0] != 0 || pes[1] != 0 || pes[2] != 1)
                return;

            uint pts = 0, dts = 0;
            int flags = pes[7] >> 6;
            if ((flags & 2) != 0 && pes.Length >= 14)
                pts = ReadTimestamp(pes, 9);
            dts = (flags & 1) != 0 && pes.Length >= 19 ? ReadTimestamp(pes, 14) : pts;

            int p = 9 + pes[8];
            while (p + 3 <= pes.Length)
            {
                byte type = pes[p];
                int size = pes[p + 1] << 8 | pes[p + 2];
                if (p + 3 + size > pes.Length)
                    break;
                var segment = new PgsSegment(type, pts, dts, pes[(p + 3)..(p + 3 + size)]);
                p += 3 + size;
                if (OnSegment is not null)
                {
                    OnSegment(segment);
                    continue;
                }

                if (type == CompositionSegment)
                    _set = [];
                if (_set is null)
                    continue;
                _set.Add(segment);
                if (type == EndSegment)
                    EndSet(_set);
            }
        }

        private void EndSet(List<PgsSegment> set)
        {
            _set = null;
            var composition = set[0];
            if (composition.Type != CompositionSegment || composition.Data.Length < 11)
                return;

            var objects = Objects(composition.Data);
            if (objects.Count > 0)
                Total++;
            var forced = objects.Where(o => (o.Flags & ForcedFlag) != 0).ToList();
            if (forced.Count > 0)
            {
                Forced++;
                // Only the forced objects: a caption mixing forced and other text shows just the forced part
                ForcedSets.Add(set.Select(s => s == composition ? s with { Data = WithObjects(s.Data, forced) } : s).ToList());
                _showingForced = true;
            }
            else if (_showingForced)
            {
                // The next caption takes the forced one off the screen; kept as an empty one, so it still does
                var clear = composition with { Data = WithObjects(composition.Data, []) };
                ForcedSets.Add([clear, .. set.Where(s => s.Type is WindowSegment or EndSegment)]);
                _showingForced = false;
            }
        }
    }

    private sealed record CompositionObject(byte Flags, byte[] Bytes);

    // A composition segment: size, frame rate, number, state, palette update and id, then its objects
    private const int ObjectsOffset = 11;

    private static List<CompositionObject> Objects(byte[] composition)
    {
        var objects = new List<CompositionObject>();
        int count = composition[10];
        int p = ObjectsOffset;
        for (int i = 0; i < count && p + 8 <= composition.Length; i++)
        {
            byte flags = composition[p + 3];
            int length = (flags & CroppedFlag) != 0 ? 16 : 8;
            if (p + length > composition.Length)
                break;
            objects.Add(new CompositionObject(flags, composition[p..(p + length)]));
            p += length;
        }

        return objects;
    }

    private static byte[] WithObjects(byte[] composition, List<CompositionObject> objects)
    {
        var data = new List<byte>(composition[..10]) { (byte)objects.Count };
        foreach (var o in objects)
            data.AddRange(o.Bytes);
        return [.. data];
    }

    /// <summary>A display set as .sup records ("PG", PTS, DTS, then the segment), keeping its timestamps.</summary>
    public static byte[] ToSup(IReadOnlyList<PgsSegment> set)
    {
        using var sup = new MemoryStream();
        Span<byte> header = stackalloc byte[11];
        foreach (var segment in set)
        {
            sup.Write("PG"u8);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header, segment.Pts);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(header[4..], segment.Dts);
            header[8] = segment.Type;
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(header[9..], (ushort)segment.Data.Length);
            sup.Write(header);
            sup.Write(segment.Data);
        }

        return sup.ToArray();
    }

    private static uint ReadTimestamp(byte[] b, int at) =>
        (uint)(((long)(b[at] & 0x0E) << 29 | (long)b[at + 1] << 22 | (long)(b[at + 2] & 0xFE) << 14 | (long)b[at + 3] << 7 | (long)b[at + 4] >> 1) & 0xFFFFFFFF);
}
