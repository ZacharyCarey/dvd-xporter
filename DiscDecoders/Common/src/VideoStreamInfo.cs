using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

public class VideoStreamInfo : StreamInfo
{
    public override StreamType Type => StreamType.Video;

    [JsonIgnore]
    public string CodecName
    {
        get
        {
            switch(Codec)
            {
                case VideoCodec.H264: return "H264";
                case VideoCodec.H265: return "H265";
                case VideoCodec.MPEG1: return "MPEG1";
                case VideoCodec.MPEG2: return "MPEG2";
                case VideoCodec.VC1: return "VC1";
                default:
                    throw new Exception("Invalid codec enum.");
            }
        }
    }

    [JsonIgnore]
    public string ResolutionName
    {
        get
        {
            switch(Resolution)
            {
                case VideoResolution.Resolution_240i: return "240i";
                case VideoResolution.Resolution_240p: return "240p";
                case VideoResolution.Resolution_288i: return "288i";
                case VideoResolution.Resolution_288p: return "288p";
                case VideoResolution.Resolution_480i: return "480i";
                case VideoResolution.Resolution_480p: return "480p";
                case VideoResolution.Resolution_576i: return "576i";
                case VideoResolution.Resolution_576p: return "576p";
                case VideoResolution.Resolution_720i: return "720i";
                case VideoResolution.Resolution_720p: return "720p";
                case VideoResolution.Resolution_1080i: return "1080i";
                case VideoResolution.Resolution_1080p: return "1080p";
                case VideoResolution.Resolution_2160p: return "2160p";
                default: 
                    throw new Exception("Invalid resolution enum.");
            }
        }
    }

    [JsonInclude]
    public string AspectRatioName
    {
        get
        {
            switch(AspectRatio)
            {
                case AspectRatio.Aspect_16_9: return "16x9";
                case AspectRatio.Aspect_4_3: return "4x3";
                default:
                    throw new Exception("Invalid aspect ratio enum.");
            }
        }
    }

    /// <summary>
    /// Gives <see cref="Resolution"/> the scan found by reading the video (where the navigation files don't record
    /// it): its interlaced or progressive form, e.g. 480i or 480p. A resolution that has only one form is left alone.
    /// </summary>
    public void SetInterlaced(bool interlaced)
    {
        Resolution = (Resolution, interlaced) switch
        {
            (VideoResolution.Resolution_240p, true) => VideoResolution.Resolution_240i,
            (VideoResolution.Resolution_240i, false) => VideoResolution.Resolution_240p,
            (VideoResolution.Resolution_288p, true) => VideoResolution.Resolution_288i,
            (VideoResolution.Resolution_288i, false) => VideoResolution.Resolution_288p,
            (VideoResolution.Resolution_480p, true) => VideoResolution.Resolution_480i,
            (VideoResolution.Resolution_480i, false) => VideoResolution.Resolution_480p,
            (VideoResolution.Resolution_576p, true) => VideoResolution.Resolution_576i,
            (VideoResolution.Resolution_576i, false) => VideoResolution.Resolution_576p,
            (VideoResolution.Resolution_720p, true) => VideoResolution.Resolution_720i,
            (VideoResolution.Resolution_720i, false) => VideoResolution.Resolution_720p,
            (VideoResolution.Resolution_1080p, true) => VideoResolution.Resolution_1080i,
            (VideoResolution.Resolution_1080i, false) => VideoResolution.Resolution_1080p,
            _ => Resolution,
        };
    }

    /// <summary>Whether it's interlaced.</summary>
    [JsonIgnore]
    public bool Interlaced
    {
        get {
            switch(Resolution)
            {
                case VideoResolution.Resolution_1080i:
                case VideoResolution.Resolution_240i:
                case VideoResolution.Resolution_288i:
                case VideoResolution.Resolution_480i:
                case VideoResolution.Resolution_576i:
                case VideoResolution.Resolution_720i:
                    return true;
                default:
                    return false;
            }
        }
    }


    [JsonPropertyName("Codec"), JsonInclude]
    public VideoCodec Codec;

    [JsonPropertyName("Framerate"), JsonInclude]
    public double Framerate;

    [JsonPropertyName("Aspect Ratio"), JsonInclude]
    public AspectRatio AspectRatio;

    [JsonPropertyName("Resolution"), JsonInclude]
    public VideoResolution Resolution;

    [JsonPropertyName("Dynamic Range"), JsonInclude]
    public VideoDynamicRange DynamicRange = VideoDynamicRange.SDR;

    [JsonPropertyName("Color Space"), JsonInclude]
    public VideoColorSpace ColorSpace = VideoColorSpace.BT709;

    /// <summary>
    /// A Dolby Vision enhancement layer: not a picture on its own, but extra data for the base layer video stream
    /// in the same file. Players without Dolby Vision ignore it and play the base layer (usually HDR10).
    /// </summary>
    [JsonPropertyName("Enhancement Layer"), JsonInclude]
    public bool IsEnhancementLayer = false;

}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoCodec
{
    MPEG1,
    MPEG2,
    VC1,
    H264,
    H265
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoResolution
{
    Resolution_240i,
    Resolution_240p,
    Resolution_288i,
    Resolution_288p,
    Resolution_480i,
    Resolution_480p,
    Resolution_576i,
    Resolution_576p,
    Resolution_720i,
    Resolution_720p,
    Resolution_1080i,
    Resolution_1080p,
    Resolution_2160p
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AspectRatio
{
    Aspect_4_3,
    Aspect_16_9
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoDynamicRange
{
    /// <summary>Standard dynamic range: all DVDs and non-UHD Blu-rays.</summary>
    SDR,

    /// <summary>HDR10 (PQ transfer with static metadata).</summary>
    HDR10,

    /// <summary>HDR10 with HDR10+ dynamic metadata.</summary>
    HDR10Plus,

    /// <summary>
    /// Dolby Vision. On UHD Blu-ray this is the enhancement layer stream (see <see cref="VideoStreamInfo.IsEnhancementLayer"/>);
    /// the main video stream it adds to is the HDR10 base layer.
    /// </summary>
    DolbyVision,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum VideoColorSpace
{
    /// <summary>ITU-R BT.601: standard definition (DVD, SD Blu-ray content).</summary>
    BT601,

    /// <summary>ITU-R BT.709: high definition.</summary>
    BT709,

    /// <summary>ITU-R BT.2020: wide colour gamut (UHD Blu-ray).</summary>
    BT2020,
}
