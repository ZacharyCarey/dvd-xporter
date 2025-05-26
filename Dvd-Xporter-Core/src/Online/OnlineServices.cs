namespace DvdXporter.Core.Online;

/// <summary>
/// The online metadata sources the user has keys for, sharing one HTTP client.
/// Call <see cref="Configure"/> whenever the keys change.
/// </summary>
public sealed class OnlineServices : IDisposable
{
    public const string DefaultLanguage = "en-US";

    private readonly HttpClient _http;

    public OnlineServices(HttpMessageHandler? handler = null)
    {
        _http = handler is null ? new HttpClient() : new HttpClient(handler);
        _http.Timeout = TimeSpan.FromSeconds(30);
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("DVD-Xporter/0.1");
    }

    /// <summary>Null when no TMDB key is set.</summary>
    public TmdbProvider? Tmdb { get; private set; }

    /// <summary>Null when no TVDB key is set.</summary>
    public TvdbProvider? Tvdb { get; private set; }

    /// <summary>Null when no OMDb key is set.</summary>
    public OmdbProvider? Omdb { get; private set; }

    /// <summary>The configured sources, TMDB first as it has the most complete data.</summary>
    public IReadOnlyList<IMetadataProvider> Providers => new IMetadataProvider?[] { Tmdb, Tvdb, Omdb }.OfType<IMetadataProvider>().ToList();

    /// <summary>The configured sources of artwork, in the same order.</summary>
    public IReadOnlyList<IImageProvider> ImageProviders => new IImageProvider?[] { Tmdb, Tvdb }.OfType<IImageProvider>().ToList();

    public void Configure(string? tmdbApiKey, string? tvdbApiKey, string? tvdbPin, string? omdbApiKey, string? language)
    {
        Tmdb = CreateTmdb(tmdbApiKey, language);
        Tvdb = CreateTvdb(tvdbApiKey, tvdbPin, language);
        Omdb = CreateOmdb(omdbApiKey);
    }

    /// <summary>A provider for testing a key before it is saved.</summary>
    public TmdbProvider? CreateTmdb(string? apiKey, string? language) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : new TmdbProvider(_http, apiKey.Trim(), Language(language));

    /// <summary>A blank <paramref name="pin"/> is left out, as most keys don't need one.</summary>
    public TvdbProvider? CreateTvdb(string? apiKey, string? pin, string? language) =>
        string.IsNullOrWhiteSpace(apiKey)
            ? null
            : new TvdbProvider(_http, apiKey.Trim(), string.IsNullOrWhiteSpace(pin) ? null : pin.Trim(), Language(language));

    public OmdbProvider? CreateOmdb(string? apiKey) =>
        string.IsNullOrWhiteSpace(apiKey) ? null : new OmdbProvider(_http, apiKey.Trim());

    public async Task<byte[]> DownloadAsync(string url, CancellationToken cancellationToken = default)
    {
        try
        {
            return await _http.GetByteArrayAsync(url, cancellationToken);
        }
        catch (HttpRequestException ex)
        {
            throw new OnlineMetadataException($"Couldn't download the image: {ex.Message}", ex);
        }
    }

    public void Dispose() => _http.Dispose();

    private static string Language(string? language) => string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language.Trim();
}
