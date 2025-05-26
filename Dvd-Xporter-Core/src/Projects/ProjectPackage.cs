using System.IO.Compression;

namespace DvdXporter.Core.Projects;

/// <summary>
/// A whole project in one file, for sharing and archiving: a ZIP of the project folder, holding the folder itself (its
/// <c>.dvdx</c>, artwork, added files and anything else in it) but not this user's working data (<see cref="ProjectFolder.CacheFileName"/>,
/// <see cref="ProjectFolder.VideoFilesFolderName"/>), which is made again where it's opened. Importing unpacks it.
/// </summary>
public static class ProjectPackage
{
    public const string Extension = ".dvdxp";

    // This user's working data
    private static readonly HashSet<string> LeftOut = new(StringComparer.OrdinalIgnoreCase)
    {
        ProjectFolder.CacheFileName, ProjectFolder.VideoFilesFolderName,
    };

    /// <summary>Writes the project's files to <paramref name="packagePath"/>, replacing any file there.</summary>
    /// <returns>How many files went in.</returns>
    /// <exception cref="IOException">It couldn't be written.</exception>
    public static int Export(ProjectFolder folder, string packagePath)
    {
        var root = folder.FolderPath;
        var top = Path.GetFileName(root);
        var files = Directory.EnumerateFileSystemEntries(root)
            .Where(entry => !LeftOut.Contains(Path.GetFileName(entry)))
            .SelectMany(entry => Directory.Exists(entry)
                ? Directory.EnumerateFiles(entry, "*", SearchOption.AllDirectories)
                : [entry])
            .Where(file => !new FileInfo(file).Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Order(StringComparer.Ordinal)
            .ToList();

        // Written beside it and moved over, so a failed export leaves nothing half-written
        var partial = packagePath + ".partial";
        try
        {
            using (var zip = ZipFile.Open(partial, ZipArchiveMode.Create))
            {
                foreach (var file in files)
                {
                    var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                    zip.CreateEntryFromFile(file, $"{top}/{relative}", CompressionLevel.Optimal);
                }
            }

            File.Move(partial, packagePath, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }

        return files.Count;
    }

    /// <summary>
    /// Unpacks a package into a new folder in <paramref name="parentFolder"/>, named as the project's folder was (with a
    /// number added if that's taken), and checks the project opens.
    /// </summary>
    /// <returns>The project file's path.</returns>
    /// <exception cref="InvalidDataException">It isn't a project package, or the project in it can't be opened.</exception>
    /// <exception cref="IOException">It couldn't be read or unpacked.</exception>
    public static string Import(string packagePath, string parentFolder)
    {
        var name = Path.GetFileName(packagePath);
        using var zip = OpenPackage(packagePath, name);

        // Everything is in one folder, with the project file at its top
        var entries = zip.Entries.Where(e => e.FullName.Length > 0 && !e.FullName.EndsWith('/')).ToList();
        var tops = entries.Select(e => e.FullName.Split('/')[0]).Distinct().ToList();
        if (tops.Count != 1 || entries.Any(e => !IsSafe(e.FullName)))
            throw new InvalidDataException($"\"{name}\" isn't a DVD Xporter project package.");
        var top = tops[0];
        var projectFiles = entries.Where(e => e.FullName.Count(c => c == '/') == 1
            && e.FullName.EndsWith(ProjectFolder.ProjectFileExtension, StringComparison.OrdinalIgnoreCase)).ToList();
        if (projectFiles.Count != 1)
            throw new InvalidDataException($"\"{name}\" isn't a DVD Xporter project package: it should hold one project file.");

        Directory.CreateDirectory(parentFolder);
        var destination = Path.Combine(parentFolder, top);
        for (int number = 2; Directory.Exists(destination) || File.Exists(destination); number++)
            destination = Path.Combine(parentFolder, $"{top} ({number})");

        // Unpacked out of sight first, so a failure leaves no half a project behind
        var unpacking = Path.Combine(parentFolder, $".{top}.importing-{Guid.NewGuid():N}");
        try
        {
            foreach (var entry in entries)
            {
                var path = Path.Combine(unpacking, entry.FullName[(top.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                entry.ExtractToFile(path);
            }

            var projectFile = Path.Combine(unpacking, Path.GetFileName(projectFiles[0].FullName));
            ProjectFolder.Open(projectFile);
            Directory.Move(unpacking, destination);
            return Path.Combine(destination, Path.GetFileName(projectFile));
        }
        catch
        {
            if (Directory.Exists(unpacking))
                Directory.Delete(unpacking, recursive: true);
            throw;
        }
    }

    private static ZipArchive OpenPackage(string path, string name)
    {
        try
        {
            return ZipFile.OpenRead(path);
        }
        catch (InvalidDataException ex)
        {
            throw new InvalidDataException($"\"{name}\" isn't a DVD Xporter project package.", ex);
        }
    }

    // No way out of the folder it's unpacked into
    private static bool IsSafe(string entry) =>
        !entry.StartsWith('/') && !entry.Contains('\\') && !entry.Contains(':')
        && entry.Split('/').All(part => part is not ("" or "." or ".."));
}
