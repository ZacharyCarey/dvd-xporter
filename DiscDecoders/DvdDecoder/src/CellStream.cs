using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// One cell's sectors (or several cells' in a row) as a read-only, seekable stream: an MPEG-2 program stream
/// that players read like a VOB, straight from the disc. Made with <see cref="Dvd.OpenCell"/> or <see cref="Dvd.OpenCells"/>.
/// </summary>
/// <remarks>
/// A cell is a run of sectors in its title set's VOB files, which the cell address table lists. A cell in an
/// interleaved (multi-angle) block is listed as several runs, with the other angles' sectors between them;
/// this stream joins its own runs and skips the rest.
/// </remarks>
public sealed class CellStream : Stream
{
    // Offsets of the system header and the VOBU start/end presentation times in a navigation pack
    private const int SystemHeaderOffset = 14;
    private const int VobuStartPtmOffset = 0x39;
    private const int VobuEndPtmOffset = 0x3D;

    // How far back from the end to look for the last navigation pack (a VOBU is at most about a second)
    private const int MaxSectorsToLastNav = 4096;

    private readonly IDiscFileSystem reader;
    private readonly VobStream vob;
    private readonly (long FirstSector, long SectorCount)[] runs;
    // Byte offset in this stream where each run starts
    private readonly long[] runStarts;
    private readonly long length;
    private long position;

    /// <param name="pieces">The sector runs to read, in play order.</param>
    internal CellStream(IDiscFileSystem reader, IfoFile ifo, bool isMenu, IEnumerable<CellAddress> pieces)
    {
        this.reader = reader;
        vob = new VobStream(reader, ifo, isMenu);
        runs = pieces
            .Select(x => ((long)x.StartSector, (long)x.LastSector - x.StartSector + 1))
            .ToArray();
        runStarts = new long[runs.Length];
        long offset = 0;
        for (int i = 0; i < runs.Length; i++)
        {
            runStarts[i] = offset;
            offset += runs[i].SectorCount * Util.SECTOR_SIZE;
        }
        length = offset;
    }

    public override bool CanRead => true;

    public override bool CanSeek => true;

    public override bool CanWrite => false;

    public override long Length => length;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int total = 0;
        while (count > 0 && position < length)
        {
            int run = Array.BinarySearch(runStarts, position);
            if (run < 0)
                run = ~run - 1;

            long inRun = position - runStarts[run];
            long runBytes = runs[run].SectorCount * Util.SECTOR_SIZE;
            int toRead = (int)Math.Min(count, runBytes - inRun);

            vob.Seek(runs[run].FirstSector * Util.SECTOR_SIZE + inRun, SeekOrigin.Begin);
            int read = vob.Read(buffer, offset, toRead);
            if (read <= 0)
                break;

            position += read;
            offset += read;
            count -= read;
            total += read;
        }

        return total;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        long target = origin switch
        {
            SeekOrigin.Current => position + offset,
            SeekOrigin.End => length + offset,
            _ => offset,
        };
        if (target < 0)
            throw new IOException("Offset went before the start of the stream.");
        position = target;
        return position;
    }

    /// <summary>
    /// The cell's presentation start and end, from its first and last navigation packs (the same times the
    /// cell extraction uses), or null if they can't be found. Leaves <see cref="Position"/> where it was.
    /// </summary>
    public (DiscTime Start, DiscTime End)? ReadPresentationTimes()
    {
        long saved = position;
        try
        {
            var sector = new byte[Util.SECTOR_SIZE];
            long sectors = length / Util.SECTOR_SIZE;
            if (sectors == 0 || !ReadSector(0, sector) || !IsNavPack(sector))
                return null;
            var start = new DiscTime(ReadUInt32(sector, VobuStartPtmOffset));

            for (long i = sectors - 1; i >= Math.Max(0, sectors - MaxSectorsToLastNav); i--)
            {
                if (ReadSector(i, sector) && IsNavPack(sector))
                    return (start, new DiscTime(ReadUInt32(sector, VobuEndPtmOffset)));
            }

            return null;
        }
        finally
        {
            position = saved;
        }
    }

    /// <summary>
    /// Byte offset of the last VOBU that starts at or before <paramref name="sinceStart"/> (from the cell's start),
    /// found by a binary search over the navigation packs that begin each VOBU. Leaves <see cref="Position"/> where it was.
    /// </summary>
    public long OffsetOfTime(TimeSpan sinceStart)
    {
        long saved = position;
        try
        {
            var sector = new byte[Util.SECTOR_SIZE];
            long sectors = length / Util.SECTOR_SIZE;
            if (sectors == 0 || !ReadSector(0, sector) || !IsNavPack(sector))
                return 0;
            ulong target = ReadUInt32(sector, VobuStartPtmOffset) + (ulong)Math.Max(0, sinceStart.TotalSeconds * 90000);

            long low = 0, high = sectors - 1, best = 0;
            while (low <= high)
            {
                long middle = low + (high - low) / 2;
                // The first navigation pack at or after the middle
                long nav = -1;
                for (long i = middle; i <= high && i < middle + MaxSectorsToLastNav; i++)
                {
                    if (ReadSector(i, sector) && IsNavPack(sector))
                    {
                        nav = i;
                        break;
                    }
                }

                if (nav < 0)
                {
                    high = middle - 1;
                }
                else if (ReadUInt32(sector, VobuStartPtmOffset) <= target)
                {
                    best = nav;
                    low = nav + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            return best * Util.SECTOR_SIZE;
        }
        finally
        {
            position = saved;
        }
    }

    /// <summary>
    /// Reads the whole stream once to find what it really holds, which the IFO can't say: the streams that
    /// have data, the presentation span, the number of pictures (to tell film from video), whether the video is
    /// interlaced, and the audio and subtitle packets before the first picture. Those belong to the cell before (a disc muxes sound ahead of
    /// pictures), so a player starting here would play them early. Leaves <see cref="Position"/> where it was.
    /// </summary>
    public CellScan Scan()
    {
        long saved = position;
        try
        {
            var scanner = new Scanner();
            const int SectorsPerRead = 256;
            var buffer = new byte[SectorsPerRead * Util.SECTOR_SIZE];
            position = 0;
            long sectorIndex = 0;
            while (true)
            {
                int total = 0;
                while (total < buffer.Length)
                {
                    int read = Read(buffer, total, buffer.Length - total);
                    if (read <= 0)
                        break;
                    total += read;
                }

                for (int offset = 0; offset + Util.SECTOR_SIZE <= total; offset += Util.SECTOR_SIZE)
                    scanner.ScanSector(buffer.AsSpan(offset, Util.SECTOR_SIZE), sectorIndex++);
                if (total < buffer.Length)
                    break;
            }

            return scanner.Finish();
        }
        finally
        {
            position = saved;
        }
    }

    /// <summary>Picks a program stream apart pack by pack for <see cref="Scan"/>.</summary>
    private sealed class Scanner
    {
        // Lead-in packets are looked for this far in; a disc muxes sound at most a second or so ahead
        private const long LeadInSectors = 4096;

        private readonly List<int> _streams = [];
        private readonly Dictionary<int, List<(long Offset, int Length, uint? Pts)>> _leadIn = [];
        private uint? _startPtm;
        private uint? _endPtm;
        private int _pictures;
        private int _zeros;
        private bool _afterPrefix;

        // The MPEG-2 extension being read (its first bytes, which may span packets), and what they've said
        private readonly byte[] _extension = new byte[5];
        private int _extensionLength = -1;
        private bool _progressiveSequence;
        private int _codedPictures;
        private int _progressiveFrames;

        // Subtitle units (SPUs) being put back together from their packets, by stream
        private readonly Dictionary<int, (List<byte> Data, uint? Pts)> _units = [];
        private readonly Dictionary<int, (int Total, int Forced)> _subtitleCounts = [];
        private readonly List<CellScan.ForcedCaption> _forced = [];
        private readonly BitrateMeter _bitrates = new();

        public void ScanSector(ReadOnlySpan<byte> sector, long index)
        {
            if (sector[0] != 0 || sector[1] != 0 || sector[2] != 1 || sector[3] != 0xBA)
                return;

            if (IsNavPack(sector))
            {
                _startPtm ??= ReadUInt32(sector, VobuStartPtmOffset);
                _endPtm = ReadUInt32(sector, VobuEndPtmOffset);
            }

            // MPEG-2 pack header: 14 bytes plus stuffing
            int p = 14 + (sector[13] & 7);
            while (p + 6 <= sector.Length && sector[p] == 0 && sector[p + 1] == 0 && sector[p + 2] == 1)
            {
                int id = sector[p + 3];
                int next = p + 6 + (sector[p + 4] << 8 | sector[p + 5]);
                if (next > sector.Length)
                    next = sector.Length;

                bool isVideo = id is >= 0xE0 and <= 0xEF;
                bool isAudio = id is >= 0xC0 and <= 0xDF;
                if ((isVideo || isAudio || id == 0xBD) && p + 9 <= next)
                {
                    int headerEnd = p + 9 + sector[p + 8];
                    uint? pts = (sector[p + 7] & 0x80) != 0 && p + 14 <= next ? ReadPts(sector[(p + 9)..]) : null;
                    int stream = id;
                    if (id == 0xBD && headerEnd < next)
                        stream = sector[headerEnd];
                    else if (id == 0xBD)
                        stream = -1;

                    if (stream >= 0)
                    {
                        if (!_streams.Contains(stream))
                            _streams.Add(stream);
                        // MPEG audio, and private stream 1's AC-3, DTS and LPCM (0x80-0xAF)
                        if (isAudio || (id == 0xBD && stream is >= 0x80 and <= 0xAF))
                            _bitrates.Add(stream, next - Math.Min(headerEnd, next), pts);
                        if (isVideo)
                            CountPictures(sector[Math.Min(headerEnd, next)..next]);
                        else if (id == 0xBD && stream is >= 0x20 and <= 0x3F && headerEnd + 1 <= next)
                            AddSubtitlePacket(stream, sector[(headerEnd + 1)..next], pts);
                        else if (index < LeadInSectors)
                            Remember(stream, index * Util.SECTOR_SIZE + p, next - p, pts);
                    }
                }

                p = next;
            }
        }

        private void Remember(int stream, long offset, int length, uint? pts)
        {
            if (!_leadIn.TryGetValue(stream, out var packets))
                _leadIn[stream] = packets = [];
            packets.Add((offset, length, pts));
        }

        /// <summary>
        /// Adds a packet of a subtitle stream. A subtitle unit starts with its size and may span packets; a packet
        /// with a time starts a new one. Once whole, it's counted, and kept if it's forced.
        /// </summary>
        private void AddSubtitlePacket(int stream, ReadOnlySpan<byte> payload, uint? pts)
        {
            if (pts is not null || !_units.ContainsKey(stream))
                _units[stream] = ([], pts);
            var (data, unitPts) = _units[stream];
            data.AddRange(payload.ToArray());
            if (data.Count < 2)
                return;

            int size = data[0] << 8 | data[1];
            if (size < 4 || data.Count < size)
                return;

            var unit = data.GetRange(0, size).ToArray();
            _units.Remove(stream);
            var counts = _subtitleCounts.GetValueOrDefault(stream);
            bool forced = IsForcedUnit(unit);
            _subtitleCounts[stream] = (counts.Total + 1, counts.Forced + (forced ? 1 : 0));
            if (forced && unitPts is not null)
                _forced.Add(new CellScan.ForcedCaption(stream, unitPts.Value, unit));
        }

        /// <summary>Whether a subtitle unit's display commands include "forced start" (shown even with subtitles off).</summary>
        private static bool IsForcedUnit(byte[] unit)
        {
            int offset = unit[2] << 8 | unit[3];
            for (int sequences = 0; sequences < 64 && offset + 4 <= unit.Length; sequences++)
            {
                int next = unit[offset + 2] << 8 | unit[offset + 3];
                int p = offset + 4;
                while (p < unit.Length)
                {
                    int command = unit[p++];
                    if (command == 0x00)
                        return true;
                    if (command == 0xFF)
                        break;
                    p += command switch
                    {
                        0x03 or 0x04 => 2,
                        0x05 => 6,
                        0x06 => 4,
                        // Colour and contrast changes: a size, then that many bytes
                        0x07 when p + 2 <= unit.Length => (unit[p] << 8 | unit[p + 1]),
                        0x01 or 0x02 => 0,
                        _ => unit.Length,
                    };
                }

                if (next == offset)
                    break;
                offset = next;
            }

            return false;
        }

        /// <summary>
        /// Counts picture start codes (00 00 01 00) and reads the MPEG-2 extensions that say whether pictures are
        /// interlaced (00 00 01 B5), carrying a code or extension split across packets.
        /// </summary>
        private void CountPictures(ReadOnlySpan<byte> data)
        {
            foreach (var b in data)
            {
                if (_extensionLength >= 0)
                {
                    _extension[_extensionLength++] = b;
                    if (_extensionLength == _extension.Length)
                    {
                        ReadExtension();
                        _extensionLength = -1;
                    }
                }

                if (_afterPrefix)
                {
                    _afterPrefix = false;
                    if (b == 0)
                        _pictures++;
                    else if (b == 0xB5)
                        _extensionLength = 0;
                }

                if (b == 0)
                {
                    _zeros++;
                }
                else
                {
                    if (b == 1 && _zeros >= 2)
                        _afterPrefix = true;
                    _zeros = 0;
                }
            }
        }

        /// <summary>
        /// A sequence extension says whether the whole sequence is progressive; a picture coding extension, whether
        /// its picture is (a progressive frame, possibly shown with a field repeated, as soft-telecined film is).
        /// </summary>
        private void ReadExtension()
        {
            switch (_extension[0] >> 4)
            {
                case 1:
                    _progressiveSequence |= (_extension[1] & 0x08) != 0;
                    break;
                case 8:
                    _codedPictures++;
                    if ((_extension[4] & 0x80) != 0)
                        _progressiveFrames++;
                    break;
            }
        }

        public CellScan Finish()
        {
            // A packet is lead-in if the stream's next timed packet still starts before the first picture, so the
            // packet that runs across the start is kept. Untimed packets go with the timed one before them.
            var blank = new List<CellScan.Packet>();
            var start = _startPtm;
            if (start is not null)
            {
                foreach (var packets in _leadIn.Values)
                {
                    bool blanking = false;
                    for (int i = 0; i < packets.Count; i++)
                    {
                        var pts = packets[i].Pts;
                        if (pts is not null)
                        {
                            var nextPts = packets.Skip(i + 1).FirstOrDefault(x => x.Pts is not null).Pts;
                            blanking = pts.Value < start.Value && nextPts is not null && nextPts.Value <= start.Value;
                        }

                        if (blanking)
                            blank.Add(new CellScan.Packet(packets[i].Offset, packets[i].Length));
                    }
                }
            }

            blank.Sort((a, b) => a.Offset.CompareTo(b.Offset));
            return new CellScan(_streams, _startPtm, _endPtm, _pictures, blank)
            {
                // MPEG-1 video has no extensions, and is always progressive
                Interlaced = _pictures > 0 ? _codedPictures > 0 && !_progressiveSequence && _progressiveFrames < _codedPictures : null,
                Subtitles = _subtitleCounts.Select(c => new CellScan.SubtitleCount(c.Key, c.Value.Total, c.Value.Forced)).ToList(),
                ForcedCaptions = _forced,
                AudioBitrates = _bitrates.Measured,
            };
        }

        private static uint ReadPts(ReadOnlySpan<byte> b) =>
            (uint)(((long)(b[0] & 0x0E) << 29 | (long)b[1] << 22 | (long)(b[2] & 0xFE) << 14 | (long)b[3] << 7 | (long)b[4] >> 1) & 0xFFFFFFFF);

        private static bool IsNavPack(ReadOnlySpan<byte> sector) =>
            sector[SystemHeaderOffset] == 0 && sector[SystemHeaderOffset + 1] == 0
            && sector[SystemHeaderOffset + 2] == 1 && sector[SystemHeaderOffset + 3] == 0xBB;

        private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
            (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);
    }

    private bool ReadSector(long index, byte[] sector)
    {
        position = index * Util.SECTOR_SIZE;
        int total = 0;
        while (total < sector.Length)
        {
            int read = Read(sector, total, sector.Length - total);
            if (read <= 0)
                return false;
            total += read;
        }
        return true;
    }

    private static bool IsNavPack(byte[] sector) =>
        sector[0] == 0 && sector[1] == 0 && sector[2] == 1 && sector[3] == 0xBA
        && sector[SystemHeaderOffset] == 0 && sector[SystemHeaderOffset + 1] == 0
        && sector[SystemHeaderOffset + 2] == 1 && sector[SystemHeaderOffset + 3] == 0xBB;

    private static uint ReadUInt32(byte[] data, int offset) =>
        (uint)(data[offset] << 24 | data[offset + 1] << 16 | data[offset + 2] << 8 | data[offset + 3]);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            vob.Dispose();
            reader.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>What <see cref="CellStream.Scan"/> found in a cell.</summary>
/// <param name="StreamIds">The streams with data, in the order they first appear: 0xE0 for video, 0xC0-0xDF for
/// MPEG audio, and private stream 1 substreams by their own ID (0x80 AC-3, 0x20 subtitles, ...).</param>
/// <param name="StartPtm">The first VOBU's start time (90 kHz), where the cell starts playing.</param>
/// <param name="EndPtm">The last VOBU's end time (90 kHz).</param>
/// <param name="Pictures">How many pictures the video has.</param>
/// <param name="LeadIn">Audio and subtitle packets before the first picture, which belong to the cell before.</param>
public sealed record CellScan(IReadOnlyList<int> StreamIds, uint? StartPtm, uint? EndPtm, int Pictures, IReadOnlyList<CellScan.Packet> LeadIn)
{
    /// <param name="Offset">Where the packet starts in the cell's stream.</param>
    /// <param name="Length">Its whole length, header included.</param>
    public sealed record Packet(long Offset, int Length);

    /// <summary>How many captions a subtitle stream shows, and how many of them are forced (shown even with subtitles off).</summary>
    public sealed record SubtitleCount(int StreamId, int Total, int Forced);

    /// <summary>
    /// Whether the video is stored interlaced: MPEG-2 video that isn't marked progressive throughout. Film stored as
    /// progressive frames (soft-telecined to 29.97, by repeating fields) isn't. Null if there's no video.
    /// </summary>
    public bool? Interlaced { get; init; }

    /// <summary>A forced caption: its subtitle stream, time (90 kHz) and whole subtitle unit.</summary>
    public sealed record ForcedCaption(int StreamId, uint Pts, byte[] Data);

    /// <summary>Each subtitle stream's captions.</summary>
    public IReadOnlyList<SubtitleCount> Subtitles { get; init; } = [];

    /// <summary>The forced captions, in order.</summary>
    public IReadOnlyList<ForcedCaption> ForcedCaptions { get; init; } = [];

    /// <summary>The audio streams' bitrates, from their first few seconds.</summary>
    public IReadOnlyList<AudioBitrate> AudioBitrates { get; init; } = [];

    /// <summary>How long the cell plays, from its navigation packs.</summary>
    public TimeSpan? Duration => StartPtm is not null && EndPtm is not null && EndPtm.Value > StartPtm.Value
        ? TimeSpan.FromSeconds((EndPtm.Value - StartPtm.Value) / 90000.0)
        : null;
}

/// <summary>
/// A program stream with some packets turned into padding as they're read, e.g. a cell's lead-in sound
/// (see <see cref="CellScan.LeadIn"/>). Nothing moves, so offsets and seeking stay the same.
/// </summary>
public sealed class PacketBlankingStream(Stream inner, IReadOnlyList<CellScan.Packet> packets) : Stream
{
    // The padding stream ID; players skip its contents
    private const byte PaddingStreamId = 0xBE;

    public override bool CanRead => true;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => false;

    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        long start = inner.Position;
        int read = inner.Read(buffer, offset, count);
        long end = start + read;
        foreach (var packet in packets)
        {
            if (packet.Offset >= end)
                break;
            if (packet.Offset + packet.Length <= start)
                continue;

            // Keep the start code and length; change the ID, and fill the rest
            Patch(buffer, offset, start, end, packet.Offset + 3, packet.Offset + 4, PaddingStreamId);
            Patch(buffer, offset, start, end, packet.Offset + 6, packet.Offset + packet.Length, 0xFF);
        }

        return read;
    }

    private static void Patch(byte[] buffer, int offset, long start, long end, long from, long to, byte value)
    {
        for (long i = Math.Max(from, start); i < Math.Min(to, end); i++)
            buffer[offset + (int)(i - start)] = value;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void Flush()
    {
    }

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            inner.Dispose();
        base.Dispose(disposing);
    }
}
