using System.Globalization;
using System.Xml.Linq;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Emby;

/// <summary>One file to put beside the exported videos: an .nfo's text, or artwork copied from the project.</summary>
/// <param name="RelativePath">Its path in the library, with <c>/</c> between folders.</param>
public sealed record MetadataFile(string RelativePath, string? Text = null, string? CopyFrom = null);

/// <summary>
/// The Information step's details and artwork, as the files Emby reads from a library folder: <c>.nfo</c>s (Emby's
/// local metadata, which it prefers to looking things up) and images named for what they are (<c>poster</c>,
/// <c>fanart</c>, ...), for the movie or show, its seasons, its episodes, and its extras.
/// </summary>
public static class EmbyMetadata
{
    /// <summary>The files that go with these exported videos (by their Export node and library path).</summary>
    /// <param name="nfo">Whether to include the .nfo files.</param>
    /// <param name="artwork">Whether to include the artwork.</param>
    public static List<MetadataFile> For(Project project, ProjectImages images, IEnumerable<(CanvasNode Export, string LibraryPath)> exported,
        bool nfo, bool artwork)
    {
        var files = new List<MetadataFile>();
        var list = exported.ToList();
        if (list.Count == 0)
            return files;

        void AddImages(string folder, IReadOnlyDictionary<ImageKind, string> source, IReadOnlyList<ImageKind> kinds, Func<ImageKind, string> name)
        {
            if (!artwork)
                return;
            foreach (var kind in kinds)
            {
                if (source.GetValueOrDefault(kind) is not { Length: > 0 } relative)
                    continue;
                var path = images.GetFullPath(relative);
                if (!File.Exists(path))
                    continue;
                var extension = Path.GetExtension(path).ToLowerInvariant() is ".jpeg" ? ".jpg" : Path.GetExtension(path).ToLowerInvariant();
                files.Add(new MetadataFile($"{Join(folder, name(kind))}{extension}", CopyFrom: path));
            }
        }

        // Extras' files are named after the video, as episodes' are: "Gag Reel.nfo", "Gag Reel-thumb.jpg"
        foreach (var (export, path) in list)
        {
            if (LibraryNaming.FindExtra(new ExportNode(export).Target, project) is not { IsGallery: false } extra)
                continue;
            var stem = path[..path.LastIndexOf('.')];
            if (nfo)
                files.Add(new MetadataFile($"{stem}.nfo", ExtraNfo(extra)));
            if (extra.HasThumbnail)
                AddImages(stem, extra.Images, ImageKinds.Extra, _ => "-thumb");
        }

        // The movie's or show's own folder
        var titleFolder = list[0].LibraryPath.Split('/')[0];
        var movie = project.Movie;
        if (movie is not null)
        {
            if (nfo)
                files.Add(new MetadataFile($"{titleFolder}/movie.nfo", MovieNfo(movie)));
            AddImages(titleFolder, movie.Images, ImageKinds.Movie, ImageName);
            return files;
        }

        var show = project.Show;
        if (show is null)
            return files;
        if (nfo)
            files.Add(new MetadataFile($"{titleFolder}/tvshow.nfo", ShowNfo(show)));
        AddImages(titleFolder, show.Images, ImageKinds.Show, ImageName);

        var seasons = new HashSet<int>();
        foreach (var (export, path) in list)
        {
            if (LibraryNaming.FindEpisode(new ExportNode(export).Target, project) is not var (seasonNumber, episode))
                continue;
            // Not the episode's own folder, if it has one for its extras
            var folder = $"{titleFolder}/{LibraryNaming.SeasonFolder(seasonNumber)}";
            var season = show.Seasons.Find(s => s.Number == seasonNumber);
            if (season is not null && seasons.Add(seasonNumber))
            {
                if (nfo && (season.Title is { Length: > 0 } || season.Overview is { Length: > 0 } || season.Premiered is not null))
                    files.Add(new MetadataFile($"{folder}/season.nfo", SeasonNfo(season)));
                AddImages(folder, season.Images, ImageKinds.Season, ImageName);
            }

            // Episodes' files are named after the video: "Show - S01E01 - Title.nfo", "...-thumb.jpg"
            var stem = path[..path.LastIndexOf('.')];
            if (episode is null)
                continue;
            if (nfo)
                files.Add(new MetadataFile($"{stem}.nfo", EpisodeNfo(show, seasonNumber, episode)));
            AddImages(stem, episode.Images, ImageKinds.Episode, _ => "-thumb");
        }

        return files;
    }

    // An image beside the others in a folder, e.g. "Firefly (2002)/poster"; an episode's (".../Show - S01E01-thumb") follows its name
    private static string Join(string folderOrStem, string name) => name.StartsWith('-') ? folderOrStem + name : $"{folderOrStem}/{name}";

    /// <summary>What Emby calls each kind of image in a folder.</summary>
    public static string ImageName(ImageKind kind) => kind switch
    {
        ImageKind.Poster => "poster",
        ImageKind.Backdrop => "fanart",
        ImageKind.Banner => "banner",
        ImageKind.Logo => "logo",
        ImageKind.Thumb => "landscape",
        ImageKind.ClearArt => "clearart",
        ImageKind.DiscArt => "disc",
        _ => kind.ToString().ToLowerInvariant(),
    };

    // ---- .nfo files ----

    public static string MovieNfo(MovieInfo movie)
    {
        var root = new XElement("movie");
        AddTitleInfo(root, movie);
        var premiered = movie.Premiered;
        if (premiered is not null)
            Add(root, "releasedate", Date(premiered.Value));
        if (movie.Collection is { Length: > 0 } collection)
            root.Add(new XElement("set", new XElement("name", collection)));
        AddAll(root, "director", movie.Directors);
        AddAll(root, "credits", movie.Writers);
        AddIds(root, movie.Ids);
        return Document(root);
    }

    public static string ShowNfo(ShowInfo show)
    {
        var root = new XElement("tvshow");
        AddTitleInfo(root, show);
        if (show.Status != ShowStatus.Unknown)
            Add(root, "status", show.Status.ToString());
        AddIds(root, show.Ids);
        return Document(root);
    }

    public static string SeasonNfo(SeasonInfo season)
    {
        var root = new XElement("season");
        Add(root, "title", season.Title);
        Add(root, "plot", season.Overview);
        Add(root, "premiered", season.Premiered is not null ? Date(season.Premiered.Value) : null);
        Add(root, "seasonnumber", season.Number.ToString(CultureInfo.InvariantCulture));
        return Document(root);
    }

    public static string EpisodeNfo(ShowInfo show, int season, EpisodeInfo episode)
    {
        var root = new XElement("episodedetails");
        Add(root, "title", episode.Title);
        Add(root, "showtitle", show.Title);
        Add(root, "season", season.ToString(CultureInfo.InvariantCulture));
        Add(root, "episode", episode.Number.ToString(CultureInfo.InvariantCulture));
        Add(root, "aired", episode.AirDate is not null ? Date(episode.AirDate.Value) : null);
        Add(root, "plot", episode.Overview);
        AddAll(root, "director", episode.Directors);
        AddAll(root, "credits", episode.Writers);
        return Document(root);
    }

    /// <summary>An extra's details: Emby reads a video's own .nfo as a movie's.</summary>
    public static string ExtraNfo(ExtraInfo extra)
    {
        var root = new XElement("movie");
        Add(root, "title", extra.Name);
        // Emby lists extras by their sort title, which is text, so the number is padded to keep 2 before 10
        AddSortTitle(root, extra.SortOrder?.ToString("000", CultureInfo.InvariantCulture));
        Add(root, "plot", extra.Overview);
        Add(root, "premiered", extra.Released is not null ? Date(extra.Released.Value) : null);
        Add(root, "releasedate", extra.Released is not null ? Date(extra.Released.Value) : null);
        Add(root, "year", extra.Released?.Year.ToString(CultureInfo.InvariantCulture));
        return Document(root);
    }

    private static void AddTitleInfo(XElement root, TitleInfo info)
    {
        Add(root, "title", info.Title);
        Add(root, "originaltitle", info.OriginalTitle);
        AddSortTitle(root, info.SortTitle);
        Add(root, "year", info.Year?.ToString(CultureInfo.InvariantCulture));
        Add(root, "premiered", info.Premiered is not null ? Date(info.Premiered.Value) : null);
        Add(root, "plot", info.Overview);
        Add(root, "tagline", info.Tagline);
        Add(root, "mpaa", info.ContentRating);
        AddAll(root, "genre", info.Genres);
        AddAll(root, "studio", info.Studios);
        AddAll(root, "tag", info.Tags);
    }

    private static void AddIds(XElement root, ProviderIds ids)
    {
        bool first = true;
        foreach (var (type, id) in new[] { ("tmdb", ids.Tmdb), ("imdb", ids.Imdb), ("tvdb", ids.Tvdb) })
        {
            if (string.IsNullOrWhiteSpace(id))
                continue;
            root.Add(new XElement("uniqueid", new XAttribute("type", type), first ? new XAttribute("default", "true") : null, id.Trim()));
            first = false;
        }

        // The older single tags, which Emby reads too
        Add(root, "tmdbid", ids.Tmdb);
        Add(root, "imdbid", ids.Imdb);
        Add(root, "tvdbid", ids.Tvdb);
    }

    /// <summary>
    /// A sort title, locked: Emby otherwise replaces it with one made from the title whenever it refreshes the item.
    /// </summary>
    private static void AddSortTitle(XElement root, string? sortTitle)
    {
        if (string.IsNullOrWhiteSpace(sortTitle))
            return;
        Add(root, "sorttitle", sortTitle);
        Add(root, "lockedfields", "SortName");
    }

    private static void Add(XElement root, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
            root.Add(new XElement(name, value.Trim()));
    }

    private static void AddAll(XElement root, string name, IEnumerable<string> values)
    {
        foreach (var value in values)
            Add(root, name, value);
    }

    private static string Date(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Document(XElement root) =>
        new XDeclaration("1.0", "utf-8", "yes") + Environment.NewLine + root.ToString();
}
