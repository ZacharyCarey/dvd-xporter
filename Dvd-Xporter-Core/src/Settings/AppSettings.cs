using DvdXporter.Core.Projects;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Settings;

/// <summary>
/// User preferences that persist between runs.
/// </summary>
public sealed class AppSettings
{
    public static AppSettings Current { get; private set; }= new();

    /// <summary>The folder of disc rips that projects find their discs in. Null until the user sets it.</summary>
    public string? DiscLibraryPath { get; set; }

    /// <summary>The folder the last new project was created in, offered as the default next time.</summary>
    public string? LastProjectLocation { get; set; }

    /// <summary>TMDB v3 API key or v4 read access token, for importing details and artwork.</summary>
    public string? TmdbApiKey { get; set; }

    /// <summary>TVDB v4 API key, for importing details and artwork.</summary>
    public string? TvdbApiKey { get; set; }

    /// <summary>The user's TVDB subscriber PIN, which only "user-supported" TVDB keys need.</summary>
    public string? TvdbPin { get; set; }

    /// <summary>OMDb API key, for importing IMDb details.</summary>
    public string? OmdbApiKey { get; set; }

    /// <summary>Language and region for online details, e.g. "en-US" or "en-GB". Null means en-US.</summary>
    public string? MetadataLanguage { get; set; }

    /// <summary>The folder of the Emby library for movies that Export writes into. Null until the user sets it.</summary>
    public string? MoviesLibraryPath { get; set; }

    /// <summary>The folder of the Emby library for TV shows that Export writes into. Null until the user sets it.</summary>
    public string? ShowsLibraryPath { get; set; }

    /// <summary>Whether exports write .nfo files with the Information step's details.</summary>
    public bool ExportNfo { get; set; } = true;

    /// <summary>Whether exports copy the Information step's artwork.</summary>
    public bool ExportArtwork { get; set; } = true;

    /// <summary>
    /// The render server to export with, as "host" or "host:port" (see <see cref="Remote.RenderServerClient"/>); null
    /// for none.
    /// </summary>
    public string? RenderServerAddress { get; set; }

    /// <summary>The render server's access key, which its console shows when it's set up.</summary>
    public string? RenderServerKey { get; set; }

    /// <summary>
    /// Clips shorter than this many seconds count as short (menu loops, logos), and the Clip Renamer can hide them.
    /// </summary>
    public int ShortClipSeconds { get; set; } = DefaultShortClipSeconds;

    public const int DefaultShortClipSeconds = 12;

    /// <summary>
    /// Whether the user has been told Tesseract isn't installed. It's optional (only the Subtitle Text node needs it), so
    /// it's suggested once.
    /// </summary>
    public bool TesseractSuggested { get; set; }

    /// <summary>The Emby library that projects of a kind export into, or null if it isn't set.</summary>
    public string? LibraryPath(ProjectKind kind) => kind == ProjectKind.Movie ? MoviesLibraryPath : ShowsLibraryPath;

    // TODO: Persist the remaining settings once their backend models exist.

    public static void Load(string filePath) => AppSettings.Current = JsonFile.TryRead<AppSettings>(filePath) ?? new AppSettings();

    public static void Save(string filePath) => JsonFile.Write(filePath, AppSettings.Current);
}
