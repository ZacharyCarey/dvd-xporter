using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>A file on a disc outside its video structure, e.g. a comic as a PDF in an <c>EXTRAS</c> folder.</summary>
/// <param name="Path">Its path from the disc's root, with <c>/</c> between folders.</param>
public sealed record DiscFile(string Path, long Length);

/// <summary>
/// Paths of a disc's other files (see <see cref="Disc.ListOtherFiles"/>). A path ending in <c>/</c> is a folder of
/// them, and <see cref="AllFiles"/> is all of them; those are copied as a folder.
/// </summary>
public static class DiscFiles
{
    /// <summary>The path that stands for all of a disc's other files.</summary>
    public const string AllFiles = "/";

    // The disc's own structure (and its players' cover pictures), and what rippers and file systems leave behind
    private static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        DiscStructure.DvdFolderName, "AUDIO_TS", "JACKET_P", DiscStructure.BlurayFolderName, "CERTIFICATE", "AACS", "BDSVM",
        "MAKEMKV", "System Volume Information", "$RECYCLE.BIN", ".Trashes", ".fseventsd", ".Spotlight-V100",
    };

    private static readonly HashSet<string> SkippedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "autorun.inf", "desktop.ini", "thumbs.db", ".DS_Store", "discatt.dat", "Desktop DB", "Desktop DF",
    };

    private const int MaxDepth = 8;
    private const int MaxFiles = 5000;

    /// <summary>Whether a path is a folder of files (or all of them), copied as a folder.</summary>
    public static bool IsFolder(string path) => path.EndsWith('/');

    /// <summary>The files a path stands for: the file itself, or those in the folder.</summary>
    public static List<DiscFile> Under(IEnumerable<DiscFile> files, string path) => path == AllFiles ? files.ToList()
        : IsFolder(path) ? files.Where(f => f.Path.StartsWith(path, StringComparison.Ordinal)).ToList()
        : files.Where(f => f.Path == path).ToList();

    /// <summary>The folders the files are in (and the folders those are in), each ending in <c>/</c>, in order.</summary>
    public static List<string> Folders(IEnumerable<DiscFile> files)
    {
        var folders = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in files)
        {
            for (int slash = file.Path.IndexOf('/'); slash >= 0; slash = file.Path.IndexOf('/', slash + 1))
                folders.Add(file.Path[..(slash + 1)]);
        }

        return [.. folders];
    }

    internal static List<DiscFile> List(IDiscFileSystem fs)
    {
        var files = new List<DiscFile>();
        void Visit(string folder, int depth)
        {
            foreach (var name in fs.GetFileNames(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (files.Count >= MaxFiles)
                    return;
                if (SkippedFiles.Contains(name) || name.StartsWith("._", StringComparison.Ordinal))
                    continue;
                var path = Join(folder, name);
                var length = fs.GetFileLength(path);
                if (length > 0)
                    files.Add(new DiscFile(path, length));
            }

            if (depth >= MaxDepth)
                return;
            foreach (var name in fs.GetDirectoryNames(folder).Order(StringComparer.OrdinalIgnoreCase))
            {
                if (!SkippedFolders.Contains(name))
                    Visit(Join(folder, name), depth + 1);
            }
        }

        Visit("", 0);
        return files;
    }

    internal static async Task CopyAsync(IDiscFileSystem fs, string path, string destination, Action<double>? progress,
        CancellationToken cancellation)
    {
        List<(string From, string To)> copies;
        if (IsFolder(path))
        {
            var prefix = path == AllFiles ? "" : path;
            copies = Under(List(fs), path)
                .Select(f => (f.Path, System.IO.Path.Combine(destination, f.Path[prefix.Length..].Replace('/', System.IO.Path.DirectorySeparatorChar))))
                .ToList();
            if (copies.Count == 0)
                throw new FileNotFoundException($"There are no files in \"{path}\" on the disc.");
        }
        else
        {
            copies = [(path, destination)];
        }

        foreach (var (from, _) in copies)
        {
            if (!fs.FileExists(from))
                throw new FileNotFoundException($"\"{from}\" isn't on the disc.");
        }

        var total = copies.Sum(c => fs.GetFileLength(c.From));
        var buffer = new byte[1 << 20];
        long copied = 0;
        foreach (var (from, to) in copies)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(to)!);
            await using var input = fs.OpenRead(from);
            await using var output = File.Create(to);
            int read;
            while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
                copied += read;
                progress?.Invoke(total > 0 ? (double)copied / total : 0);
            }
        }
    }

    private static string Join(string folder, string name) => folder.Length == 0 ? name : $"{folder}/{name}";
}
