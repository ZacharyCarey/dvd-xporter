using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>
/// Reading a Blu-ray's files, by their path from the disc's root (e.g. "BDMV/PLAYLIST/00001.mpls"), where a missing
/// file or folder is no error: navigation files are often optional, or have a backup copy.
/// </summary>
internal static class BlurayFiles
{
    /// <summary>Opens the folder holding BDMV, or the image.</summary>
    /// <exception cref="BlurayException">It doesn't exist, or the image isn't readable.</exception>
    public static IDiscFileSystem Open(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new BlurayException($"\"{path}\" doesn't exist.");

        try
        {
            return DiscFileSystem.Open(path);
        }
        catch (Exception ex)
        {
            throw new BlurayException($"\"{path}\" couldn't be opened as a Blu-ray image: {ex.Message}", ex);
        }
    }

    /// <summary>A file's contents, or null if it isn't there. AVCHD discs' short names (INDEX.BDM, 00001.MPL) are found too.</summary>
    /// <exception cref="IOException">It couldn't be read.</exception>
    public static byte[]? ReadFile(this IDiscFileSystem files, string path) 
    {
        var file = ReadExactFile(files, path);
        if (file is not null)
            return file;

        var avchd = AvchdName(path);
        return (avchd is not null) ? ReadExactFile(files, avchd) : null;
    }

    /// <summary>A file opened for reading, or null if it isn't there.</summary>
    /// <exception cref="IOException">It couldn't be opened.</exception>
    public static Stream? TryOpen(this IDiscFileSystem files, string path) => files.FileExists(path) ? files.OpenRead(path) : null;

    /// <summary>The names of the files in a folder; none if it isn't there.</summary>
    /// <exception cref="IOException">It couldn't be listed.</exception>
    public static IReadOnlyList<string> ListFiles(this IDiscFileSystem files, string folder) =>
        files.DirectoryExists(folder) ? files.GetFileNames(folder).ToList() : [];

    private static byte[]? ReadExactFile(IDiscFileSystem files, string path)
    {
        using var stream = files.TryOpen(path);
        if (stream is null)
            return null;
        var data = new byte[stream.Length];
        stream.ReadExactly(data);
        return data;
    }

    private static readonly Dictionary<string, string> AvchdExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        [".mpls"] = ".MPL",
        [".clpi"] = ".CPI",
        [".m2ts"] = ".MTS",
        [".bdmv"] = ".BDM",
    };

    // AVCHD camcorders write 8.3 names: an upper-case name of up to 8 characters and a 3-letter extension
    private static string? AvchdName(string path)
    {
        if (!AvchdExtensions.TryGetValue(Path.GetExtension(path), out var extension))
            return null;
        var name = Path.GetFileNameWithoutExtension(path).ToUpperInvariant();
        var folder = path[..(path.LastIndexOf('/') + 1)];
        return $"{folder}{name[..Math.Min(8, name.Length)]}{extension}";
    }
}
