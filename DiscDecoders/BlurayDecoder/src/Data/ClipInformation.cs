using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>
/// A run of a clip's packets on one system time clock: its presentation timestamps run on from
/// <see cref="PresentationStart"/> to <see cref="PresentationEnd"/>.
/// </summary>
public class StcSequence
{
    /// <summary>The PID of the packets carrying the clock.</summary>
    public readonly ushort PcrPid;

    /// <summary>The source packet it starts at.</summary>
    public readonly uint StartPacket;

    internal readonly uint PresentationStart45k;
    internal readonly uint PresentationEnd45k;

    public DiscTime PresentationStart => NavigationFile.ToDiscTime(PresentationStart45k);
    public DiscTime PresentationEnd => NavigationFile.ToDiscTime(PresentationEnd45k);

    internal StcSequence(BitReader reader)
    {
        PcrPid = reader.ReadUInt16();
        StartPacket = reader.ReadUInt32();
        PresentationStart45k = reader.ReadUInt32();
        PresentationEnd45k = reader.ReadUInt32();
    }
}

/// <summary>A run of a clip's packets with continuous arrival times, made of one or more <see cref="StcSequence"/>s.</summary>
public class AtcSequence
{
    /// <summary>The source packet it starts at.</summary>
    public readonly uint StartPacket;

    /// <summary>The STC ID of its first STC sequence: play items refer to them by STC ID (see <see cref="PlayItemClip.StcId"/>).</summary>
    public readonly byte FirstStcId;

    public readonly ReadOnlyCollection<StcSequence> StcSequences;

    internal AtcSequence(BitReader reader)
    {
        StartPacket = reader.ReadUInt32();
        int count = reader.ReadByte();
        FirstStcId = reader.ReadByte();
        var sequences = new StcSequence[count];
        for (int i = 0; i < count; i++)
            sequences[i] = new StcSequence(reader);
        StcSequences = sequences.AsReadOnly();
    }
}

/// <summary>One of a clip's elementary streams, as its program info describes it.</summary>
public class ProgramStream
{
    public readonly ushort Pid;

    public readonly StreamCodingType CodingType;

    /// <summary>The raw format field: a <see cref="VideoFormat"/> or an <see cref="AudioFormat"/>, by the coding type.</summary>
    public readonly byte Format;

    /// <summary>The raw rate field: a <see cref="VideoRate"/> or an <see cref="AudioRate"/>, by the coding type.</summary>
    public readonly byte Rate;

    /// <summary>A video stream's aspect ratio (which playlists don't record).</summary>
    public readonly VideoAspect Aspect;

    /// <summary>Whether a video stream has closed captions (line 21 data).</summary>
    public readonly bool HasClosedCaptions;

    public readonly DynamicRange DynamicRange;
    public readonly ColorSpace ColorSpace;
    public readonly byte CrFlag;
    public readonly bool HdrPlus;

    public readonly TextCharacterCode CharacterCode;

    /// <summary>The ISO 639-2 language code of an audio or subtitle stream, or "" if it has none.</summary>
    public readonly string LanguageCode = "";

    public VideoFormat VideoFormat => (VideoFormat)Format;
    public AudioFormat AudioFormat => (AudioFormat)Format;
    public VideoRate VideoRate => (VideoRate)Rate;
    public AudioRate AudioRate => (AudioRate)Rate;

    internal ProgramStream(BitReader reader, Action<string>? onWarning)
    {
        Pid = reader.ReadUInt16();
        int length = reader.ReadByte();
        long start = reader.Position;
        CodingType = (StreamCodingType)reader.ReadByte();
        switch (CodingType)
        {
            case StreamCodingType.Mpeg1Video:
            case StreamCodingType.Mpeg2Video:
            case StreamCodingType.Vc1:
            case StreamCodingType.H264:
            case StreamCodingType.H264Mvc:
            case StreamCodingType.Hevc:
                Format = reader.ReadBits<byte>(4);
                Rate = reader.ReadBits<byte>(4);
                Aspect = (VideoAspect)reader.ReadBits<byte>(4);
                reader.Skip(2);
                HasClosedCaptions = reader.ReadBit();
                if (CodingType == StreamCodingType.Hevc)
                {
                    CrFlag = reader.ReadBits<byte>(1);
                    DynamicRange = (DynamicRange)reader.ReadBits<byte>(4);
                    ColorSpace = (ColorSpace)reader.ReadBits<byte>(4);
                    HdrPlus = reader.ReadBit();
                }
                break;

            case StreamCodingType.Mpeg1Audio:
            case StreamCodingType.Mpeg2Audio:
            case StreamCodingType.Lpcm:
            case StreamCodingType.Ac3:
            case StreamCodingType.Dts:
            case StreamCodingType.TrueHd:
            case StreamCodingType.Ac3Plus:
            case StreamCodingType.DtsHd:
            case StreamCodingType.DtsHdMaster:
            case StreamCodingType.Ac3PlusSecondary:
            case StreamCodingType.DtsHdSecondary:
                Format = reader.ReadBits<byte>(4);
                Rate = reader.ReadBits<byte>(4);
                LanguageCode = reader.ReadString(3);
                break;

            case StreamCodingType.PresentationGraphics:
            case StreamCodingType.InteractiveGraphics:
            case (StreamCodingType)0xa0: // picture-in-picture subtitles
                LanguageCode = reader.ReadString(3);
                break;

            case StreamCodingType.TextSubtitle:
                CharacterCode = (TextCharacterCode)reader.ReadByte();
                LanguageCode = reader.ReadString(3);
                break;

            default:
                onWarning?.Invoke($"Unrecognised coding type 0x{(byte)CodingType:x2} for PID {Pid}.");
                break;
        }
        reader.Seek(start + length);
    }
}

/// <summary>A program: the streams a stretch of the clip carries, and the PID of its program map table.</summary>
public class ClipProgram
{
    /// <summary>The source packet it starts at.</summary>
    public readonly uint StartPacket;

    public readonly ushort ProgramMapPid;

    public readonly byte GroupCount;

    public readonly ReadOnlyCollection<ProgramStream> Streams;

    internal ClipProgram(BitReader reader, Action<string>? onWarning)
    {
        StartPacket = reader.ReadUInt32();
        ProgramMapPid = reader.ReadUInt16();
        int count = reader.ReadByte();
        GroupCount = reader.ReadByte();
        var streams = new ProgramStream[count];
        for (int i = 0; i < count; i++)
            streams[i] = new ProgramStream(reader, onWarning);
        Streams = streams.AsReadOnly();
    }
}

/// <summary>
/// One stream's entry point map: where in the clip each access point (a picture decoding can start from) is, in two
/// levels. Coarse entries hold the high bits of the timestamp and packet number; fine entries the low bits.
/// </summary>
public class EntryPointMap
{
    internal readonly record struct Coarse(int FineIndex, int Pts, uint Packet);
    internal readonly record struct Fine(bool IsAngleChangePoint, byte EndPositionOffset, int Pts, int Packet);

    public readonly ushort Pid;

    public readonly byte StreamType;

    internal readonly Coarse[] CoarseEntries;
    internal readonly Fine[] FineEntries;

    /// <summary>How many access points it lists.</summary>
    public int Count => FineEntries.Length;

    internal EntryPointMap(ushort pid, byte streamType, Coarse[] coarse, Fine[] fine)
    {
        Pid = pid;
        StreamType = streamType;
        CoarseEntries = coarse;
        FineEntries = fine;
    }

    // The map's entries for each stream are kept apart from the list of streams
    internal static ReadOnlyCollection<EntryPointMap> ReadAll(BitReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length == 0)
            return ReadOnlyCollection<EntryPointMap>.Empty;
        reader.Skip(12);
        reader.Skip(4); // CPI type
        long mapStart = reader.Position;
        reader.Skip(8);
        int count = reader.ReadByte();

        var headers = new (ushort Pid, byte StreamType, int Coarse, int Fine, long Start)[count];
        for (int i = 0; i < count; i++)
        {
            ushort pid = reader.ReadUInt16();
            reader.Skip(10);
            byte streamType = reader.ReadBits<byte>(4);
            int coarse = reader.ReadUInt16();
            int fine = (int)reader.ReadBits(18);
            headers[i] = (pid, streamType, coarse, fine, reader.ReadUInt32() + mapStart);
        }

        var maps = new EntryPointMap[count];
        for (int i = 0; i < count; i++)
        {
            var (pid, streamType, coarseCount, fineCount, start) = headers[i];
            reader.Seek(start);
            uint fineStart = reader.ReadUInt32();

            reader.Require(8L * coarseCount, "entry point map");
            var coarse = new Coarse[coarseCount];
            for (int j = 0; j < coarseCount; j++)
                coarse[j] = new Coarse((int)reader.ReadBits(18), (int)reader.ReadBits(14), reader.ReadUInt32());

            reader.Seek(start + fineStart);
            reader.Require(4L * fineCount, "entry point map");
            var fine = new Fine[fineCount];
            for (int j = 0; j < fineCount; j++)
                fine[j] = new Fine(reader.ReadBit(), reader.ReadBits<byte>(3), (int)reader.ReadBits(11), (int)reader.ReadBits(17));

            maps[i] = new EntryPointMap(pid, streamType, coarse, fine);
        }
        return maps.AsReadOnly();
    }
}

/// <summary>
/// A clip info file (.clpi): what's in one clip (.m2ts file), its timestamps, and where its access points are, for
/// seeking <see href="https://github.com/lw/BluRay/wiki/CLPI"/>.
/// </summary>
public class ClipInformation
{
    /// <summary>The size of the clip's source packets: a 4-byte arrival timestamp and a 188-byte transport packet.</summary>
    public const int SourcePacketSize = 192;

    /// <summary>The clip's number, e.g. "00012" for 00012.clpi and 00012.m2ts.</summary>
    public readonly string ClipId;

    /// <summary>The file's format version, e.g. "0200" (0300 for UHD).</summary>
    public readonly string Version;

    public readonly byte ClipStreamType;

    /// <summary>What the clip is for: 1 for a movie, 2 or 3 for a slideshow, 4 to 7 for a sub path's clip (6 for text subtitles).</summary>
    public readonly byte ApplicationType;

    /// <summary>The highest rate the clip is read at, in bytes per second.</summary>
    public readonly uint RecordingRate;

    /// <summary>How many 192-byte source packets the clip has.</summary>
    public readonly uint SourcePacketCount;

    /// <summary>The fonts a text subtitle clip uses, by file number (BDMV/AUXDATA/xxxxx.otf).</summary>
    public readonly ReadOnlyCollection<string> FontFiles = ReadOnlyCollection<string>.Empty;

    public readonly ReadOnlyCollection<AtcSequence> AtcSequences;

    public readonly ReadOnlyCollection<ClipProgram> Programs;

    /// <summary>Entry point maps, one per video stream (usually just one).</summary>
    public readonly ReadOnlyCollection<EntryPointMap> EntryPoints;

    /// <summary>The timestamp its presentation starts at (often 10:00 rather than 0), or null if it has no STC sequence.</summary>
    public DiscTime? PresentationStart => (FirstStc is not null) ? FirstStc.PresentationStart : null;

    /// <summary>How long it plays: its STC sequences' lengths together.</summary>
    public DiscTime Duration
    {
        get
        {
            ulong ticks = 0;
            foreach (var stc in AtcSequences.SelectMany(atc => atc.StcSequences))
            {
                if (stc.PresentationEnd45k > stc.PresentationStart45k)
                    ticks += (ulong)(stc.PresentationEnd45k - stc.PresentationStart45k) * 2;
            }
            return new DiscTime(ticks);
        }
    }

    private StcSequence? FirstStc => AtcSequences.FirstOrDefault()?.StcSequences.FirstOrDefault();

    internal ClipInformation(BitReader reader, string clipId, Action<string>? onWarning)
    {
        ClipId = clipId;
        Version = NavigationFile.ReadHeader(reader, "HDMV");
        reader.Require(20, "header");
        uint sequenceStart = reader.ReadUInt32();
        uint programStart = reader.ReadUInt32();
        uint cpiStart = reader.ReadUInt32();

        // Clip info
        reader.Seek(40);
        reader.Skip(32 + 16);
        ClipStreamType = reader.ReadByte();
        ApplicationType = reader.ReadByte();
        reader.Skip(31);
        bool hasAtcDelta = reader.ReadBit();
        RecordingRate = reader.ReadUInt32();
        SourcePacketCount = reader.ReadUInt32();
        reader.Skip(128 * 8);
        int typeInfoLength = reader.ReadUInt16();
        reader.Skip(typeInfoLength * 8);
        if (hasAtcDelta)
        {
            reader.Skip(8);
            int deltas = reader.ReadByte();
            reader.Skip(deltas * (32 + 5 * 8 + 4 * 8 + 8));
        }
        if (ApplicationType == 6)
        {
            reader.Skip(8);
            int fontCount = reader.ReadByte();
            var fonts = new string[fontCount];
            for (int i = 0; i < fontCount; i++)
            {
                fonts[i] = reader.ReadString(5);
                reader.Skip(8);
            }
            FontFiles = fonts.AsReadOnly();
        }

        // Sequence info
        reader.Seek(sequenceStart);
        reader.Skip(5 * 8);
        int atcCount = reader.ReadByte();
        var atcSequences = new AtcSequence[atcCount];
        for (int i = 0; i < atcCount; i++)
            atcSequences[i] = new AtcSequence(reader);
        AtcSequences = atcSequences.AsReadOnly();

        // Program info
        reader.Seek(programStart);
        reader.Skip(5 * 8);
        int programCount = reader.ReadByte();
        var programs = new ClipProgram[programCount];
        for (int i = 0; i < programCount; i++)
            programs[i] = new ClipProgram(reader, onWarning);
        Programs = programs.AsReadOnly();

        // Characteristic point info: the entry point map
        reader.Seek(cpiStart);
        EntryPoints = EntryPointMap.ReadAll(reader);
    }

    /// <summary>The program info for a stream, or null if the clip has none for it.</summary>
    public ProgramStream? FindStream(int pid) => Programs.SelectMany(p => p.Streams).FirstOrDefault(s => s.Pid == pid);

    /// <summary>
    /// The byte offset in the clip of the last access point at or before <paramref name="sinceStart"/>, counted from
    /// the start of its presentation: where to start reading to decode from that time.
    /// </summary>
    public long GetByteOffset(TimeSpan sinceStart)
    {
        uint start = FirstStc?.PresentationStart45k ?? 0;
        uint timestamp = start + (uint)Math.Max(0, sinceStart.TotalSeconds * 45000);
        return (long)FindSourcePacket(timestamp, true, 0) * SourcePacketSize;
    }

    /// <summary>
    /// The source packet of the access point nearest a timestamp (45 kHz, in STC sequence <paramref name="stcId"/>):
    /// the last one before it if <paramref name="before"/>, otherwise the first one after it.
    /// </summary>
    internal uint FindSourcePacket(uint timestamp, bool before, byte stcId)
    {
        if (EntryPoints.Count == 0)
            return before ? 0 : SourcePacketCount;

        // Assumes that there is only one PID of interest
        var coarse = EntryPoints[0].CoarseEntries;
        var fine = EntryPoints[0].FineEntries;
        int ii, jj, start, end;
        int fineIndex = 0;
        ulong pts, coarsePts;
        uint spn, coarseSpn;

        // The STC sequence's first packet is where in the map to start searching
        uint stcSpn = FindStcStartPacket(stcId);
        for (ii = 0; ii < coarse.Length; ii++)
        {
            fineIndex = coarse[ii].FineIndex;
            // The wanted entry is either after this one or in the middle of the one before
            if (coarse[ii].Packet >= stcSpn)
                break;
        }
        if (ii >= coarse.Length)
            return SourcePacketCount;

        pts = ((ulong)(coarse[ii].Pts & ~0x01) << 18) + ((ulong)fine[fineIndex].Pts << 8);
        if (pts > timestamp && ii != 0)
        {
            // The starting point and the wanted timestamp are in the coarse entry before
            ii--;
            coarsePts = (uint)(coarse[ii].Pts & ~0x01) << 18;
            coarseSpn = coarse[ii].Packet;
            start = coarse[ii].FineIndex;
            end = coarse[ii + 1].FineIndex;
            // A fine entry with both its packet after the STC sequence's start and its timestamp after the wanted one
            for (jj = start; jj < end; jj++)
            {
                pts = coarsePts + ((uint)fine[jj].Pts << 8);
                spn = (coarseSpn & ~0x1FFFFu) + (uint)fine[jj].Packet;
                if (stcSpn >= spn && pts > timestamp)
                    break;
            }
        }
        else
        {
            // The wanted timestamp is somewhere after the coarse entry the STC sequence's start is in
            start = ii;
            for (ii = start; ii < coarse.Length; ii++)
            {
                fineIndex = coarse[ii].FineIndex;
                pts = ((ulong)(coarse[ii].Pts & ~0x01) << 18) + ((ulong)fine[fineIndex].Pts << 8);
                if (pts > timestamp)
                    break;
            }
            // Before the first entry: the start of the clip
            if (ii == 0)
                return 0;
            ii--;
            coarsePts = (uint)(coarse[ii].Pts & ~0x01) << 18;
            start = coarse[ii].FineIndex;
            end = ii < coarse.Length - 1 ? coarse[ii + 1].FineIndex : fine.Length;
            for (jj = start; jj < end; jj++)
            {
                pts = coarsePts + ((uint)fine[jj].Pts << 8);
                if (pts > timestamp)
                    break;
            }
        }

        if (before)
            jj--;
        if (jj == end)
        {
            ii++;
            if (ii >= coarse.Length)
                return SourcePacketCount;
            jj = coarse[ii].FineIndex;
        }
        return (coarse[ii].Packet & ~0x1FFFFu) + (uint)fine[jj].Packet;
    }

    /// <summary>The source packet STC sequence <paramref name="stcId"/> starts at.</summary>
    internal uint FindStcStartPacket(byte stcId)
    {
        foreach (var atc in AtcSequences)
        {
            if (stcId < atc.FirstStcId + atc.StcSequences.Count)
                return atc.StcSequences[stcId - atc.FirstStcId].StartPacket;
        }
        return 0;
    }
}
