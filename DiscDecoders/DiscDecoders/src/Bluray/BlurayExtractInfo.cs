using DiscDecoders.BlurayDecoder;
using DiscDecoders.Common;

namespace DiscDecoders;

public partial class BlurayDisc
{
    private static VideoCodec GetVideoCodec(StreamCodingType stream)
    {
        switch (stream)
        {
            case StreamCodingType.H264: return VideoCodec.H264;
            case StreamCodingType.Hevc: return VideoCodec.H265;
            case StreamCodingType.Mpeg1Video: return VideoCodec.MPEG1;
            case StreamCodingType.Mpeg2Video: return VideoCodec.MPEG2;
            case StreamCodingType.Vc1: return VideoCodec.VC1;
            default:
                throw new Exception($"Unknown bluray video codec: {stream}");
        }
    }

    private static VideoResolution GetVideoResolution(VideoFormat format)
    {
        switch (format)
        {
            case VideoFormat.Format480i:
                return VideoResolution.Resolution_480i;
            case VideoFormat.Format480p:
                return VideoResolution.Resolution_480p;
            case VideoFormat.Format576i:
                return VideoResolution.Resolution_576i;
            case VideoFormat.Format576p:
                return VideoResolution.Resolution_576p;
            case VideoFormat.Format720p:
                return VideoResolution.Resolution_720p;
            case VideoFormat.Format1080i:
                return VideoResolution.Resolution_1080i;
            case VideoFormat.Format1080p:
                return VideoResolution.Resolution_1080p;
            case VideoFormat.Format2160p:
                return VideoResolution.Resolution_2160p;
            default:
                throw new Exception($"Invalild resolution: {format}");
        }
    }

    private static double GetVideoRate(VideoRate rate)
    {
        switch (rate)
        {
            case VideoRate.Rate24: return 24;
            case VideoRate.Rate23_976: return 24000.0 / 1001.0;
            case VideoRate.Rate25: return 25;
            case VideoRate.Rate29_97: return 30000.0 / 1001.0;
            case VideoRate.Rate50: return 50;
            case VideoRate.Rate59_94: return 60000.0 / 1001.0;
            default:
                throw new Exception($"Invalid video rate: {rate}");
        }
    }

    private static AspectRatio GetVideoAspect(VideoAspect? aspect)
    {
        switch (aspect)
        {
            case VideoAspect.Aspect16_9: return AspectRatio.Aspect_16_9;
            case VideoAspect.Aspect4_3: return AspectRatio.Aspect_4_3;
            case null:
                throw new Exception("A video stream has no aspect ratio: its clip info is missing, or doesn't list it.");
            default:
                throw new Exception($"Invalid aspect ratio: {aspect}");
        }
    }

    private static AudioCodec GetAudioCodec(StreamCodingType stream)
    {
        switch (stream)
        {
            case StreamCodingType.Ac3: return AudioCodec.AC3;
            case StreamCodingType.Ac3Plus: return AudioCodec.AC3Plus;
            case StreamCodingType.Ac3PlusSecondary: return AudioCodec.AC3Plus_Secondary;
            case StreamCodingType.Dts: return AudioCodec.DTS;
            case StreamCodingType.DtsHd: return AudioCodec.DTSHD;
            case StreamCodingType.DtsHdMaster: return AudioCodec.DTSHD_Master;
            case StreamCodingType.DtsHdSecondary: return AudioCodec.DTSHD_Secondary;
            case StreamCodingType.Lpcm: return AudioCodec.LPCM;
            case StreamCodingType.Mpeg1Audio: return AudioCodec.MPEG1;
            case StreamCodingType.Mpeg2Audio: return AudioCodec.MPEG2;
            case StreamCodingType.TrueHd: return AudioCodec.TruHD;
            default:
                throw new Exception($"Unknown bluray audio codec: {stream}");
        }
    }

    private static double GetAudioRate(AudioRate rate)
    {
        switch (rate)
        {
            case AudioRate.Rate192:
            case AudioRate.Rate192Combo: return 192;
            case AudioRate.Rate48: return 48;
            case AudioRate.Rate96:
            case AudioRate.Rate96Combo: return 96;
            default:
                throw new Exception($"Invalid audio rate: {rate}");
        }
    }

    private static AudioChannelLayout GetChannelLayout(AudioFormat format) => format switch
    {
        AudioFormat.Mono => AudioChannelLayout.Mono,
        AudioFormat.Stereo => AudioChannelLayout.Stereo,
        AudioFormat.MultiChannel => AudioChannelLayout.MultiChannel,
        AudioFormat.Combo => AudioChannelLayout.StereoAndMultiChannel,
        _ => throw new Exception($"Invalid audio format: {format}"),
    };

    private static TextCharacterSet? GetCharacterSet(TextCharacterCode code) => code switch
    {
        0 => null,
        TextCharacterCode.Utf8 => TextCharacterSet.UTF8,
        TextCharacterCode.Utf16BE => TextCharacterSet.UTF16BE,
        TextCharacterCode.ShiftJis => TextCharacterSet.ShiftJIS,
        TextCharacterCode.EucKr => TextCharacterSet.EUCKR,
        TextCharacterCode.Gb18030 => TextCharacterSet.GB18030,
        TextCharacterCode.CnGb => TextCharacterSet.CNGB,
        TextCharacterCode.Big5 => TextCharacterSet.Big5,
        _ => throw new Exception($"Invalid character code: {code}"),
    };

    private static VideoStreamInfo ExtractVideoStreamInfo(PlaylistStream stream, VideoAspect? aspect, bool isSecondary, DiscTime duration)
    {
        var info = new VideoStreamInfo();
        info.Codec = GetVideoCodec(stream.CodingType);
        info.Resolution = GetVideoResolution(stream.VideoFormat);
        info.Framerate = GetVideoRate(stream.VideoRate);
        info.Language = stream.Language;
        info.StreamId = stream.Pid;
        info.SubpathId = stream.SubPathId;
        info.AspectRatio = GetVideoAspect(aspect);
        info.IsSecondary = isSecondary;
        info.Duration = duration;
        info.DynamicRange = GetDynamicRange(stream);
        info.ColorSpace = GetColorSpace(stream.ColorSpace, info.Resolution);

        return info;
    }

    private static VideoDynamicRange GetDynamicRange(PlaylistStream stream) => stream.DynamicRange switch
    {
        DynamicRange.Sdr => VideoDynamicRange.SDR,
        DynamicRange.Hdr10 => stream.HdrPlus ? VideoDynamicRange.HDR10Plus : VideoDynamicRange.HDR10,
        DynamicRange.DolbyVision => VideoDynamicRange.DolbyVision,
        _ => throw new Exception($"Unknown bluray dynamic range: {stream.DynamicRange}"),
    };

    /// <summary>
    /// Only UHD (HEVC) streams record their colour space. For the rest, the Blu-ray spec fixes it by resolution:
    /// BT.709 for HD, BT.601 for standard definition.
    /// </summary>
    private static VideoColorSpace GetColorSpace(ColorSpace colorSpace, VideoResolution resolution) => colorSpace switch
    {
        ColorSpace.BT709 => VideoColorSpace.BT709,
        ColorSpace.BT2020 => VideoColorSpace.BT2020,
        ColorSpace.Unspecified => resolution is VideoResolution.Resolution_480i or VideoResolution.Resolution_480p
            or VideoResolution.Resolution_576i or VideoResolution.Resolution_576p
            ? VideoColorSpace.BT601
            : VideoColorSpace.BT709,
        _ => throw new Exception($"Unknown bluray colour space: {colorSpace}"),
    };

    private static uint GetChannelCount(AudioFormat format)
    {
        switch (format)
        {
            case AudioFormat.Mono: return 1;
            case AudioFormat.Stereo: return 2;
            case AudioFormat.Combo:
            case AudioFormat.MultiChannel:
                return 0;
            default:
                throw new Exception("Invalid audio format.");
        }
    }

    private static AudioStreamInfo ExtractAudioStreamInfo(PlaylistStream stream, bool isSecondary, DiscTime duration)
    {
        var info = new AudioStreamInfo();
        info.Codec = GetAudioCodec(stream.CodingType);
        info.Rate = GetAudioRate(stream.AudioRate);
        info.Language = stream.Language;
        info.StreamId = stream.Pid;
        info.SubpathId = stream.SubPathId;
        info.IsSecondary = isSecondary;
        info.ChannelCount = GetChannelCount(stream.AudioFormat);
        info.ChannelLayout = GetChannelLayout(stream.AudioFormat);
        info.Duration = duration;
        return info;
    }

    private static SubtitleStreamInfo ExtractPresentationStreamInfo(PlaylistStream stream)
    {
        var info = new SubtitleStreamInfo();

        switch (stream.CodingType)
        {
            case StreamCodingType.PresentationGraphics:
                info.Codec = SubtitleCodec.hdmv_pgs_subtitle;
                break;
            case StreamCodingType.TextSubtitle:
                info.Codec = SubtitleCodec.hdmv_text_subtitle;
                break;
            default:
                info.Codec = SubtitleCodec.Unknown;
                break;
        }

        info.Language = stream.Language;
        info.StreamId = stream.Pid;
        info.SubpathId = stream.SubPathId;
        info.Duration = null;
        info.IsSecondary = false;
        info.CharacterSet = info.Codec == SubtitleCodec.hdmv_text_subtitle ? GetCharacterSet(stream.CharacterCode) : null;
        return info;
    }
}
