using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

public enum StreamType
{
    Video = 0,
    Audio = 1,
    Subtitle = 2
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "Stream Type")]
[JsonDerivedType(typeof(VideoStreamInfo), "Video")]
[JsonDerivedType(typeof(AudioStreamInfo), "Audio")]
[JsonDerivedType(typeof(SubtitleStreamInfo), "Subtitle")]
public abstract class StreamInfo
{
    /// <summary>
    /// The stream's ID in its clip: the PID on Blu-ray. On DVD, the MPEG stream ID (0xE0 video, 0xC0-0xDF MPEG audio)
    /// or, for streams inside private stream 1, the substream ID (0x80 AC-3, 0x88 DTS, 0xA0 LPCM, 0x20 subtitles, ...).
    /// </summary>
    [JsonPropertyName("Stream ID"), JsonInclude]
    public int StreamId;

    /// <summary>Blu-ray: the sub path the stream comes from, or null if it's in the main path (and always on DVD).</summary>
    [JsonPropertyName("Subpath ID"), JsonInclude]
    public int? SubpathId = null;

    [JsonPropertyName("Duration"), JsonInclude]
    public DiscTime? Duration = null;

    [JsonPropertyName("Language"), JsonInclude, JsonConverter(typeof(LanguageJsonConverter))]
    public Iso639.Language? Language = null;

    [JsonPropertyName("Is Secondary"), JsonInclude]
    public bool IsSecondary = false;

    [JsonIgnore]
    public abstract StreamType Type { get; }
}
