using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

/// <summary>A DVD's files, which are all in its VIDEO_TS folder, by name (e.g. "VTS_01_0.IFO").</summary>
internal static class DvdFiles
{
    private const string Folder = "VIDEO_TS";

    /// <summary>The DVD's files, or null if <paramref name="path"/> doesn't exist.</summary>
    /// <exception cref="InvalidDataException">The image isn't a readable disc image.</exception>
    public static IDiscFileSystem? Open(string path) =>
        File.Exists(path) || Directory.Exists(path) ? DiscFileSystem.Open(path) : null;

    /// <exception cref="FileNotFoundException">The DVD hasn't the file.</exception>
    public static Stream OpenFile(this IDiscFileSystem files, string fileName)
    {
        // The file system's own message doesn't say which file
        if (!files.FileExists($"{Folder}/{fileName}"))
            throw new FileNotFoundException($"{fileName} is missing from the disc's {Folder} folder.", fileName);
        return files.OpenRead($"{Folder}/{fileName}");
    }

    /// <summary>The file's size in bytes, or 0 if the DVD hasn't it.</summary>
    public static long GetFileSize(this IDiscFileSystem files, string fileName) =>
        files.FileExists($"{Folder}/{fileName}") ? files.GetFileLength($"{Folder}/{fileName}") : 0;
}
