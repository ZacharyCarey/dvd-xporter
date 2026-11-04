using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Projects;

/// <summary>
/// A project on disk: a folder holding the main <c>&lt;Name&gt;.dxproj</c> file (shareable project data)
/// and <c>cache.json</c> (this user's machine-specific data).
/// </summary>
public sealed partial class ProjectFolder
{
    public const string ProjectFileExtension = ".dxproj";
    public const string CacheFileName = "cache.json";

    /// <summary>
    /// Video files copied or demuxed out of the discs (see <see cref="Clips.DiscCache"/>).
    /// This user's working data, like <see cref="CacheFileName"/>: left out when the project is exported.
    /// </summary>
    public const string VideoFilesFolderName = "Video Files";

    /// <summary>
    /// The folder of what's been read from the project's discs, one file per disc by fingerprint (see
    /// <see cref="Clips.DiscContents"/>). Part of the project, so it's shared with it: nobody reads a disc again.
    /// </summary>
    public const string DiscInfoFolderName = "Disc Info";

    /// <summary>
    /// The folder of text read off the discs' picture subtitles and corrected (see <see cref="Subtitles.SubtitleTexts"/>).
    /// Part of the project, so the corrections are shared with it.
    /// </summary>
    public const string SubtitleTextFolderName = "Subtitle Text";

    private readonly Dictionary<ProjectDisc, DiscCache> _discCaches = [];

    private ProjectFolder(string projectFilePath, Project project, ProjectCache cache)
    {
        ProjectFilePath = projectFilePath;
        Project = project;
        Cache = cache;
        Images = new ProjectImages(FolderPath);
        Files = new ProjectFiles(FolderPath);
    }

    public string ProjectFilePath { get; }

    public string FolderPath => Path.GetDirectoryName(ProjectFilePath)!;

    public Project Project { get; }

    public ProjectCache Cache { get; }

    public ProjectImages Images { get; }

    public ProjectFiles Files { get; }

    /// <summary>What's been learnt from one of the project's discs: one for each, shared by everything that uses it.</summary>
    public DiscCache GetDiscCache(ProjectDisc disc)
    {
        lock (_discCaches)
        {
            if (!_discCaches.TryGetValue(disc, out var cache))
                _discCaches[disc] = cache = new DiscCache(this, disc);
            return cache;
        }
    }

    /// <summary>
    /// The folder a new project named <paramref name="projectName"/> would be created in.
    /// </summary>
    public static string GetFolderPath(string parentFolder, string projectName) =>
        Path.Combine(parentFolder, ToSafeFileName(projectName));

    /// <exception cref="IOException">The project folder already exists and isn't empty.</exception>
    public static ProjectFolder Create(string parentFolder, Project project)
    {
        var folderPath = GetFolderPath(parentFolder, project.Name);
        if (Directory.Exists(folderPath) && Directory.EnumerateFileSystemEntries(folderPath).Any())
            throw new IOException($"The folder \"{folderPath}\" already exists and isn't empty.");

        Directory.CreateDirectory(folderPath);
        var projectFilePath = Path.Combine(folderPath, ToSafeFileName(project.Name) + ProjectFileExtension);
        var projectFolder = new ProjectFolder(projectFilePath, project,
            ProjectCache.Load(Path.Combine(folderPath, CacheFileName)));
        projectFolder.Save();
        projectFolder.Cache.Save();
        return projectFolder;
    }

    /// <exception cref="InvalidDataException">The file isn't a project this version can read.</exception>
    public static ProjectFolder Open(string projectFilePath)
    {
        var fullPath = Path.GetFullPath(projectFilePath);
        var project = ReadProjectFile(fullPath);
        var cache = ProjectCache.Load(Path.Combine(Path.GetDirectoryName(fullPath)!, CacheFileName));
        return new ProjectFolder(fullPath, project, cache);
    }

    public void Save() => JsonFile.Write(ProjectFilePath, ToFileData(Project));

    /// <summary>
    /// Adds the disc at <paramref name="source"/> to <paramref name="release"/>, remembering where it is if it's on this
    /// computer (one on the render server is found there each time). <paramref name="isManual"/>: it was picked from
    /// outside the Disc Library. What was read from it before it was removed is thrown away, so it's read again, unless
    /// it's still in the project in another release.
    /// </summary>
    public ProjectDisc AddDisc(Release release, DiscSource source, bool isManual)
    {
        var stillHere = Project.FindDisc(source.Fingerprint) is not null;
        var disc = new ProjectDisc(source.Name, source.Format, source.Fingerprint);
        release.Discs.Add(disc);
        if (!stillHere)
            GetDiscCache(disc).Forget();
        if (source.IsHere)
            Cache.Set(disc.Fingerprint, new ProjectCache.DiscLocation(source.Path, isManual));
        return disc;
    }

    /// <summary>Removes the disc from its release, and forgets where it is.</summary>
    public void RemoveDisc(ProjectDisc disc)
    {
        Project.FindRelease(disc)?.Discs.Remove(disc);
        Cache.Remove(disc.Fingerprint);
        ForgetDiscCache(disc);
    }

    /// <summary>Removes the release and its discs, and forgets where they are.</summary>
    public void RemoveRelease(Release release)
    {
        foreach (var disc in release.Discs)
        {
            Cache.Remove(disc.Fingerprint);
            ForgetDiscCache(disc);
        }

        Project.Releases.Remove(release);
    }

    private void ForgetDiscCache(ProjectDisc disc)
    {
        lock (_discCaches)
            _discCaches.Remove(disc);
    }

    /// <summary>
    /// Makes a name safe to use as a file or folder name on any OS, e.g. "Firefly: The Series?" → "Firefly - The Series".
    /// </summary>
    public static string ToSafeFileName(string name)
    {
        var builder = new System.Text.StringBuilder();
        foreach (var c in name)
        {
            switch (c)
            {
                case ':':
                    builder.Append(" - ");
                    break;
                case '/' or '\\' or '|':
                    builder.Append('-');
                    break;
                case '<' or '>' or '"' or '?' or '*':
                    break;
                default:
                    if (!char.IsControl(c))
                        builder.Append(c);
                    break;
            }
        }

        var safe = string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)).TrimEnd('.');
        return safe.Length > 0 ? safe : "Untitled";
    }
}
