using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Emby;

/// <summary>
/// Where an Export node's file goes in an Emby library, following Emby's naming rules:
/// <c>Movie (Year)/Movie (Year).mkv</c>, <c>Show (Year)/Season 01/Show - S01E01 - Title.mkv</c>, and extras in
/// type folders (<c>Featurettes/</c>, <c>Deleted Scenes/</c>, ...) beside the movie or show, or a show's season or
/// episode they belong to (<c>Season 01/S01E01/Featurettes/</c>, the episode moved into that folder too).
/// </summary>
public static class LibraryNaming
{
    /// <summary>What the export is, e.g. "Movie: Serenity", "S01E02 – The Train Job", "S01E02 › Featurettes: Gag Reel".</summary>
    public static string Describe(CanvasNode export, Project project)
    {
        var target = new ExportNode(export).Target;
        if (target == NodeTypes.MovieTarget)
            return project.Info.Title;
        if (FindEpisode(target, project) is var (season, episode))
        {
            var title = string.IsNullOrWhiteSpace(episode?.Title) ? "" : $" – {episode.Title}";
            return $"S{season:00}E{(episode?.Number ?? 0):00}{title}";
        }

        if (NodeTypes.IsExtraTarget(target))
        {
            var extra = FindExtra(target, project);
            return extra is null ? "Missing extra"
                : $"{Belonging(extra)}{extra.TypeLabel}: {(string.IsNullOrWhiteSpace(extra.Name) ? "Unnamed" : extra.Name.Trim())}";
        }

        return "No library item";
    }

    /// <summary>The title to tag the file with: the movie's, the episode's, or the extra's name.</summary>
    public static string? Title(CanvasNode export, Project project)
    {
        var target = new ExportNode(export).Target;
        if (target == NodeTypes.MovieTarget)
            return project.Info.Title;
        if (FindEpisode(target, project) is (_, { } episode) && !string.IsNullOrWhiteSpace(episode.Title))
            return episode.Title;
        var extra = FindExtra(target, project);
        if (extra is not null && !string.IsNullOrWhiteSpace(extra.Name))
            return extra.Name.Trim();
        return null;
    }

    /// <summary>
    /// The file's path inside the library, with <c>/</c> between folders, or null if the export isn't set up
    /// (no target, or an extra that's been removed or has no name).
    /// </summary>
    public static string? RelativePath(CanvasNode export, Project project, string extension)
    {
        var titleFolder = Safe(project.Info.Year is not null ? $"{project.Info.Title} ({project.Info.Year.Value})" : project.Info.Title);
        var version = new ExportNode(export).Version;
        var versionSuffix = version.Length > 0 ? $" - {Safe(version)}" : "";
        var target = new ExportNode(export).Target;

        if (target == NodeTypes.MovieTarget && project.Movie is not null)
            return $"{titleFolder}/{titleFolder}{versionSuffix}.{extension}";

        if (FindEpisode(target, project) is var (season, episode) && project.Show is not null)
        {
            var number = episode?.Number ?? 0;
            var folder = $"{titleFolder}/{SeasonFolder(season)}";
            // An episode with extras of its own gets a folder, with them beside it in theirs
            if (project.Info.Extras.Any(e => e.Season == season && e.Episode == number))
                folder += $"/{EpisodeFolder(season, number)}";
            var title = string.IsNullOrWhiteSpace(episode?.Title) ? "" : $" - {Safe(episode.Title)}";
            var show = Safe(project.Info.Title);
            return $"{folder}/{show} - S{season:00}E{number:00}{title}{versionSuffix}.{extension}";
        }

        var extra = FindExtra(target, project);
        if (extra is not null && extra.Name.Trim() is { Length: > 0 } name)
        {
            // A gallery is a folder of images, named after it
            return extra.IsGallery
                ? $"{ExtrasHome(extra, project, titleFolder)}/Gallery/{Safe(name)}"
                : $"{ExtrasHome(extra, project, titleFolder)}/{ExtraFolder(extra.Type)}/{Safe(name)}{(extension.Length > 0 ? "." + extension : "")}";
        }

        return null;
    }

    /// <summary>Where one of the title's files goes in the library: its Files folder (see <see cref="TitleInfo.Files"/>).</summary>
    public static string FilePath(TitleFile file, Project project)
    {
        var titleFolder = Safe(project.Info.Year is not null ? $"{project.Info.Title} ({project.Info.Year.Value})" : project.Info.Title);
        return $"{titleFolder}/Files/{Safe(file.Name)}";
    }

    // MP4 keeps a short description (iTunes "desc") and the full one apart ("ldes")
    private const int ShortDescriptionLength = 255;

    /// <summary>
    /// File tags describing the export's library item, from the Information step: release or air date,
    /// overview, genres and people, for an episode its show, season and number, and for an extra its date and
    /// overview (its title is set separately). MKV keeps every tag;
    /// MP4 keeps the ones iTunes has a place for (and its media type), and ffmpeg drops the rest.
    /// </summary>
    public static List<(string Key, string Value)> Tags(CanvasNode export, Project project, string extension)
    {
        var tags = new List<(string, string)>();
        void Add(string key, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                tags.Add((key, value.Trim()));
        }

        void AddOverview(string? overview)
        {
            if (string.IsNullOrWhiteSpace(overview))
                return;
            overview = overview.Trim();
            Add("description", overview.Length <= ShortDescriptionLength ? overview : overview[..(ShortDescriptionLength - 1)].TrimEnd() + "...");
            Add("synopsis", overview);
        }

        var target = new ExportNode(export).Target;
        bool mkv = extension == "mkv";
        var movie = project.Movie;
        var show = project.Show;
        if (target == NodeTypes.MovieTarget && movie is not null)
        {
            Add("date", movie.Premiered?.ToString("yyyy-MM-dd") ?? movie.Year?.ToString());
            AddOverview(movie.Overview);
            Add("genre", string.Join("; ", movie.Genres));
            if (mkv)
            {
                Add("director", string.Join(", ", movie.Directors));
                Add("written_by", string.Join(", ", movie.Writers));
                Add("production_studio", string.Join(", ", movie.Studios));
                Add("law_rating", movie.ContentRating);
            }
            else
            {
                Add("media_type", "9");
            }
        }
        else if (FindEpisode(target, project) is var (season, episode) && show is not null)
        {
            var number = episode?.Number ?? 0;
            Add("show", show.Title);
            Add("season_number", season.ToString());
            Add("episode_sort", number.ToString());
            Add("episode_id", $"S{season:00}E{number:00}");
            Add("date", episode?.AirDate?.ToString("yyyy-MM-dd"));
            AddOverview(episode?.Overview);
            Add("genre", string.Join("; ", show.Genres));
            Add("network", show.Studios.FirstOrDefault());
            if (mkv)
            {
                Add("director", string.Join(", ", episode?.Directors ?? []));
                Add("written_by", string.Join(", ", episode?.Writers ?? []));
                Add("law_rating", show.ContentRating);
            }
            else
            {
                Add("media_type", "10");
            }
        }
        else if (FindExtra(target, project) is { IsGallery: false } extra)
        {
            Add("date", extra.Released?.ToString("yyyy-MM-dd"));
            AddOverview(extra.Overview);
        }

        return tags;
    }

    // Emby recognises these folder names whatever their case
    /// <summary>A season's folder in the show's: "Season 01", or "Season 00" for the specials.</summary>
    public static string SeasonFolder(int season) => $"Season {season:00}";

    private static string EpisodeFolder(int season, int episode) => $"S{season:00}E{episode:00}";

    // Where a show's extra's type folder goes: in the show's folder, a season's, or an episode's
    private static string ExtrasHome(ExtraInfo extra, Project project, string titleFolder)
    {
        var season = extra.Season;
        if (project.Show is null || season is null)
            return titleFolder;
        var seasonFolder = $"{titleFolder}/{SeasonFolder(season.Value)}";
        var episode = extra.Episode;
        return episode is null ? seasonFolder : $"{seasonFolder}/{EpisodeFolder(season.Value, episode.Value)}";
    }

    /// <summary>What a show's extra belongs to, to put before its name: "Season 2 › ", "S02E05 › ", or "" for the whole show.</summary>
    public static string Belonging(ExtraInfo extra)
    {
        var season = extra.Season;
        if (season is null)
            return "";
        var episode = extra.Episode;
        return episode is not null ? $"S{season.Value:00}E{episode.Value:00} › "
            : season.Value == 0 ? "Specials › " : $"Season {season.Value} › ";
    }

    /// <summary>Whether a show's extra belongs to a season or episode that's gone from the show.</summary>
    public static bool BelongsToRemoved(ExtraInfo extra, Project project)
    {
        var show = project.Show;
        if (show is null || extra.Season is null)
            return false;
        var season = show.Seasons.Find(s => s.Number == extra.Season.Value);
        return season is null || (extra.Episode is not null && season.Episodes.All(e => e.Number != extra.Episode.Value));
    }

    private static string ExtraFolder(string type) =>
        string.Join(' ', type.Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    /// <summary>The extra an extra target names, or null if it isn't one (or the extra's been removed).</summary>
    public static ExtraInfo? FindExtra(string target, Project project) => NodeTypes.IsExtraTarget(target)
        ? project.Info.Extras.Find(e => e.Id == target[NodeTypes.ExtraTargetPrefix.Length..])
        : null;

    /// <summary>The season and episode an episode target names; the episode is null if it's gone from the show.</summary>
    public static (int Season, EpisodeInfo? Episode)? FindEpisode(string target, Project project)
    {
        if (!target.StartsWith(NodeTypes.EpisodeTargetPrefix, StringComparison.Ordinal))
            return null;
        var parts = target[NodeTypes.EpisodeTargetPrefix.Length..].Split(':');
        if (parts.Length != 2 || !int.TryParse(parts[0], out var season) || !int.TryParse(parts[1], out var number))
            return null;

        var episode = project.Show?.Seasons.Find(s => s.Number == season)?.Episodes.Find(e => e.Number == number);
        return (season, episode ?? new EpisodeInfo { Number = number });
    }

    private static string Safe(string name) => ProjectFolder.ToSafeFileName(name);
}
