using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>How a stream is coded (its MPEG-TS stream type).</summary>
public enum StreamCodingType : byte
{
    Mpeg1Video = 0x01,
    Mpeg2Video = 0x02,
    Mpeg1Audio = 0x03,
    Mpeg2Audio = 0x04,
    Lpcm = 0x80,
    Ac3 = 0x81,
    Dts = 0x82,
    TrueHd = 0x83,
    Ac3Plus = 0x84,
    DtsHd = 0x85,
    DtsHdMaster = 0x86,
    Vc1 = 0xea,
    H264 = 0x1b,
    /// <summary>The dependent view of 3D (MVC) video.</summary>
    H264Mvc = 0x20,
    Hevc = 0x24,
    /// <summary>PGS subtitles.</summary>
    PresentationGraphics = 0x90,
    /// <summary>Menus.</summary>
    InteractiveGraphics = 0x91,
    TextSubtitle = 0x92,
    Ac3PlusSecondary = 0xa1,
    DtsHdSecondary = 0xa2,
}

public enum VideoFormat : byte
{
    /// <summary>ITU-R BT.601-5</summary>
    Format480i = 1,
    /// <summary>ITU-R BT.601-4</summary>
    Format576i = 2,
    /// <summary>SMPTE 293M</summary>
    Format480p = 3,
    /// <summary>SMPTE 274M</summary>
    Format1080i = 4,
    /// <summary>SMPTE 296M</summary>
    Format720p = 5,
    /// <summary>SMPTE 274M</summary>
    Format1080p = 6,
    /// <summary>ITU-R BT.1358</summary>
    Format576p = 7,
    /// <summary>BT.2020</summary>
    Format2160p = 8,
}

public enum VideoRate : byte
{
    /// <summary>23.976 Hz (24000/1001)</summary>
    Rate23_976 = 1,
    Rate24 = 2,
    Rate25 = 3,
    /// <summary>29.97 Hz (30000/1001)</summary>
    Rate29_97 = 4,
    Rate50 = 6,
    /// <summary>59.94 Hz (60000/1001)</summary>
    Rate59_94 = 7,
}

public enum VideoAspect : byte
{
    Aspect4_3 = 2,
    Aspect16_9 = 3,
}

/// <summary>A video stream's colour space. Only UHD (HEVC) streams record it.</summary>
public enum ColorSpace : byte
{
    Unspecified = 0,
    BT709 = 1,
    BT2020 = 2,
}

public enum DynamicRange : byte
{
    Sdr = 0,
    Hdr10 = 1,
    DolbyVision = 2,
}

public enum AudioFormat : byte
{
    Mono = 1,
    Stereo = 3,
    MultiChannel = 6,
    /// <summary>Stereo AC-3 or DTS core with a multi-channel lossless (TrueHD or DTS-HD) stream.</summary>
    Combo = 12,
}

public enum AudioRate : byte
{
    Rate48 = 1,
    Rate96 = 4,
    Rate192 = 5,
    /// <summary>A 48 or 96 kHz AC-3 or DTS core with a 192 kHz lossless stream.</summary>
    Rate192Combo = 12,
    /// <summary>A 48 kHz AC-3 or DTS core with a 96 kHz lossless stream.</summary>
    Rate96Combo = 14,
}

/// <summary>How a text subtitle stream's text is encoded.</summary>
public enum TextCharacterCode : byte
{
    Utf8 = 0x01,
    Utf16BE = 0x02,
    ShiftJis = 0x03,
    EucKr = 0x04,
    Gb18030 = 0x05,
    CnGb = 0x06,
    Big5 = 0x07,
}

/// <summary>
/// One stream a play item lets the viewer choose, from its stream number table: which packets it is (its PID, and its
/// sub path if it's in another clip) and how it's coded.
/// </summary>
public class PlaylistStream
{
    /// <summary>Where the stream is: 1 for the play item's own clip; 2 to 4 for a sub path's (see <see cref="SubPathId"/>).</summary>
    public readonly byte StreamType;

    /// <summary>The sub path whose clip carries the stream, or null if it's in the play item's own clip.</summary>
    public readonly byte? SubPathId;

    /// <summary>Which of the sub path's clips carries the stream, for a sub path with several.</summary>
    public readonly byte SubClipId;

    /// <summary>The stream's MPEG-TS packet ID.</summary>
    public readonly ushort Pid;

    public readonly StreamCodingType CodingType;

    /// <summary>The raw format field: a <see cref="VideoFormat"/> or an <see cref="AudioFormat"/>, by the coding type.</summary>
    public readonly byte Format;

    /// <summary>The raw rate field: a <see cref="VideoRate"/> or an <see cref="AudioRate"/>, by the coding type.</summary>
    public readonly byte Rate;

    /// <summary>A video stream's dynamic range. Only UHD (HEVC) streams record it; others are SDR.</summary>
    public readonly DynamicRange DynamicRange;

    /// <summary>A video stream's colour space. Only UHD (HEVC) streams record it; others are unspecified.</summary>
    public readonly ColorSpace ColorSpace;

    /// <summary>The raw cr_flag of a UHD video stream.</summary>
    public readonly byte CrFlag;

    /// <summary>The video stream carries HDR10+ dynamic metadata (UHD only).</summary>
    public readonly bool HdrPlus;

    /// <summary>A text subtitle stream's encoding.</summary>
    public readonly TextCharacterCode CharacterCode;

    /// <summary>The ISO 639-2 language code of an audio or subtitle stream, or "" if it has none.</summary>
    public readonly string LanguageCode = "";

    /// <summary>A secondary audio stream's primary audio streams (indexes into <see cref="StreamTable.Audio"/>) it can be mixed with.</summary>
    public readonly ReadOnlyCollection<byte> PrimaryAudioReferences = ReadOnlyCollection<byte>.Empty;

    /// <summary>A secondary video stream's secondary audio streams (indexes into <see cref="StreamTable.SecondaryAudio"/>).</summary>
    public readonly ReadOnlyCollection<byte> SecondaryAudioReferences = ReadOnlyCollection<byte>.Empty;

    /// <summary>A secondary video stream's picture-in-picture subtitles (indexes into <see cref="StreamTable.PipPresentationGraphics"/>).</summary>
    public readonly ReadOnlyCollection<byte> PipPresentationGraphicsReferences = ReadOnlyCollection<byte>.Empty;

    public VideoFormat VideoFormat => (VideoFormat)Format;
    public AudioFormat AudioFormat => (AudioFormat)Format;
    public VideoRate VideoRate => (VideoRate)Rate;
    public AudioRate AudioRate => (AudioRate)Rate;

    /// <summary>The stream's language; undetermined if it has none.</summary>
    public Iso639.Language Language => Iso639.Language.FromPart3(string.IsNullOrWhiteSpace(LanguageCode) ? "und" : LanguageCode);

    internal PlaylistStream(BitReader reader, Action<string>? onWarning, bool secondaryAudio = false, bool secondaryVideo = false)
    {
        // Where it is
        int length = reader.ReadByte();
        long start = reader.Position;
        StreamType = reader.ReadByte();
        byte subPathId = 0;
        switch (StreamType)
        {
            case 1:
                Pid = reader.ReadUInt16();
                break;
            case 2:
                subPathId = reader.ReadByte();
                SubClipId = reader.ReadByte();
                Pid = reader.ReadUInt16();
                break;
            case 3:
            case 4:
                subPathId = reader.ReadByte();
                Pid = reader.ReadUInt16();
                break;
            default:
                onWarning?.Invoke($"Unrecognised stream type 0x{StreamType:x2}.");
                break;
        }
        // Type 4 is picture-in-picture in the play item's own clip
        SubPathId = StreamType is 2 or 3 ? subPathId : null;
        reader.Seek(start + length);

        // How it's coded
        length = reader.ReadByte();
        start = reader.Position;
        CodingType = (StreamCodingType)reader.ReadByte();
        switch (CodingType)
        {
            case StreamCodingType.Mpeg1Video:
            case StreamCodingType.Mpeg2Video:
            case StreamCodingType.Vc1:
            case StreamCodingType.H264:
            case StreamCodingType.Hevc:
                Format = reader.ReadBits<byte>(4);
                Rate = reader.ReadBits<byte>(4);
                if (CodingType == StreamCodingType.Hevc)
                {
                    DynamicRange = (DynamicRange)reader.ReadBits<byte>(4);
                    ColorSpace = (ColorSpace)reader.ReadBits<byte>(4);
                    CrFlag = reader.ReadBits<byte>(1);
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
                LanguageCode = reader.ReadString(3);
                break;

            case StreamCodingType.TextSubtitle:
                CharacterCode = (TextCharacterCode)reader.ReadByte();
                LanguageCode = reader.ReadString(3);
                break;

            default:
                onWarning?.Invoke($"Unrecognised coding type 0x{(byte)CodingType:x2}.");
                break;
        }
        reader.Seek(start + length);

        if (secondaryAudio)
            PrimaryAudioReferences = ReadReferences(reader);
        if (secondaryVideo)
        {
            SecondaryAudioReferences = ReadReferences(reader);
            PipPresentationGraphicsReferences = ReadReferences(reader);
        }
    }

    // A count, a reserved byte, then the references padded to an even number of bytes
    private static ReadOnlyCollection<byte> ReadReferences(BitReader reader)
    {
        int count = reader.ReadByte();
        reader.Skip(8);
        var references = reader.ReadBytes(count);
        if (count % 2 != 0)
            reader.Skip(8);
        return references.AsReadOnly();
    }
}

/// <summary>
/// The streams a play item lets the viewer choose (its STN table), by kind, in the order the player numbers them.
/// </summary>
public class StreamTable
{
    public readonly ReadOnlyCollection<PlaylistStream> Video;
    public readonly ReadOnlyCollection<PlaylistStream> Audio;

    /// <summary>PGS subtitles.</summary>
    public readonly ReadOnlyCollection<PlaylistStream> PresentationGraphics;

    /// <summary>PGS subtitles for picture-in-picture video, numbered after <see cref="PresentationGraphics"/>.</summary>
    public readonly ReadOnlyCollection<PlaylistStream> PipPresentationGraphics;

    /// <summary>Menus.</summary>
    public readonly ReadOnlyCollection<PlaylistStream> InteractiveGraphics;

    /// <summary>Audio mixed over the primary audio, e.g. commentary for picture-in-picture video.</summary>
    public readonly ReadOnlyCollection<PlaylistStream> SecondaryAudio;

    /// <summary>Picture-in-picture video.</summary>
    public readonly ReadOnlyCollection<PlaylistStream> SecondaryVideo;

    /// <summary>
    /// Dolby Vision enhancement layers (UHD only), on dual-layer Dolby Vision discs: the main video stream is the HDR10
    /// base layer, and this stream adds the Dolby Vision layer on top.
    /// </summary>
    public readonly ReadOnlyCollection<PlaylistStream> DolbyVision;

    internal StreamTable(BitReader reader, Action<string>? onWarning)
    {
        int length = reader.ReadUInt16();
        long start = reader.Position;
        reader.Skip(16);

        int video = reader.ReadByte();
        int audio = reader.ReadByte();
        int pg = reader.ReadByte();
        int ig = reader.ReadByte();
        int secondaryAudio = reader.ReadByte();
        int secondaryVideo = reader.ReadByte();
        int pipPg = reader.ReadByte();
        int dolbyVision = reader.ReadByte();
        reader.Skip(4 * 8);

        Video = Read(video);
        Audio = Read(audio);
        PresentationGraphics = Read(pg);
        PipPresentationGraphics = Read(pipPg);
        InteractiveGraphics = Read(ig);
        SecondaryAudio = Read(secondaryAudio, isSecondaryAudio: true);
        SecondaryVideo = Read(secondaryVideo, isSecondaryVideo: true);
        DolbyVision = Read(dolbyVision);

        reader.Seek(start + length);

        ReadOnlyCollection<PlaylistStream> Read(int count, bool isSecondaryAudio = false, bool isSecondaryVideo = false)
        {
            var streams = new PlaylistStream[count];
            for (int i = 0; i < count; i++)
                streams[i] = new PlaylistStream(reader, onWarning, isSecondaryAudio, isSecondaryVideo);
            return streams.AsReadOnly();
        }
    }
}
