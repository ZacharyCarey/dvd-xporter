using System.Text.Json;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using static DvdXporter.Core.Online.OnlineJson;

namespace DvdXporter.Core.Online;

/// <summary>
/// The Movie Database (themoviedb.org). Accepts either a v3 API key or a v4 read access token.
/// </summary>
public sealed class TmdbProvider(HttpClient http, string apiKey, string language) : IMetadataProvider, IImageProvider
{
    private const string ApiBase = "https://api.themoviedb.org/3/";
    private const string ImageBase = "https://image.tmdb.org/t/p/";

    private static readonly string[] WritingJobs = ["Writer", "Screenplay", "Teleplay", "Story", "Novel", "Author"];

    public string Name => "TMDB";

    /// <summary>"US" from "en-US"; used to pick content ratings.</summary>
    private string Region => language.Contains('-') ? language[(language.IndexOf('-') + 1)..].ToUpperInvariant() : "US";

    private string ImageLanguage => language.Split('-')[0];

    public async Task<string?> TestKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await GetAsync("configuration", cancellationToken);
            return null;
        }
        catch (OnlineMetadataException ex)
        {
            return ex.Message;
        }
    }

    public async Task<IReadOnlyList<SearchResult>> SearchAsync(ProjectKind kind, string query, int? year,
        CancellationToken cancellationToken = default)
    {
        query = query.Trim();
        var isTv = kind == ProjectKind.TvShow;

        // An IMDb ID ("tt...") is looked up directly.
        if (query.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && query.Length > 2 && query[2..].All(char.IsAsciiDigit))
        {
            var found = await GetAsync($"find/{query}", cancellationToken, ("external_source", "imdb_id"));
            return Array(found, isTv ? "tv_results" : "movie_results").Select(r => ToSearchResult(r, isTv)).ToList();
        }

        var parameters = new List<(string, string)> { ("query", query) };
        if (year is not null)
            parameters.Add((isTv ? "first_air_date_year" : "year", year.Value.ToString()));
        var json = await GetAsync(isTv ? "search/tv" : "search/movie", cancellationToken, [.. parameters]);
        return Array(json, "results").Select(r => ToSearchResult(r, isTv)).ToList();
    }

    public async Task<TitleInfo> FetchAsync(ProjectKind kind, string id, ImportOptions options,
        CancellationToken cancellationToken = default) =>
        kind == ProjectKind.Movie
            ? await FetchMovieAsync(id, cancellationToken)
            : await FetchShowAsync(id, options, cancellationToken);

    // TMDB has posters, backdrops and logos for titles, posters for seasons, and stills for episodes.
    // Landscape thumbnails come from backdrops, and season backdrops from the show's.
    public bool HasImages(ImageKind kind, int? season, int? episode) =>
        episode is not null
            ? kind == ImageKind.Thumb
            : season is not null
                ? kind is ImageKind.Poster or ImageKind.Backdrop or ImageKind.Thumb
                : kind is ImageKind.Poster or ImageKind.Backdrop or ImageKind.Thumb or ImageKind.Logo;

    /// <summary>
    /// Images TMDB has for one kind of artwork, best rated first. TMDB has no banners, clear art or disc art,
    /// so those return nothing. Seasons only have posters (their backdrops fall back to the show's), and episodes only stills.
    /// </summary>
    public async Task<IReadOnlyList<OnlineImage>> GetImagesAsync(ProjectKind kind, ProviderIds ids, int? season, int? episode,
        ImageKind imageKind, CancellationToken cancellationToken = default)
    {
        var tmdbId = ids.Tmdb ?? throw new OnlineMetadataException(
            "This project isn't linked to TMDB yet. Use \"Import Details Online...\" with TMDB, or enter its TMDB ID under Online IDs.");
        var languages = ("include_image_language", $"{ImageLanguage},null");
        string path;
        string arrayName;
        if (episode is not null && season is not null)
        {
            if (imageKind != ImageKind.Thumb)
                return [];
            (path, arrayName) = ($"tv/{tmdbId}/season/{season.Value}/episode/{episode.Value}/images", "stills");
        }
        else if (season is not null && imageKind == ImageKind.Poster)
        {
            (path, arrayName) = ($"tv/{tmdbId}/season/{season.Value}/images", "posters");
        }
        else
        {
            arrayName = imageKind switch
            {
                ImageKind.Poster => "posters",
                ImageKind.Backdrop or ImageKind.Thumb => "backdrops",
                ImageKind.Logo => "logos",
                _ => "",
            };
            if (arrayName.Length == 0)
                return [];
            path = $"{(kind == ProjectKind.Movie ? "movie" : "tv")}/{tmdbId}/images";
        }

        var json = await GetAsync(path, cancellationToken, languages);
        var thumbnailSize = arrayName == "posters" ? "w342" : "w300";
        var images = Array(json, arrayName)
            .Select(i => (Path: String(i, "file_path"), Image: i))
            // Godot can't preview SVG logos and Emby wants PNG, so skip them.
            .Where(i => i.Path is not null && !i.Path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Select(i => new OnlineImage($"{ImageBase}original{i.Path}", $"{ImageBase}{thumbnailSize}{i.Path}",
                Int(i.Image, "width") ?? 0, Int(i.Image, "height") ?? 0, String(i.Image, "iso_639_1"), Double(i.Image, "vote_average")));

        // Backdrops without text suit full-screen backgrounds; ones with a title on them suit landscape thumbnails.
        return imageKind switch
        {
            ImageKind.Backdrop => images.OrderBy(i => i.Language is null ? 0 : 1).ThenByDescending(i => i.Rating).ToList(),
            ImageKind.Thumb when episode is null => images.OrderBy(i => i.Language is null ? 1 : 0).ThenByDescending(i => i.Rating).ToList(),
            _ => images.OrderByDescending(i => i.Rating).ToList(),
        };
    }

    private async Task<MovieInfo> FetchMovieAsync(string id, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"movie/{id}", cancellationToken, ("append_to_response", "release_dates,credits,keywords"));
        var crew = Array(Object(json, "credits"), "crew").ToList();
        var movie = new MovieInfo
        {
            Title = String(json, "title") ?? "",
            OriginalTitle = DifferentFrom(String(json, "original_title"), String(json, "title")),
            Overview = String(json, "overview"),
            Tagline = String(json, "tagline"),
            Premiered = Date(String(json, "release_date")),
            Year = Year(String(json, "release_date")),
            Genres = Names(Array(json, "genres")),
            Studios = Names(Array(json, "production_companies")),
            Tags = Names(Array(Object(json, "keywords"), "keywords")),
            Collection = String(Object(json, "belongs_to_collection"), "name"),
            Directors = CrewNames(crew, job => job == "Director"),
            Writers = CrewNames(crew, job => WritingJobs.Contains(job)),
            ContentRating = Array(Object(json, "release_dates"), "results")
                .Where(r => String(r, "iso_3166_1") == Region)
                .SelectMany(r => Array(r, "release_dates"))
                .Select(r => String(r, "certification"))
                .FirstOrDefault(c => c is not null),
        };
        movie.Ids.Tmdb = id;
        movie.Ids.Imdb = String(json, "imdb_id");
        return movie;
    }

    private async Task<ShowInfo> FetchShowAsync(string id, ImportOptions options, CancellationToken cancellationToken)
    {
        var json = await GetAsync($"tv/{id}", cancellationToken, ("append_to_response", "external_ids,content_ratings,keywords"));
        var show = new ShowInfo
        {
            Title = String(json, "name") ?? "",
            OriginalTitle = DifferentFrom(String(json, "original_name"), String(json, "name")),
            Overview = String(json, "overview"),
            Premiered = Date(String(json, "first_air_date")),
            Year = Year(String(json, "first_air_date")),
            Genres = Names(Array(json, "genres")),
            Studios = Names(Array(json, "networks")),
            Tags = Names(Array(Object(json, "keywords"), "results")),
            Status = String(json, "status") switch
            {
                "Ended" or "Canceled" => ShowStatus.Ended,
                null => ShowStatus.Unknown,
                _ => ShowStatus.Continuing,
            },
            ContentRating = Array(Object(json, "content_ratings"), "results")
                .Where(r => String(r, "iso_3166_1") == Region)
                .Select(r => String(r, "rating"))
                .FirstOrDefault(r => r is not null),
        };
        var externalIds = Object(json, "external_ids");
        show.Ids.Tmdb = id;
        show.Ids.Imdb = String(externalIds, "imdb_id");
        show.Ids.Tvdb = String(externalIds, "tvdb_id");

        if (!options.Episodes)
            return show;

        foreach (var seasonSummary in Array(json, "seasons"))
        {
            var number = Int(seasonSummary, "season_number");
            if (number is null || (number == 0 && !options.IncludeSpecials))
                continue;

            var seasonJson = await GetAsync($"tv/{id}/season/{number}", cancellationToken);
            var season = show.AddSeason(number.Value);
            var name = String(seasonJson, "name");
            season.Title = name == season.DisplayName || name == $"Season {number}" ? null : name;
            season.Overview = String(seasonJson, "overview");
            season.Premiered = Date(String(seasonJson, "air_date"));

            foreach (var episodeJson in Array(seasonJson, "episodes"))
            {
                var episodeNumber = Int(episodeJson, "episode_number");
                if (episodeNumber is null)
                    continue;

                var crew = Array(episodeJson, "crew").ToList();
                var episode = season.AddEpisode(episodeNumber.Value);
                episode.Title = String(episodeJson, "name") ?? "";
                episode.Overview = String(episodeJson, "overview");
                episode.AirDate = Date(String(episodeJson, "air_date"));
                episode.Directors = CrewNames(crew, job => job == "Director");
                episode.Writers = CrewNames(crew, job => WritingJobs.Contains(job));
            }
        }

        return show;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        var query = new List<string> { $"language={Uri.EscapeDataString(language)}" };
        query.AddRange(parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));

        // v4 read access tokens are JWTs and go in a header; v3 keys go in the query string.
        var isAccessToken = apiKey.StartsWith("eyJ", StringComparison.Ordinal);
        if (!isAccessToken)
            query.Add($"api_key={Uri.EscapeDataString(apiKey)}");

        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}?{string.Join('&', query)}");
        if (isAccessToken)
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", apiKey);
        return await OnlineJson.GetAsync(http, request, Name, cancellationToken);
    }

    private static SearchResult ToSearchResult(JsonElement result, bool isTv)
    {
        var poster = String(result, "poster_path");
        return new SearchResult(
            String(result, "id") ?? "",
            String(result, isTv ? "name" : "title") ?? "",
            Year(String(result, isTv ? "first_air_date" : "release_date")),
            String(result, "overview"),
            poster is null ? null : $"{ImageBase}w92{poster}");
    }

    private static List<string> Names(IEnumerable<JsonElement> items) =>
        items.Select(i => String(i, "name")).OfType<string>().Distinct().ToList();

    private static List<string> CrewNames(IEnumerable<JsonElement> crew, Func<string, bool> jobMatches) =>
        crew.Where(c =>
            {
                var job = String(c, "job");
                return job is not null && jobMatches(job);
            })
            .Select(c => String(c, "name")).OfType<string>().Distinct().ToList();

    private static string? DifferentFrom(string? value, string? other) => value == other ? null : value;
}
