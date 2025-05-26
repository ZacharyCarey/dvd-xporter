using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

/// <summary>The kind of disc. The names are saved in project files and hashed into fingerprints: don't rename them.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscFormat
{
    Dvd,
    Bluray,
}

public class DiscInfo
{
    /// <summary>
    /// The name of the movie loaded from the disc itself.
    /// </summary>
    [JsonPropertyName("Name"), JsonInclude]
    public string? Name = null;

    [JsonPropertyName("Disc Side"), JsonInclude]
    public DiscSide? DiscSide = null;

    /// <summary>
    /// The disc number in the set
    /// </summary>
    [JsonPropertyName("Disc Number"), JsonInclude]
    public int? DiscNumber = null;

    /// <summary>
    /// The number of discs in this set.
    /// For example, the total number of discs within a box set.
    /// </summary>
    [JsonPropertyName("Number of Discs"), JsonInclude]
    public int? NumberOfDiscs = null;

    /// <summary>
    /// All playlists available on the disc. On DVDs playlists are usually referred
    /// to as a 'VTS'
    /// </summary>
    [JsonPropertyName("Playlists"), JsonInclude]
    public List<PlaylistInfo> Playlists = new();   

    /// <summary>
    /// A list of all available clip files present on the disc. These are the raw video files
    /// that can either be played individually, or stitched together in a playlist.
    /// </summary>
    [JsonPropertyName("Clips"), JsonInclude]
    public List<ClipInfo> Clips = new();
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DiscSide
{
    Front,
    Back
}
