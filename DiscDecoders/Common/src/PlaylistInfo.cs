using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

public class PlaylistInfo
{
    [JsonPropertyName("Name"), JsonInclude]
    public string Name = "";

    [JsonPropertyName("Duration"), JsonInclude]
    public DiscTime Duration;

    [JsonPropertyName("Angles"), JsonInclude]
    public int Angles;

    [JsonPropertyName("Clips"), JsonInclude]
    public List<PlaylistClip> Clips = new();

    [JsonPropertyName("Chapters"), JsonInclude]
    public List<ChapterInfo> Chapters = new();

    /// <summary>The colours the playlist's image subtitles are drawn with (see <see cref="ClipInfo.SubtitlePalette"/>).</summary>
    [JsonPropertyName("Subtitle Palette"), JsonInclude]
    public List<Rgb>? SubtitlePalette = null;

    [JsonPropertyName("Video Streams"), JsonInclude]
    public List<VideoStreamInfo> VideoStreams = new();

    [JsonPropertyName("Audio Streams"), JsonInclude]
    public List<AudioStreamInfo> AudioStreams = new();

    [JsonPropertyName("Subtitle Streams"), JsonInclude]
    public List<SubtitleStreamInfo> SubtitleStreams = new();

    /// <summary>DVD: the still shown when the program chain ends, if any.</summary>
    [JsonPropertyName("Still"), JsonInclude]
    public Still? Still = null;

    /// <summary>DVD: the program chain's details. Null on Blu-ray.</summary>
    [JsonPropertyName("Program Chain"), JsonInclude]
    public DvdProgramChain? ProgramChain = null;
}

public class PlaylistClip
{
    [JsonPropertyName("Filename"), JsonInclude]
    public string Filename = "";

    [JsonPropertyName("Angle"), JsonInclude]
    public int Angle;

    [JsonPropertyName("Start Time"), JsonInclude]
    public DiscTime StartTime;

    [JsonPropertyName("In Time"), JsonInclude]
    public DiscTime InTime;

    [JsonPropertyName("Out Time"), JsonInclude]
    public DiscTime OutTime;

    /// <summary>Blu-ray: the still shown when this clip's play item ends, if any.</summary>
    [JsonPropertyName("Still"), JsonInclude]
    public Still? Still = null;
}

public class ChapterInfo
{
    [JsonPropertyName("Title"), JsonInclude]
    public string? Title = null;

    [JsonPropertyName("Start Time"), JsonInclude]
    public DiscTime StartTime;

    [JsonPropertyName("Stop Time"), JsonInclude]
    public DiscTime StopTime;

    [JsonPropertyName("Index"), JsonInclude]
    public UInt64 Index;

    [JsonPropertyName("Clip Reference"), JsonInclude]
    public string ClipReference = "";
}

/// <summary>A picture held on screen when a playlist or clip finishes, before playback moves on.</summary>
/// <param name="Length">How long it's held, or null to hold it until the viewer moves on.</param>
public sealed record Still(DiscTime? Length);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ProgramPlayback
{
    /// <summary>The programs (chapters) play in order.</summary>
    Sequential,

    /// <summary>Programs are picked at random, possibly repeating.</summary>
    Random,

    /// <summary>The programs play once each in a random order.</summary>
    Shuffle,
}

/// <summary>A DVD playlist's program chain (PGC) details that a Blu-ray playlist has no counterpart for.</summary>
public class DvdProgramChain
{
    /// <summary>How many programs (the DVD's chapter points) it has.</summary>
    [JsonPropertyName("Programs"), JsonInclude]
    public int Programs;

    [JsonPropertyName("Playback"), JsonInclude]
    public ProgramPlayback Playback = ProgramPlayback.Sequential;

    /// <summary>For random or shuffled playback: how many programs are played.</summary>
    [JsonPropertyName("Programs Played"), JsonInclude]
    public int? ProgramsPlayed = null;

    /// <summary>The program chain a menu's "go up" button goes to (0 for none).</summary>
    [JsonPropertyName("Go Up PGC"), JsonInclude]
    public int GoUpPgc;

    /// <summary>For a menu: the language of the menus it's among (its language unit).</summary>
    [JsonPropertyName("Menu Language"), JsonInclude, JsonConverter(typeof(LanguageJsonConverter))]
    public Iso639.Language? MenuLanguage = null;
}
