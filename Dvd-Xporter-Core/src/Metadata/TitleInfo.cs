using System.Text.Json.Serialization;

namespace DvdXporter.Core.Metadata;

/// <summary>
/// The artwork Emby shows in its menus. Not every kind applies to every item; see <see cref="ImageKinds"/>.
/// </summary>
public enum ImageKind
{
    /// <summary>Main cover art, 2:3 (Emby "Primary"; exported as poster.jpg).</summary>
    Poster,

    /// <summary>Full-screen background, 16:9 (exported as fanart.jpg).</summary>
    Backdrop,

    /// <summary>Wide strip, about 5.4:1 (exported as banner.jpg).</summary>
    Banner,

    /// <summary>Transparent title logo (exported as logo.png).</summary>
    Logo,

    /// <summary>16:9 landscape image; for episodes, the episode thumbnail (exported as landscape.jpg / -thumb.jpg).</summary>
    Thumb,

    /// <summary>Transparent character/title art (exported as clearart.png).</summary>
    ClearArt,

    /// <summary>Transparent round disc image (exported as disc.png).</summary>
    DiscArt,
}

public static class ImageKinds
{
    public static readonly IReadOnlyList<ImageKind> Movie =
        [ImageKind.Poster, ImageKind.Backdrop, ImageKind.Banner, ImageKind.Logo, ImageKind.Thumb, ImageKind.ClearArt, ImageKind.DiscArt];

    public static readonly IReadOnlyList<ImageKind> Show =
        [ImageKind.Poster, ImageKind.Backdrop, ImageKind.Banner, ImageKind.Logo, ImageKind.Thumb, ImageKind.ClearArt];

    public static readonly IReadOnlyList<ImageKind> Season =
        [ImageKind.Poster, ImageKind.Backdrop, ImageKind.Banner, ImageKind.Thumb];

    public static readonly IReadOnlyList<ImageKind> Episode = [ImageKind.Thumb];

    public static readonly IReadOnlyList<ImageKind> Extra = [ImageKind.Thumb];
}

/// <summary>IDs on online metadata sites. Emby uses them to match items and fill in anything left blank.</summary>
public sealed class ProviderIds
{
    public string? Tmdb { get; set; }

    public string? Tvdb { get; set; }

    public string? Imdb { get; set; }
}

/// <summary>
/// Details shared by movies and TV shows. Image values are paths relative to the project folder.
/// </summary>
public abstract class TitleInfo
{
    public string Title { get; set; } = "";

    public string? OriginalTitle { get; set; }

    /// <summary>Title used for sorting, e.g. "Matrix, The".</summary>
    public string? SortTitle { get; set; }

    public int? Year { get; set; }

    /// <summary>Release date (movies) or first air date (shows).</summary>
    public DateOnly? Premiered { get; set; }

    public string? Overview { get; set; }

    public string? Tagline { get; set; }

    /// <summary>e.g. "PG-13" or "TV-14".</summary>
    public string? ContentRating { get; set; }

    public List<string> Genres { get; set; } = [];

    /// <summary>Studios, or networks for TV shows.</summary>
    public List<string> Studios { get; set; } = [];

    public List<string> Tags { get; set; } = [];

    public ProviderIds Ids { get; set; } = new();

    public Dictionary<ImageKind, string> Images { get; set; } = [];

    /// <summary>Bonus videos (featurettes, deleted scenes, ...), which go in type folders beside the movie or show.</summary>
    public List<ExtraInfo> Extras { get; set; } = [];

    /// <summary>
    /// Files that aren't videos (a comic as a PDF, wallpapers, a PC program), copied as they are into a Files folder
    /// beside the movie or show. Emby doesn't show them.
    /// </summary>
    public List<TitleFile> Files { get; set; } = [];
}

/// <summary>
/// One of a movie's or show's files that aren't videos (see <see cref="TitleInfo.Files"/>): a file or folder off one of
/// the project's discs, or a file added to the project from this computer (see <see cref="Projects.ProjectFiles"/>).
/// </summary>
public sealed class TitleFile
{
    /// <summary>The disc's fingerprint, for one off a disc; null for one added to the project.</summary>
    public string? Disc { get; set; }

    /// <summary>Off a disc, its path there: a file's, or a folder's (ending "/"). Added to the project, its name there.</summary>
    public string Path { get; set; } = "";

    [JsonIgnore]
    public bool IsAdded => Disc is null;

    [JsonIgnore]
    public bool IsFolder => DiscDecoders.DiscFiles.IsFolder(Path);

    /// <summary>What it's called in the library: the file's or folder's own name.</summary>
    [JsonIgnore]
    public string Name => Path.TrimEnd('/').Split('/')[^1];
}

/// <summary>A bonus video (or a gallery or file from the disc): what it's called, and which of Emby's extra folders it goes in.</summary>
public sealed class ExtraInfo
{
    /// <summary>Emby's extra types, by the folder each goes in (see <see cref="Type"/>), with display names.</summary>
    public static readonly IReadOnlyList<(string Value, string Label)> Types =
    [
        ("extras", "Extras"),
        ("behind the scenes", "Behind the scenes"),
        ("deleted scenes", "Deleted scenes"),
        ("featurettes", "Featurettes"),
        ("interviews", "Interviews"),
        ("scenes", "Scenes"),
        ("shorts", "Shorts"),
        (TrailersType, "Trailers"),
        (GalleryType, "Gallery (images)"),
    ];

    /// <summary>A folder of pictures (e.g. concept art) saved from a disc's gallery by an Image Gallery node. Emby doesn't read it.</summary>
    public const string GalleryType = "gallery";

    [JsonIgnore]
    public bool IsGallery => Type == GalleryType;

    /// <summary>Emby plays these from a movie's or show's trailer button rather than listing them, so shows no thumbnail.</summary>
    public const string TrailersType = "trailers";

    /// <summary>Whether it can have a thumbnail: not a gallery (no video) or a trailer (see <see cref="TrailersType"/>).</summary>
    [JsonIgnore]
    public bool HasThumbnail => !IsGallery && Type != TrailersType;

    /// <summary>Stays the same when it's renamed, so exports keep pointing at it.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N")[..12];

    public string Name { get; set; } = "";

    /// <summary>One of <see cref="Types"/>' values, e.g. "featurettes".</summary>
    public string Type { get; set; } = "extras";

    [JsonIgnore]
    public string TypeLabel => Types.FirstOrDefault(t => t.Value == Type).Label ?? Type;

    /// <summary>What it is, as Emby shows it under its name (which is its title).</summary>
    public string? Overview { get; set; }

    public DateOnly? Released { get; set; }

    /// <summary>Its thumbnail (see <see cref="ImageKinds.Extra"/>), by its path in the project.</summary>
    public Dictionary<ImageKind, string> Images { get; set; } = [];

    /// <summary>A show's extra: the season it belongs to, or null for the whole show.</summary>
    public int? Season { get; set; }

    /// <summary>With <see cref="Season"/>: the number of the episode it belongs to, or null for the whole season.</summary>
    public int? Episode { get; set; }

    /// <summary>Where it comes among the extras Emby shows, lowest first, or null to go by its name after the numbered ones.</summary>
    public int? SortOrder { get; set; }

    /// <summary>
    /// <paramref name="extras"/> as Emby lists them: by <see cref="SortOrder"/>, then the ones without one by name.
    /// </summary>
    public static List<ExtraInfo> InEmbyOrder(IEnumerable<ExtraInfo> extras) => extras
        .OrderBy(e => e.SortOrder ?? int.MaxValue)
        .ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
        .ToList();
}

public sealed class MovieInfo : TitleInfo
{
    /// <summary>The collection the movie belongs to, e.g. "The Matrix Collection".</summary>
    public string? Collection { get; set; }

    public List<string> Directors { get; set; } = [];

    public List<string> Writers { get; set; } = [];
}

public enum ShowStatus
{
    Unknown,
    Continuing,
    Ended,
}

public sealed class ShowInfo : TitleInfo
{
    public ShowStatus Status { get; set; }

    /// <summary>Kept sorted by number; season 0 holds specials.</summary>
    public List<SeasonInfo> Seasons { get; set; } = [];

    public SeasonInfo AddSeason(int number)
    {
        var season = new SeasonInfo { Number = number };
        Seasons.Add(season);
        SortSeasons();
        return season;
    }

    public int NextSeasonNumber() => Seasons.Count == 0 ? 1 : Math.Max(1, Seasons.Max(s => s.Number) + 1);

    public void SortSeasons() => Seasons.Sort((a, b) => a.Number.CompareTo(b.Number));
}

public sealed class SeasonInfo
{
    public int Number { get; set; }

    /// <summary>Optional name, e.g. "Specials" or a story-arc title.</summary>
    public string? Title { get; set; }

    public string? Overview { get; set; }

    public DateOnly? Premiered { get; set; }

    public Dictionary<ImageKind, string> Images { get; set; } = [];

    /// <summary>Kept sorted by number.</summary>
    public List<EpisodeInfo> Episodes { get; set; } = [];

    [JsonIgnore]
    public string DisplayName => Number == 0 ? "Specials" : $"Season {Number}";

    public EpisodeInfo AddEpisode(int number)
    {
        var episode = new EpisodeInfo { Number = number };
        Episodes.Add(episode);
        SortEpisodes();
        return episode;
    }

    public int NextEpisodeNumber() => Episodes.Count == 0 ? 1 : Episodes.Max(e => e.Number) + 1;

    /// <summary>Adds <paramref name="count"/> episodes numbered on from the last. Returns the first.</summary>
    public EpisodeInfo AddEpisodes(int count)
    {
        var first = AddEpisode(NextEpisodeNumber());
        for (var i = 1; i < count; i++)
            AddEpisode(NextEpisodeNumber());
        return first;
    }

    public void SortEpisodes() => Episodes.Sort((a, b) => a.Number.CompareTo(b.Number));
}

public sealed class EpisodeInfo
{
    public int Number { get; set; }

    public string Title { get; set; } = "";

    public string? Overview { get; set; }

    public DateOnly? AirDate { get; set; }

    public List<string> Directors { get; set; } = [];

    public List<string> Writers { get; set; } = [];

    public Dictionary<ImageKind, string> Images { get; set; } = [];
}
