namespace DvdXporter.Core.Projects;

/// <summary>
/// Files added to the project from this computer (<c>files/</c>), e.g. a PDF that isn't on any disc: kept inside the
/// project folder, so they travel with the project when it's shared or sent to the render server. Each is known by its
/// name there.
/// </summary>
public sealed class ProjectFiles(string projectFolderPath)
{
    public const string FolderName = "files";

    private string FolderPath => Path.Combine(projectFolderPath, FolderName);

    /// <summary>
    /// Copies a file into the project, keeping its name (with a number added if another file has it), and returns its
    /// name there. One that's already in the project, or added before, isn't copied again.
    /// </summary>
    public string Import(string sourcePath)
    {
        var name = Path.GetFileName(sourcePath);
        if (string.Equals(Path.GetFullPath(sourcePath), GetFullPath(name), StringComparison.Ordinal))
            return name;

        Directory.CreateDirectory(FolderPath);
        var stem = Path.GetFileNameWithoutExtension(sourcePath);
        var extension = Path.GetExtension(sourcePath);
        for (int number = 2; File.Exists(GetFullPath(name)); number++)
        {
            if (IsSame(sourcePath, GetFullPath(name)))
                return name;
            name = $"{stem} ({number}){extension}";
        }

        File.Copy(sourcePath, GetFullPath(name));
        return name;
    }

    private static bool IsSame(string path, string other)
    {
        if (new FileInfo(path).Length != new FileInfo(other).Length)
            return false;
        using var a = File.OpenRead(path);
        using var b = File.OpenRead(other);
        var bufferA = new byte[1024 * 1024];
        var bufferB = new byte[bufferA.Length];
        int read;
        while ((read = a.ReadAtLeast(bufferA, bufferA.Length, throwOnEndOfStream: false)) > 0)
        {
            b.ReadExactly(bufferB, 0, read);
            if (!bufferA.AsSpan(0, read).SequenceEqual(bufferB.AsSpan(0, read)))
                return false;
        }

        return true;
    }

    public bool Exists(string name) => File.Exists(GetFullPath(name));

    /// <summary>Deletes a file the project no longer uses. One that isn't there is ignored.</summary>
    public void Delete(string name)
    {
        var path = GetFullPath(name);
        if (File.Exists(path))
            File.Delete(path);
    }

    // Only ever a name in the folder: a name from a project file can't reach outside it
    public string GetFullPath(string name) => Path.Combine(FolderPath, Path.GetFileName(name));
}
