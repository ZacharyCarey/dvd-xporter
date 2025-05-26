using DvdXporter.Core.Metadata;

namespace DvdXporter.Core.Online;

/// <summary>
/// Copies downloaded details into a project's info. Unless <see cref="ImportOptions.Overwrite"/> is set,
/// only blank fields are filled, so nothing the user typed is lost. Images are never touched.
/// </summary>
public static class MetadataMerger
{
    public static void Merge(TitleInfo target, TitleInfo source, ImportOptions options)
    {
        var overwrite = options.Overwrite;

        // The user picked this exact title, so its IDs always apply.
        target.Ids.Tmdb = source.Ids.Tmdb ?? target.Ids.Tmdb;
        target.Ids.Tvdb = source.Ids.Tvdb ?? target.Ids.Tvdb;
        target.Ids.Imdb = source.Ids.Imdb ?? target.Ids.Imdb;

        if (options.Details)
        {
            target.Title = Pick(target.Title, source.Title, overwrite) ?? "";
            target.OriginalTitle = Pick(target.OriginalTitle, source.OriginalTitle, overwrite);
            target.SortTitle = Pick(target.SortTitle, source.SortTitle, overwrite);
            target.Year = Pick(target.Year, source.Year, overwrite);
            target.Premiered = Pick(target.Premiered, source.Premiered, overwrite);
            target.Overview = Pick(target.Overview, source.Overview, overwrite);
            target.Tagline = Pick(target.Tagline, source.Tagline, overwrite);
            target.ContentRating = Pick(target.ContentRating, source.ContentRating, overwrite);
            target.Genres = Pick(target.Genres, source.Genres, overwrite);
            target.Studios = Pick(target.Studios, source.Studios, overwrite);
            target.Tags = Pick(target.Tags, source.Tags, overwrite);

            if (target is MovieInfo movie && source is MovieInfo sourceMovie)
            {
                movie.Collection = Pick(movie.Collection, sourceMovie.Collection, overwrite);
                movie.Directors = Pick(movie.Directors, sourceMovie.Directors, overwrite);
                movie.Writers = Pick(movie.Writers, sourceMovie.Writers, overwrite);
            }

            if (target is ShowInfo show && source is ShowInfo sourceShow
                && sourceShow.Status != ShowStatus.Unknown && (overwrite || show.Status == ShowStatus.Unknown))
            {
                show.Status = sourceShow.Status;
            }
        }

        if (options.Episodes && target is ShowInfo targetShow && source is ShowInfo fromShow)
            MergeSeasons(targetShow, fromShow, overwrite);
    }

    private static void MergeSeasons(ShowInfo target, ShowInfo source, bool overwrite)
    {
        foreach (var sourceSeason in source.Seasons)
        {
            var season = target.Seasons.Find(s => s.Number == sourceSeason.Number) ?? target.AddSeason(sourceSeason.Number);
            season.Title = Pick(season.Title, sourceSeason.Title, overwrite);
            season.Overview = Pick(season.Overview, sourceSeason.Overview, overwrite);
            season.Premiered = Pick(season.Premiered, sourceSeason.Premiered, overwrite);

            foreach (var sourceEpisode in sourceSeason.Episodes)
            {
                var episode = season.Episodes.Find(e => e.Number == sourceEpisode.Number) ?? season.AddEpisode(sourceEpisode.Number);
                episode.Title = Pick(episode.Title, sourceEpisode.Title, overwrite) ?? "";
                episode.Overview = Pick(episode.Overview, sourceEpisode.Overview, overwrite);
                episode.AirDate = Pick(episode.AirDate, sourceEpisode.AirDate, overwrite);
                episode.Directors = Pick(episode.Directors, sourceEpisode.Directors, overwrite);
                episode.Writers = Pick(episode.Writers, sourceEpisode.Writers, overwrite);
            }
        }
    }

    private static string? Pick(string? current, string? incoming, bool overwrite) =>
        !string.IsNullOrWhiteSpace(incoming) && (overwrite || string.IsNullOrWhiteSpace(current)) ? incoming : current;

    private static T? Pick<T>(T? current, T? incoming, bool overwrite) where T : struct =>
        incoming is not null && (overwrite || current is null) ? incoming : current;

    private static List<string> Pick(List<string> current, List<string> incoming, bool overwrite) =>
        incoming.Count > 0 && (overwrite || current.Count == 0) ? [.. incoming] : current;
}
