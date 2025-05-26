using System.Text.Json;
using System.Text.RegularExpressions;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;
using static DvdXporter.Core.Online.OnlineJson;

namespace DvdXporter.Core.Online;

/// <summary>
/// IMDb data through OMDb (omdbapi.com), which offers personal API keys; IMDb itself doesn't.
/// OMDb has season episode lists (titles and air dates) but no specials, and only a single poster.
/// </summary>
public sealed partial class OmdbProvider(HttpClient http, string apiKey) : IMetadataProvider
{
    private const string ApiBase = "https://www.omdbapi.com/";

    public string Name => "IMDb (OMDb)";

    public async Task<string?> TestKeyAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // The Matrix: a lookup that always exists.
            await GetAsync(cancellationToken, ("i", "tt0133093"));
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
        if (ImdbIdPattern().IsMatch(query))
        {
            var title = await GetAsync(cancellationToken, ("i", query));
            return [ToSearchResult(title)];
        }

        var parameters = new List<(string, string)> { ("s", query), ("type", kind == ProjectKind.Movie ? "movie" : "series") };
        if (year is not null)
            parameters.Add(("y", year.Value.ToString()));

        try
        {
            var json = await GetAsync(cancellationToken, [.. parameters]);
            return Array(json, "Search").Select(ToSearchResult).ToList();
        }
        catch (OnlineMetadataException ex) when (ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            return [];
        }
    }

    public async Task<TitleInfo> FetchAsync(ProjectKind kind, string id, ImportOptions options,
        CancellationToken cancellationToken = default)
    {
        var json = await GetAsync(cancellationToken, ("i", id), ("plot", "full"));
        TitleInfo info = kind == ProjectKind.Movie
            ? new MovieInfo
            {
                Directors = People(String(json, "Director")),
                Writers = People(String(json, "Writer")),
                Studios = List(String(json, "Production")),
            }
            : new ShowInfo();

        info.Title = String(json, "Title") ?? "";
        info.Year = Year(String(json, "Year"));
        info.Premiered = Date(String(json, "Released"));
        info.ContentRating = String(json, "Rated");
        info.Overview = String(json, "Plot");
        info.Genres = List(String(json, "Genre"));
        info.Ids.Imdb = String(json, "imdbID") ?? id;

        var seasonCount = Int(json, "totalSeasons");
        if (info is ShowInfo show && options.Episodes && seasonCount is not null)
        {
            for (var number = 1; number <= seasonCount.Value; number++)
            {
                var seasonJson = await GetAsync(cancellationToken, ("i", id), ("Season", number.ToString()));
                var season = show.AddSeason(number);
                foreach (var episodeJson in Array(seasonJson, "Episodes"))
                {
                    var episodeNumber = Int(episodeJson, "Episode");
                    if (episodeNumber is null || season.Episodes.Any(e => e.Number == episodeNumber.Value))
                        continue;

                    var episode = season.AddEpisode(episodeNumber.Value);
                    episode.Title = String(episodeJson, "Title") ?? "";
                    episode.AirDate = Date(String(episodeJson, "Released"));
                }
            }
        }

        return info;
    }

    private async Task<JsonElement> GetAsync(CancellationToken cancellationToken, params (string Name, string Value)[] parameters)
    {
        var query = string.Join('&', parameters.Select(p => $"{p.Name}={Uri.EscapeDataString(p.Value)}"));
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}?apikey={Uri.EscapeDataString(apiKey)}&{query}");
        var json = await OnlineJson.GetAsync(http, request, Name, cancellationToken);

        // OMDb reports most problems with HTTP 200 and Response=False.
        if (String(json, "Response") == "False")
            throw new OnlineMetadataException($"{Name}: {String(json, "Error") ?? "the request failed."}");
        return json;
    }

    private static SearchResult ToSearchResult(JsonElement result) =>
        new(String(result, "imdbID") ?? "", String(result, "Title") ?? "", Year(String(result, "Year")), String(result, "Plot"),
            String(result, "Poster"));

    private static List<string> List(string? commaSeparated) =>
        commaSeparated?.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct().ToList() ?? [];

    // "Joss Whedon (creator), Tim Minear (screenplay)" → ["Joss Whedon", "Tim Minear"]
    private static List<string> People(string? commaSeparated) =>
        List(commaSeparated).Select(p => ParentheticalPattern().Replace(p, "").Trim()).Where(p => p.Length > 0).Distinct().ToList();

    [GeneratedRegex(@"^tt\d+$", RegexOptions.IgnoreCase)]
    private static partial Regex ImdbIdPattern();

    [GeneratedRegex(@"\s*\([^)]*\)")]
    private static partial Regex ParentheticalPattern();
}
