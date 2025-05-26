using DiscDecoders;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// A movie's or show's files that aren't videos (see <see cref="TitleInfo.Files"/>), in the library: each is copied as
/// it is into the Files folder, keeping its name (a folder whole, with what's in it).
/// </summary>
public static class TitleFiles
{
    /// <summary>The project disc it's copied off, if it's off one that's still in the project.</summary>
    public static ProjectDisc? DiscOf(TitleFile file, Project project) => file.IsAdded ? null : project.FindDisc(file.Disc!);

    /// <summary>
    /// What's wrong with it. A disc that can't be found (here or on the render server) goes in <paramref name="missing"/>,
    /// as it can be found later.
    /// </summary>
    public static List<GraphIssue> Check(TitleFile file, ProjectTranscodeSources sources, List<ProjectDisc> missing)
    {
        var issues = new List<GraphIssue>();
        void error(string message) => issues.Add(new GraphIssue(IssueLevel.Error, null, $"File: {message}"));

        if (file.IsAdded)
        {
            if (!sources.Folder.Files.Exists(file.Path))
                error($"\"{file.Path}\" isn't in the project's files any more. Add it again in the Information step.");
            return issues;
        }

        var disc = DiscOf(file, sources.Project);
        if (disc is null)
        {
            error($"\"{file.Path}\" is from a disc that's been removed from the project.");
            return issues;
        }

        var onDisc = sources.GetDiscFiles(disc);
        if (onDisc is null)
            missing.Add(disc);
        else if (DiscFiles.Under(onDisc, file.Path).Count == 0)
            error($"\"{file.Path}\" isn't on \"{disc.Label}\".");
        return issues;
    }

    /// <summary>Copies the file (or folder) to <paramref name="destination"/>.</summary>
    /// <param name="progress">Called with how far it's got, 0–1.</param>
    /// <exception cref="IOException">A file couldn't be read or written, or the disc isn't there.</exception>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    public static async Task CopyAsync(TitleFile file, ProjectTranscodeSources sources, string destination, Action<double> progress,
        CancellationToken cancellation)
    {
        if (file.IsAdded)
        {
            await CopyFileAsync(sources.Folder.Files.GetFullPath(file.Path), destination, progress, cancellation);
            return;
        }

        var disc = DiscOf(file, sources.Project) ?? throw new IOException($"\"{file.Path}\" is from a disc that's been removed from the project.");
        var source = sources.GetSource(disc) ?? throw new IOException($"\"{disc.Label}\" isn't on this computer or the render server.");
        await source.CopyOtherFilesAsync(file.Path, destination, progress, cancellation);
    }

    private static async Task CopyFileAsync(string from, string to, Action<double> progress, CancellationToken cancellation)
    {
        await using var input = File.OpenRead(from);
        await using var output = File.Create(to);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        int read;
        while ((read = await input.ReadAsync(buffer, cancellation)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
            copied += read;
            progress((double)copied / input.Length);
        }
    }
}
