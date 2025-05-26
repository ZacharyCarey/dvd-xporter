using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

public class AudioStreamInfo : StreamInfo
{
    public override StreamType Type => StreamType.Audio;

    [JsonIgnore]
    public string CodecName
    {
        get
        {
            switch(Codec)
            {
                case AudioCodec.MPEG1: return "MPEG1";
                case AudioCodec.MPEG2: return "MPEG2";
                case AudioCodec.LPCM: return "LPCM";
                case AudioCodec.AC3: return "AC3";
                case AudioCodec.DTS: return "DTS";
                case AudioCodec.TruHD: return "TruHD";
                case AudioCodec.AC3Plus: return "AC3-Plus";
                case AudioCodec.DTSHD: return "DTSHD";
                case AudioCodec.DTSHD_Master: return "DTSHD Master";
                case AudioCodec.AC3Plus_Secondary: return "AC3-Plus Secondary";
                case AudioCodec.DTSHD_Secondary: return "DTSHD Secondary";
                case AudioCodec.SDDS: return "SDDS";
                default:
                    throw new Exception("Invalid audio codec.");
            }
        }
    }

    [JsonPropertyName("Codec"), JsonInclude]
    public AudioCodec Codec;

    [JsonPropertyName("Rate"), JsonInclude]
    public double Rate;

    /// <summary>How many channels, or 0 if the disc only says "multi-channel" (see <see cref="ChannelLayout"/>).</summary>
    [JsonPropertyName("Channel Count"), JsonInclude]
    public uint ChannelCount;

    /// <summary>Blu-ray: the channel layout as the playlist gives it. Null on DVD, which gives <see cref="ChannelCount"/>.</summary>
    [JsonPropertyName("Channel Layout"), JsonInclude]
    public AudioChannelLayout? ChannelLayout = null;
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioCodec
{
    MPEG1,
    MPEG2,
    LPCM,
    AC3,
    DTS,
    TruHD,
    AC3Plus,
    DTSHD,
    DTSHD_Master,
    AC3Plus_Secondary,
    DTSHD_Secondary,
    SDDS
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum AudioChannelLayout
{
    Mono,
    Stereo,
    MultiChannel,

    /// <summary>Stereo and multi-channel versions of the sound in one stream.</summary>
    StereoAndMultiChannel,
}
