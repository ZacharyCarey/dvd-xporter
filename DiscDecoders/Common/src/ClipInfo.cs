using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

/// <summary>A colour, e.g. one entry of a subtitle palette.</summary>
public readonly record struct Rgb(byte R, byte G, byte B);

public class ClipInfo
{
    [JsonPropertyName("File Name"), JsonInclude]
    public string Filename = "";

    /// <summary>
    /// The colours the clip's image subtitles are drawn with, where the disc gives them apart from the subtitles
    /// (a DVD cell: its program chain's palette, which subtitles pick their colours from); otherwise null.
    /// </summary>
    [JsonPropertyName("Subtitle Palette"), JsonInclude]
    public List<Rgb>? SubtitlePalette = null;

    [JsonPropertyName("Streams"), JsonInclude]
    public List<StreamInfo> Streams = new();

    [JsonPropertyName("Duration"), JsonInclude]
    public DiscTime Duration = DiscTime.Zero;

    /// <summary>
    /// The timestamp the clip's presentation starts at on its own timeline, which <see cref="PlaylistClip.InTime"/> and
    /// <see cref="PlaylistClip.OutTime"/> are on: a Blu-ray clip's is often 10:00; a DVD cell's is 0, as its playlists
    /// time cells from their start. Null if unknown.
    /// </summary>
    [JsonPropertyName("Start"), JsonInclude]
    public DiscTime? Start = null;

    /// <summary>
    /// Where the disc's chapters start in the clip, as offsets from its start, in order: every playlist's chapter marks
    /// that land in it (a chapter mark is a point in a playlist; this is the clip it falls in).
    /// </summary>
    [JsonPropertyName("Chapters"), JsonInclude]
    public List<DiscTime> Chapters = new();

    /// <summary>
    /// What reading the clip through found (see <see cref="ClipScan"/>), which its navigation files don't say. Null if
    /// it couldn't be read.
    /// </summary>
    [JsonPropertyName("Scan"), JsonInclude]
    public ClipScan? Scan = null;
}
