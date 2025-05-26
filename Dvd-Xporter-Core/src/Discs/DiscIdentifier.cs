using System.Security.Cryptography;
using System.Text;
using DiscDecoders;
using DiscDecoders.Common;

namespace DvdXporter.Core.Discs;

/// <summary>
/// Turns a path (disc folder or ISO) into a fingerprinted <see cref="LocalDiscSource"/>, using the cache when possible.
/// </summary>
public sealed class DiscIdentifier(FingerprintCache cache)
{
    public FingerprintCache Cache { get; } = cache;

    /// <exception cref="DiscIdentificationException">The path isn't a readable DVD or Blu-ray rip.</exception>
    public LocalDiscSource Identify(string path)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        try
        {
            if (File.Exists(fullPath))
                return IdentifyImage(fullPath);
            if (Directory.Exists(fullPath))
                return IdentifyFolder(fullPath);
        }
        catch (DiscException ex)
        {
            throw new DiscIdentificationException(ex.Message, ex);
        }
        catch (Exception ex) when (ex is not DiscIdentificationException and not OperationCanceledException)
        {
            throw new DiscIdentificationException($"\"{fullPath}\" could not be read: {ex.Message}", ex);
        }

        throw new DiscIdentificationException($"\"{fullPath}\" doesn't exist.");
    }

    private LocalDiscSource IdentifyImage(string imagePath)
    {
        if (!imagePath.EndsWith(".iso", StringComparison.OrdinalIgnoreCase))
            throw new DiscIdentificationException($"\"{Path.GetFileName(imagePath)}\" isn't an ISO disc image.");

        var info = new FileInfo(imagePath);
        var signature = $"{info.Length}:{info.LastWriteTimeUtc.Ticks}";
        if (Cache.TryGet(imagePath, signature, out var cached))
            return ToSource(imagePath, cached);

        using var fs = new ImageDiscFileSystem(imagePath);
        var (format, files) = DiscStructure.FindStructureFiles(fs);
        var fingerprint = DiscStructure.ComputeFingerprint(fs, format, files);
        return Remember(imagePath, new FingerprintCache.Entry(signature, DiscSourceKind.Image, format, fingerprint));
    }

    private LocalDiscSource IdentifyFolder(string folderPath)
    {
        // Picking the VIDEO_TS/BDMV folder itself means the disc root is its parent.
        var root = folderPath;
        var parent = Path.GetDirectoryName(root);
        if (DiscStructure.IsStructureFolderName(Path.GetFileName(root)) && parent is not null)
            root = parent;

        using var fs = new FolderDiscFileSystem(root);
        var (format, files) = DiscStructure.FindStructureFiles(fs);
        var signature = ComputeFolderSignature(fs, files);
        if (Cache.TryGet(root, signature, out var cached))
            return ToSource(root, cached);

        var fingerprint = DiscStructure.ComputeFingerprint(fs, format, files);
        return Remember(root, new FingerprintCache.Entry(signature, DiscSourceKind.Folder, format, fingerprint));
    }

    private static string ComputeFolderSignature(FolderDiscFileSystem fs, IEnumerable<string> files)
    {
        var builder = new StringBuilder();
        foreach (var file in files.Order(StringComparer.Ordinal))
            builder.Append(file).Append(':').Append(fs.GetFileLength(file)).Append(':')
                .Append(fs.GetLastWriteTimeUtc(file).Ticks).Append('\n');
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())))[..16];
    }

    private LocalDiscSource Remember(string path, FingerprintCache.Entry entry)
    {
        Cache.Set(path, entry);
        return ToSource(path, entry);
    }

    private static LocalDiscSource ToSource(string path, FingerprintCache.Entry entry) =>
        new(path, entry.Kind, entry.Format, entry.Fingerprint);
}
