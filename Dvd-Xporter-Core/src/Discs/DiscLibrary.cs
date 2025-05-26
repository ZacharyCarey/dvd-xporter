using DiscDecoders;

namespace DvdXporter.Core.Discs;

public sealed record DiscScanError(string Path, string Message);

public sealed record DiscLibraryScanResult(int DiscCount, IReadOnlyList<DiscScanError> Errors);

/// <summary>
/// The user's folder of disc rips. Discs can sit directly in it or in subfolders (e.g. one folder per box set);
/// names don't matter because every disc is identified by fingerprint.
/// </summary>
public sealed class DiscLibrary(DiscIdentifier identifier)
{
    // Deep enough for "Library/Collection/Set/Disc", shallow enough to stay quick on a big drive.
    private const int MaxDepth = 6;

    private volatile IReadOnlyDictionary<string, LocalDiscSource> _discsByFingerprint = new Dictionary<string, LocalDiscSource>();

    public IReadOnlyCollection<LocalDiscSource> Discs => _discsByFingerprint.Values.ToList();

    /// <summary>The folder the current results came from, or null if nothing has been scanned.</summary>
    public string? RootPath { get; private set; }

    public LocalDiscSource? Find(string fingerprint) => _discsByFingerprint.GetValueOrDefault(fingerprint);

    /// <summary>
    /// The folder a library disc sits in, relative to the library root (e.g. "Firefly" or "Shows/Firefly"),
    /// or null if it is directly in the root or outside the library.
    /// </summary>
    public string? GetParentFolder(LocalDiscSource source)
    {
        var root = RootPath;
        var parent = Path.GetDirectoryName(source.Path);
        if (root is null || parent is null)
            return null;

        var relative = Path.GetRelativePath(root, parent);
        return relative == "." || relative.StartsWith("..") || Path.IsPathRooted(relative) ? null : relative;
    }

    public void Clear()
    {
        _discsByFingerprint = new Dictionary<string, LocalDiscSource>();
        RootPath = null;
    }

    /// <summary>
    /// Walks <paramref name="rootPath"/> and fingerprints every disc found. Blocking; run it off the UI thread.
    /// </summary>
    /// <param name="progress">Receives the number of discs found so far.</param>
    public DiscLibraryScanResult Scan(string rootPath, IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
            throw new DirectoryNotFoundException($"The Disc Library folder \"{rootPath}\" doesn't exist.");

        var fullRootPath = Path.GetFullPath(rootPath);
        var found = new Dictionary<string, LocalDiscSource>();
        var errors = new List<DiscScanError>();
        ScanDirectory(fullRootPath, 0, found, errors, progress, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();
        _discsByFingerprint = found;
        RootPath = fullRootPath;
        identifier.Cache.Save();
        return new DiscLibraryScanResult(found.Count, errors);
    }

    private void ScanDirectory(string directory, int depth, Dictionary<string, LocalDiscSource> found,
        List<DiscScanError> errors, IProgress<int>? progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (DiscStructure.IsDiscRootFolder(directory))
        {
            TryAdd(directory, found, errors, progress);
            return;
        }

        List<string> files, subdirectories;
        try
        {
            files = Directory.EnumerateFiles(directory).ToList();
            subdirectories = Directory.EnumerateDirectories(directory).ToList();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            errors.Add(new DiscScanError(directory, ex.Message));
            return;
        }

        foreach (var file in files.Where(f => f.EndsWith(".iso", StringComparison.OrdinalIgnoreCase)).Order())
        {
            cancellationToken.ThrowIfCancellationRequested();
            TryAdd(file, found, errors, progress);
        }

        if (depth >= MaxDepth)
            return;

        foreach (var subdirectory in subdirectories.Order())
        {
            if (!Path.GetFileName(subdirectory).StartsWith('.'))
                ScanDirectory(subdirectory, depth + 1, found, errors, progress, cancellationToken);
        }
    }

    private void TryAdd(string path, Dictionary<string, LocalDiscSource> found, List<DiscScanError> errors, IProgress<int>? progress)
    {
        try
        {
            var source = identifier.Identify(path);
            if (found.TryAdd(source.Fingerprint, source))
                progress?.Report(found.Count);
        }
        catch (DiscIdentificationException ex)
        {
            errors.Add(new DiscScanError(path, ex.Message));
        }
    }
}
