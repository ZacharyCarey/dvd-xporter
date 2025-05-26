using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using static DvdXporter.Core.Online.OnlineJson;

namespace DvdXporter.Core.Online;

/// <summary>
/// TheTVDB (thetvdb.com), through its v4 API. The key logs in for a token that lasts a month; a "user-supported" key
/// also needs the user's subscriber PIN. Has banners and clear art, which TMDB doesn't, but no crew for episodes.
/// </summary>
public sealed class TvdbProvider(HttpClient http, string apiKey, string? pin, string language) : IMetadataProvider, IImageProvider
{
    private const string ApiBase = "https://api4.thetvdb.com/v4/";
    private const string ArtworkBase = "https://artworks.thetvdb.com/";

    // Episodes come 500 to a page, so this is far more than any show needs
    private const int MaxEpisodePages = 40;

    private Task<string>? _token;
    // Each kind of artwork's ID, which TVDB lists rather than documents: (record type, e.g. "series"; name, e.g. "clearlogo")
    private Dictionary<int, (string RecordType, string Name)>? _artworkTypes;

    public string Name => "TVDB";

    /// <summary>TVDB's code for the language, e.g. "eng" for "en-US".</summary>
    private string Language3 { get; } = ThreeLetterLanguage(language);

    /// <summary>The region's code, e.g. "usa" for "en-US"; used to pick content ratings.</summary>
    private string Country3 { get; } = ThreeLetterCountry(language);

    public async Task<string?> TestKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            await TokenAsync(cancellationToken);
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
        var type = kind == ProjectKind.Movie ? "movie" : "series";

        // An IMDb ID ("tt...") is looked up directly.
        if (query.StartsWith("tt", StringComparison.OrdinalIgnoreCase) && query.Length > 2 && query[2..].All(char.IsAsciiDigit))
        {
            var found = await GetAsync($"search/remoteid/{query}", cancellationToken);
            return Array(found, "data")
                .Select(r => Object(r, type))
                .Where(r => r.ValueKind == JsonValueKind.Object)
                .Select(r => new SearchResult(String(r, "id") ?? "", String(r, "name") ?? "", Year(String(r, "year")), null,
                    ImageUrl(String(r, "image"))))
                .ToList();
        }

        var parameters = new List<(string, string)> { ("query", query), ("type", type) };
        if (year is not null)
            parameters.Add(("year", year.Value.ToString(CultureInfo.InvariantCulture)));
        var json = await GetAsync("search", cancellationToken, [.. parameters]);
        return Array(json, "data").Select(ToSearchResult).ToList();
    }

    public async Task<TitleInfo> FetchAsync(ProjectKind kind, string id, ImportOptions options,
        CancellationToken cancellationToken = default) =>
        kind == ProjectKind.Movie
            ? await FetchMovieAsync(id, cancellationToken)
            : await FetchShowAsync(id, options, cancellationToken);

    // Titles have posters, backgrounds, banners, clear logos and clear art; seasons posters, backgrounds and banners;
    // episodes a single screenshot. Landscape thumbnails come from backgrounds, and seasons' also from the show's.
    public bool HasImages(ImageKind kind, int? season, int? episode) =>
        episode is not null
            ? kind == ImageKind.Thumb
            : season is not null
                ? kind is ImageKind.Poster or ImageKind.Backdrop or ImageKind.Banner or ImageKind.Thumb
                : kind is ImageKind.Poster or ImageKind.Backdrop or ImageKind.Banner or ImageKind.Logo or ImageKind.Thumb
                    or ImageKind.ClearArt;

    /// <summary>
    /// Images TVDB has for one kind of artwork, most popular first, in the language or without one (or in any, if
    /// there are none of those).
    /// </summary>
    public async Task<IReadOnlyList<OnlineImage>> GetImagesAsync(ProjectKind kind, ProviderIds ids, int? season, int? episode,
        ImageKind imageKind, CancellationToken cancellationToken = default)
    {
        var tvdbId = ids.Tvdb ?? throw new OnlineMetadataException(
            "This project isn't linked to TVDB yet. Use \"Import Details Online...\" with TVDB, or enter its TVDB ID under Online IDs.");
        if (episode is not null && season is not null)
            return await EpisodeImagesAsync(tvdbId, season.Value, episode.Value, cancellationToken);

        var typeName = imageKind switch
        {
            ImageKind.Poster => "poster",
            ImageKind.Backdrop or ImageKind.Thumb => "background",
            ImageKind.Banner => "banner",
            ImageKind.Logo => "clearlogo",
            ImageKind.ClearArt => "clearart",
            _ => null,
        };
        if (typeName is null)
            return [];

        var types = await ArtworkTypesAsync(cancellationToken);
        var artworks = new List<JsonElement>();
        if (season is not null && imageKind != ImageKind.Thumb)
            artworks.AddRange(OfType(await SeasonArtworksAsync(tvdbId, season.Value, cancellationToken), types, "season", typeName));
        if (season is null || imageKind is ImageKind.Backdrop or ImageKind.Thumb)
        {
            var recordType = kind == ProjectKind.Movie ? "movie" : "series";
            artworks.AddRange(OfType(await TitleArtworksAsync(kind, tvdbId, cancellationToken), types, recordType, typeName));
        }

        var images = artworks
            .Select(a => (Artwork: a, Url: ImageUrl(String(a, "image"))))
            // Godot can't preview SVGs and Emby wants PNG, so skip them.
            .Where(a => a.Url is not null && !a.Url.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
            .Select(a => (
                Image: new OnlineImage(a.Url!, ImageUrl(String(a.Artwork, "thumbnail")) ?? a.Url!, Int(a.Artwork, "width") ?? 0,
                    Int(a.Artwork, "height") ?? 0, String(a.Artwork, "language"), 0),
                HasText: Bool(a.Artwork, "includesText"),
                Score: Double(a.Artwork, "score")))
            .ToList();
        var inLanguage = images.Where(i => i.Image.Language is null || i.Image.Language == Language3).ToList();
        if (inLanguage.Count > 0)
            images = inLanguage;

        // Backgrounds without text suit full-screen backgrounds; ones with a title on them suit landscape thumbnails.
        var ordered = imageKind switch
        {
            ImageKind.Backdrop => images.OrderBy(i => i.HasText ? 1 : 0).ThenByDescending(i => i.Score),
            ImageKind.Thumb => images.OrderBy(i => i.HasText ? 0 : 1).ThenByDescending(i => i.Score),
            _ => images.OrderByDescending(i => i.Score),
        };
        return ordered.Select(i => i.Image).ToList();
    }

    private async Task<MovieInfo> FetchMovieAsync(string id, CancellationToken cancellationToken)
    {
        var json = Object(await GetAsync($"movies/{id}/extended", cancellationToken, ("meta", "translations")), "data");
        var people = Array(json, "characters").ToList();
        var title = String(Translation(json, "nameTranslations"), "name") ?? String(json, "name") ?? "";
        var movie = new MovieInfo
        {
            Title = title,
            OriginalTitle = DifferentFrom(String(json, "name"), title),
            Overview = String(Translation(json, "overviewTranslations"), "overview"),
            Tagline = String(Translation(json, "overviewTranslations"), "tagline") ?? String(Translation(json, "nameTranslations"), "tagline"),
            Premiered = Date(String(Object(json, "first_release"), "date")),
            Year = Year(String(json, "year")),
            Genres = Names(Array(json, "genres")),
            Studios = Names(Array(json, "studios").Concat(Array(Object(json, "companies"), "production"))),
            // Official lists are TVDB's collections
            Collection = Array(json, "lists").Where(l => Bool(l, "isOfficial")).Select(l => String(l, "name")).FirstOrDefault(n => n is not null),
            Directors = PeopleNames(people, "Director"),
            Writers = PeopleNames(people, "Writer"),
            ContentRating = ContentRating(json),
        };
        movie.Ids.Tvdb = id;
        movie.Ids.Imdb = RemoteId(json, "IMDB");
        movie.Ids.Tmdb = RemoteId(json, "TheMovieDB");
        return movie;
    }

    private async Task<ShowInfo> FetchShowAsync(string id, ImportOptions options, CancellationToken cancellationToken)
    {
        var json = Object(await GetAsync($"series/{id}/extended", cancellationToken, ("meta", "translations"), ("short", "true")), "data");
        var title = String(Translation(json, "nameTranslations"), "name") ?? String(json, "name") ?? "";
        var show = new ShowInfo
        {
            Title = title,
            OriginalTitle = DifferentFrom(String(json, "name"), title),
            Overview = String(Translation(json, "overviewTranslations"), "overview") ?? String(json, "overview"),
            Premiered = Date(String(json, "firstAired")),
            Year = Year(String(json, "year")) ?? Year(String(json, "firstAired")),
            Genres = Names(Array(json, "genres")),
            Studios = Names([Object(json, "originalNetwork"), Object(json, "latestNetwork")]),
            Status = String(Object(json, "status"), "name") switch
            {
                "Ended" => ShowStatus.Ended,
                null => ShowStatus.Unknown,
                _ => ShowStatus.Continuing,
            },
            ContentRating = ContentRating(json),
        };
        show.Ids.Tvdb = id;
        show.Ids.Imdb = RemoteId(json, "IMDB");
        show.Ids.Tmdb = RemoteId(json, "TheMovieDB");

        if (!options.Episodes)
            return show;

        foreach (var episodeJson in await EpisodesAsync(id, cancellationToken))
        {
            var seasonNumber = Int(episodeJson, "seasonNumber");
            var number = Int(episodeJson, "number");
            if (seasonNumber is null || number is null || (seasonNumber == 0 && !options.IncludeSpecials))
                continue;

            var season = show.Seasons.Find(s => s.Number == seasonNumber) ?? show.AddSeason(seasonNumber.Value);
            if (season.Episodes.Any(e => e.Number == number))
                continue;

            var episode = season.AddEpisode(number.Value);
            episode.Title = String(episodeJson, "name") ?? "";
            episode.Overview = String(episodeJson, "overview");
            episode.AirDate = Date(String(episodeJson, "aired"));
            // TVDB's seasons have no dates of their own
            if (episode.AirDate is not null && (season.Premiered is null || episode.AirDate < season.Premiered))
                season.Premiered = episode.AirDate;
        }

        return show;
    }

    /// <summary>Every episode in the show's default order (usually as aired), named in the language where TVDB can.</summary>
    private async Task<List<JsonElement>> EpisodesAsync(string id, CancellationToken cancellationToken)
    {
        var episodes = new List<JsonElement>();
        for (var page = 0; page < MaxEpisodePages; page++)
        {
            var json = await GetAsync($"series/{id}/episodes/default/{Language3}", cancellationToken,
                ("page", page.ToString(CultureInfo.InvariantCulture)));
            episodes.AddRange(Array(Object(json, "data"), "episodes"));
            if (String(Object(json, "links"), "next") is null)
                break;
        }

        return episodes;
    }

    private async Task<IReadOnlyList<OnlineImage>> EpisodeImagesAsync(string tvdbId, int season, int episode,
        CancellationToken cancellationToken)
    {
        var json = await GetAsync($"series/{tvdbId}/episodes/default", cancellationToken, ("page", "0"),
            ("season", season.ToString(CultureInfo.InvariantCulture)), ("episodeNumber", episode.ToString(CultureInfo.InvariantCulture)));
        return Array(Object(json, "data"), "episodes")
            .Select(e => ImageUrl(String(e, "image")))
            .OfType<string>()
            .Select(url => new OnlineImage(url, url, 0, 0, null, 0))
            .ToList();
    }

    private async Task<IEnumerable<JsonElement>> TitleArtworksAsync(ProjectKind kind, string tvdbId, CancellationToken cancellationToken)
    {
        var path = kind == ProjectKind.Movie ? $"movies/{tvdbId}/extended" : $"series/{tvdbId}/artworks";
        return Array(Object(await GetAsync(path, cancellationToken), "data"), "artworks");
    }

    /// <summary>The artwork of the show's season, in its default order; none if it has no such season.</summary>
    private async Task<IEnumerable<JsonElement>> SeasonArtworksAsync(string tvdbId, int number, CancellationToken cancellationToken)
    {
        var show = Object(await GetAsync($"series/{tvdbId}/extended", cancellationToken, ("short", "true")), "data");
        var defaultType = Int(show, "defaultSeasonType");
        var seasonId = Array(show, "seasons")
            .Where(s => Int(s, "number") == number)
            .OrderBy(s => Int(Object(s, "type"), "id") == defaultType ? 0 : 1)
            .Select(s => String(s, "id"))
            .FirstOrDefault(s => s is not null);
        if (seasonId is null)
            return [];

        return Array(Object(await GetAsync($"seasons/{seasonId}/extended", cancellationToken), "data"), "artwork");
    }

    private static IEnumerable<JsonElement> OfType(IEnumerable<JsonElement> artworks, Dictionary<int, (string RecordType, string Name)> types,
        string recordType, string typeName) =>
        artworks.Where(a => types.TryGetValue(Int(a, "type") ?? -1, out var t)
            && t.RecordType == recordType && t.Name.StartsWith(typeName, StringComparison.Ordinal));

    private async Task<Dictionary<int, (string RecordType, string Name)>> ArtworkTypesAsync(CancellationToken cancellationToken)
    {
        if (_artworkTypes is null)
        {
            var json = await GetAsync("artwork/types", cancellationToken);
            var types = new Dictionary<int, (string, string)>();
            foreach (var type in Array(json, "data"))
            {
                var id = Int(type, "id");
                if (id is not null)
                {
                    // "ClearLogo" → "clearlogo", "16:9 Screencap" → "screencap"
                    var name = new string((String(type, "name") ?? "").Where(char.IsAsciiLetter).ToArray()).ToLowerInvariant();
                    types[id.Value] = ((String(type, "recordType") ?? "").ToLowerInvariant(), name);
                }
            }

            _artworkTypes = types;
        }

        return _artworkTypes;
    }

    private async Task<JsonElement> GetAsync(string path, CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        var token = await TokenAsync(cancellationToken);
        var query = parameters.Length == 0 ? "" : "?" + string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}{path}{query}");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        return await OnlineJson.GetAsync(http, request, Name, cancellationToken);
    }

    /// <summary>Logs in the first time it's needed; a failed login is tried again next time.</summary>
    private async Task<string> TokenAsync(CancellationToken cancellationToken)
    {
        var login = _token ??= LoginAsync();
        try
        {
            return await login.WaitAsync(cancellationToken);
        }
        catch (OnlineMetadataException)
        {
            if (_token == login)
                _token = null;
            throw;
        }
    }

    // Not cancelled with the request that started it, as others may be waiting for it too
    private async Task<string> LoginAsync()
    {
        var body = new Dictionary<string, string> { ["apikey"] = apiKey };
        if (pin is not null)
            body["pin"] = pin;
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}login") { Content = JsonContent.Create(body) };
        var json = await OnlineJson.GetAsync(http, request, Name, CancellationToken.None);
        return String(Object(json, "data"), "token") ?? throw new OnlineMetadataException($"{Name} didn't send a login token.");
    }

    private SearchResult ToSearchResult(JsonElement result) =>
        new(String(result, "tvdb_id") ?? String(result, "id") ?? "",
            String(Object(result, "translations"), Language3) ?? String(result, "name") ?? "",
            Year(String(result, "year")),
            String(Object(result, "overviews"), Language3) ?? String(result, "overview"),
            ImageUrl(String(result, "thumbnail") ?? String(result, "image_url")));

    /// <summary>A title's own translation into the language (not one of its aliases), if it has one.</summary>
    private JsonElement Translation(JsonElement record, string list) =>
        Array(Object(record, "translations"), list).FirstOrDefault(t => String(t, "language") == Language3 && !Bool(t, "isAlias"));

    private string? ContentRating(JsonElement record) =>
        Array(record, "contentRatings")
            .Where(r => string.Equals(String(r, "country"), Country3, StringComparison.OrdinalIgnoreCase))
            .Select(r => String(r, "name"))
            .FirstOrDefault(r => r is not null);

    /// <summary>The title's ID on another site, by the site's name, e.g. "IMDB".</summary>
    private static string? RemoteId(JsonElement record, string source) =>
        Array(record, "remoteIds")
            .Where(r => String(r, "sourceName")?.Contains(source, StringComparison.OrdinalIgnoreCase) == true)
            .Select(r => String(r, "id"))
            .FirstOrDefault(i => i is not null);

    private static List<string> Names(IEnumerable<JsonElement> items) =>
        items.Select(i => String(i, "name")).OfType<string>().Distinct().ToList();

    private static List<string> PeopleNames(IEnumerable<JsonElement> people, string type) =>
        people.Where(p => String(p, "peopleType") == type).Select(p => String(p, "personName")).OfType<string>().Distinct().ToList();

    // Artwork is usually a whole URL, but older paths are relative to the artwork site
    private static string? ImageUrl(string? path) =>
        path is null || path.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? path : ArtworkBase + path.TrimStart('/');

    private static string? DifferentFrom(string? value, string? other) => value == other ? null : value;

    private static string ThreeLetterLanguage(string language)
    {
        try
        {
            return Iso639.Language.FromPart1(language.Split('-')[0])?.Part2 ?? "eng";
        }
        catch (Exception)
        {
            // Not a language the library knows
            return "eng";
        }
    }

    private static string ThreeLetterCountry(string language)
    {
        var region = language.Contains('-') ? language[(language.IndexOf('-') + 1)..] : "US";
        try
        {
            return new RegionInfo(region).ThreeLetterISORegionName.ToLowerInvariant();
        }
        catch (ArgumentException)
        {
            return "usa";
        }
    }
}
