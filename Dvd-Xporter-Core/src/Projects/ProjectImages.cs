namespace DvdXporter.Core.Projects;

/// <summary>
/// Artwork stored inside the project folder (<c>images/</c>), so it travels with the project when shared.
/// Paths handed out are relative to the project folder, with '/' separators.
/// </summary>
public sealed class ProjectImages(string projectFolderPath)
{
    public const string FolderName = "images";

    public static readonly IReadOnlyList<string> SupportedExtensions = [".jpg", ".jpeg", ".png", ".webp"];

    /// <summary>Copies an image into the project and returns its project-relative path.</summary>
    /// <exception cref="InvalidDataException">The file isn't a supported image type.</exception>
    public string Import(string sourcePath)
    {
        var extension = Path.GetExtension(sourcePath).ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
        {
            throw new InvalidDataException(
                $"\"{Path.GetFileName(sourcePath)}\" isn't a supported image. Use JPEG, PNG or WebP.");
        }

        Directory.CreateDirectory(Path.Combine(projectFolderPath, FolderName));
        var relativePath = $"{FolderName}/{Guid.NewGuid():N}{(extension == ".jpeg" ? ".jpg" : extension)}";
        File.Copy(sourcePath, GetFullPath(relativePath));
        return relativePath;
    }

    /// <summary>Saves downloaded image data into the project and returns its project-relative path.</summary>
    /// <param name="extension">The file type, e.g. ".jpg", usually taken from the download URL.</param>
    public string Import(byte[] data, string extension)
    {
        extension = extension.ToLowerInvariant();
        if (!SupportedExtensions.Contains(extension))
            throw new InvalidDataException($"\"{extension}\" images aren't supported. Use JPEG, PNG or WebP.");

        Directory.CreateDirectory(Path.Combine(projectFolderPath, FolderName));
        var relativePath = $"{FolderName}/{Guid.NewGuid():N}{(extension == ".jpeg" ? ".jpg" : extension)}";
        File.WriteAllBytes(GetFullPath(relativePath), data);
        return relativePath;
    }

    public string GetFullPath(string relativePath) =>
        Path.Combine(projectFolderPath, relativePath.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Deletes an image the project no longer uses. Missing files are ignored.</summary>
    public void Delete(string? relativePath)
    {
        if (string.IsNullOrEmpty(relativePath))
            return;

        var fullPath = GetFullPath(relativePath);
        if (File.Exists(fullPath))
            File.Delete(fullPath);
    }

    /// <summary>Deletes every image in <paramref name="images"/> (e.g. of a season being removed), and empties it.</summary>
    public void DeleteAll<TKey>(Dictionary<TKey, string> images)
        where TKey : notnull
    {
        foreach (var path in images.Values)
            Delete(path);
        images.Clear();
    }
}
