using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace DiscDecoders.Common;

/// <summary>
/// Read-only view of a disc's files, whether it is a folder on disk or an ISO image.
/// Paths are relative to the disc root and use '/' separators; "" is the root.
/// </summary>
public interface IDiscFileSystem : IDisposable
{
    /// <summary>The image's volume label, or null for a folder.</summary>
    string? VolumeLabel { get; }

    IEnumerable<string> GetDirectoryNames(string directory);

    IEnumerable<string> GetFileNames(string directory);

    long GetFileLength(string path);

    bool FileExists(string path);

    bool DirectoryExists(string directory);

    Stream OpenRead(string path);
}

public static class DiscFileSystem
{
    /// <summary>A disc's files: <paramref name="path"/> is its root folder or its ISO image.</summary>
    /// <exception cref="InvalidDataException">The image isn't a readable disc image.</exception>
    public static IDiscFileSystem Open(string path) =>
        File.Exists(path) ? new ImageDiscFileSystem(path) : new FolderDiscFileSystem(path);

    /// <summary>The name in <paramref name="names"/> that matches <paramref name="name"/>, ignoring case.</summary>
    public static string? Find(IEnumerable<string> names, string name) =>
        names.FirstOrDefault(n => n.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public sealed class FolderDiscFileSystem(string rootPath) : IDiscFileSystem
{
    public string? VolumeLabel => null;

    public IEnumerable<string> GetDirectoryNames(string directory) =>
        Directory.EnumerateDirectories(ToFullPath(directory)).Select(Path.GetFileName)!;

    public IEnumerable<string> GetFileNames(string directory) =>
        Directory.EnumerateFiles(ToFullPath(directory)).Select(Path.GetFileName)!;

    public long GetFileLength(string path) => new FileInfo(ToFullPath(path)).Length;

    public bool FileExists(string path) => File.Exists(ToFullPath(path));

    public bool DirectoryExists(string directory) => Directory.Exists(ToFullPath(directory));

    public DateTime GetLastWriteTimeUtc(string path) => File.GetLastWriteTimeUtc(ToFullPath(path));

    public Stream OpenRead(string path) => File.OpenRead(ToFullPath(path));

    public void Dispose()
    {
    }

    private string ToFullPath(string path) =>
        Path.Combine(rootPath, path.Replace('/', Path.DirectorySeparatorChar));
}

public sealed class ImageDiscFileSystem : IDiscFileSystem
{
    private readonly Stream _stream;
    private readonly DiscUtils.DiscFileSystem _fileSystem;

    /// <exception cref="InvalidDataException">The image isn't a readable disc image.</exception>
    public ImageDiscFileSystem(string imagePath)
    {
        _stream = File.OpenRead(imagePath);
        try
        {
            // DVD-Video and Blu-ray images are UDF; plain ISO 9660 is a fallback for oddly mastered DVDs.
            if (UdfReader.Detect(_stream))
                _fileSystem = new UdfReader(_stream);
            else if (CDReader.Detect(_stream))
                _fileSystem = new CDReader(_stream, joliet: true);
            else
                throw new InvalidDataException($"\"{Path.GetFileName(imagePath)}\" is not a readable disc image.");
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    public string? VolumeLabel => _fileSystem.VolumeLabel;

    public IEnumerable<string> GetDirectoryNames(string directory) =>
        _fileSystem.GetDirectories(ToImagePath(directory)).Select(EntryName);

    public IEnumerable<string> GetFileNames(string directory) =>
        _fileSystem.GetFiles(ToImagePath(directory)).Select(EntryName);

    public long GetFileLength(string path) => _fileSystem.GetFileLength(ToImagePath(path));

    public bool FileExists(string path) => _fileSystem.FileExists(ToImagePath(path));

    public bool DirectoryExists(string directory) => _fileSystem.DirectoryExists(ToImagePath(directory));

    public Stream OpenRead(string path) => _fileSystem.OpenFile(ToImagePath(path), FileMode.Open, FileAccess.Read);

    public void Dispose()
    {
        (_fileSystem as IDisposable)?.Dispose();
        _stream.Dispose();
    }

    // DiscUtils uses the platform's directory separator for paths inside the image.
    private static string ToImagePath(string path) => path.Replace('/', Path.DirectorySeparatorChar);

    // DiscUtils returns full paths; ISO 9660 names may also carry a ";1" version suffix.
    private static string EntryName(string fullPath)
    {
        var name = fullPath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)[^1];
        var versionIndex = name.IndexOf(';');
        return versionIndex >= 0 ? name[..versionIndex] : name;
    }
}
