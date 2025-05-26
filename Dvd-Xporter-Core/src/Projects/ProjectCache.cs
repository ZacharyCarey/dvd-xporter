using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Projects;

/// <summary>
/// This user's machine-specific data for one project (<c>cache.json</c> in the project folder),
/// such as where each disc was found. Never shared; safe to delete, as it is rebuilt as needed.
/// </summary>
public sealed class ProjectCache
{
    private const int CurrentFormatVersion = 1;

    private readonly string _filePath;
    private readonly Lock _lock = new();
    private readonly Dictionary<string, DiscLocation> _discLocations;
    private readonly Dictionary<string, string> _exportSources;

    /// <param name="IsManual">True if the user picked the path, rather than it being found in the Disc Library.</param>
    public sealed record DiscLocation(string Path, bool IsManual);

    private sealed class CacheFile
    {
        public int FormatVersion { get; set; } = CurrentFormatVersion;

        public Dictionary<string, DiscLocation> DiscLocations { get; set; } = [];

        /// <summary>Library path → the alternate picked for it (see <see cref="Transcoding.ExportFile.SourceId"/>).</summary>
        public Dictionary<string, string> ExportSources { get; set; } = [];
    }

    private ProjectCache(string filePath, Dictionary<string, DiscLocation> discLocations, Dictionary<string, string> exportSources)
    {
        _filePath = filePath;
        _discLocations = discLocations;
        _exportSources = exportSources;
    }

    internal static ProjectCache Load(string filePath)
    {
        var file = JsonFile.TryRead<CacheFile>(filePath);
        var current = file?.FormatVersion == CurrentFormatVersion;
        return new ProjectCache(filePath, current ? file!.DiscLocations : [], current ? file!.ExportSources ?? [] : []);
    }

    /// <summary>
    /// Which version this user picked for library items that several releases can make (library path → source),
    /// instead of the best placed release's. A pick is this user's alone: others have other discs.
    /// </summary>
    public Dictionary<string, string> ExportSources()
    {
        lock (_lock)
            return new Dictionary<string, string>(_exportSources);
    }

    /// <summary>Picks the version to export for a library item, or (null) goes back to the best placed release's.</summary>
    public void SetExportSource(string libraryPath, string? sourceId)
    {
        lock (_lock)
        {
            if (sourceId is null)
                _exportSources.Remove(libraryPath);
            else
                _exportSources[libraryPath] = sourceId;
        }
    }

    public DiscLocation? Get(string fingerprint)
    {
        lock (_lock)
            return _discLocations.GetValueOrDefault(fingerprint);
    }

    public void Set(string fingerprint, DiscLocation location)
    {
        lock (_lock)
            _discLocations[fingerprint] = location;
    }

    public void Remove(string fingerprint)
    {
        lock (_lock)
            _discLocations.Remove(fingerprint);
    }

    public void Save()
    {
        CacheFile snapshot;
        lock (_lock)
            snapshot = new CacheFile
            {
                DiscLocations = new Dictionary<string, DiscLocation>(_discLocations),
                ExportSources = new Dictionary<string, string>(_exportSources),
            };
        JsonFile.Write(_filePath, snapshot);
    }
}
