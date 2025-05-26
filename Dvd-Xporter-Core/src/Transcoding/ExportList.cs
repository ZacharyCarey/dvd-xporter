using DvdXporter.Core.Emby;
using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

/// <summary>Whether a file can be exported now, and if not, what stops it (the worst reason).</summary>
public enum ExportState
{
    Ready,

    /// <summary>A disc it reads isn't on this computer or the render server.</summary>
    MissingDisc,

    /// <summary>Something on its canvas is wrong (or it has nowhere to go in the library).</summary>
    Error,
}

/// <summary>
/// One file the Export step can make: an Output File (or Image Gallery) on one of the project's canvases, or one of the
/// movie's or show's files that aren't videos (see <see cref="TitleInfo.Files"/>).
/// </summary>
public sealed class ExportFile
{
    /// <summary>The canvas it's made on; null for one of the title's files, which are picked in the Information step.</summary>
    public TranscodeCanvas? Canvas { get; init; }

    /// <summary>How ffmpeg makes it; null for one of the title's files, which are copied as they are.</summary>
    public TranscodeJob? Job { get; init; }

    /// <summary>The title's file it copies, for one.</summary>
    public TitleFile? TitleFile { get; init; }

    /// <summary>What to call it: the library item's name, or the output's.</summary>
    public required string Name { get; init; }

    /// <summary>Its path in the library (see <see cref="LibraryNaming.RelativePath"/>), if it has a place there.</summary>
    public string? LibraryPath { get; init; }

    /// <summary>Whether it makes a folder (an image gallery's, or one copied off a disc) rather than one file.</summary>
    public bool IsFolder { get; init; }

    /// <summary>The discs it reads.</summary>
    public required IReadOnlyCollection<ProjectDisc> Discs { get; init; }

    /// <summary>Tells the file apart from the others: its library path, or where it is if it hasn't one.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// The discs it reads that aren't on this computer or the render server.
    /// </summary>
    public required List<ProjectDisc> MissingDiscs { get; init; }

    /// <summary>The discs it reads that are only on the render server, so they're read through it.</summary>
    public required List<ProjectDisc> ServerDiscs { get; init; }

    /// <summary>Its problems, worst first. Node ids are on <see cref="Canvas"/>'s own nodes; a title's file's have none.</summary>
    public required List<GraphIssue> Issues { get; init; }

    /// <summary>Where it goes on disk, if it's in the library and the library folder is set.</summary>
    public string? Destination { get; init; }

    /// <summary>The library folder it's in, e.g. "Season 01", "Featurettes", or <see cref="ExportList.MovieGroup"/>.</summary>
    public required string Group { get; init; }

    /// <summary>
    /// Which Output File makes it, as "canvas id/output node id" (or "file/", then where the title's file is from): how a pick of one
    /// alternate is remembered.
    /// </summary>
    public required string SourceId { get; init; }

    /// <summary>The release its discs come from (the best placed, if several), or null if it reads no discs.</summary>
    public Release? Release { get; init; }

    /// <summary>
    /// Every file that makes the same library item (same place in the library), best first: the ones from releases
    /// higher in the project's list come first. This file is one of them. Just itself, if there are no others.
    /// </summary>
    public List<ExportFile> Alternates { get; internal set; } = [];

    /// <summary>Whether it's used instead of a better placed alternate because the user picked it.</summary>
    public bool IsPicked { get; internal set; }

    /// <summary>Where it's set up, to point to: "on \"Canvas 1\"", or "in the Information step".</summary>
    public string MadeWhere => Canvas is not null ? $"on \"{Canvas.Name}\"" : "in the Information step";

    /// <summary>What to call it among its alternates, e.g. "Season 5 (Blu-ray)" or the canvas name.</summary>
    public string SourceName => Release?.Name ?? Canvas?.Name ?? "Information";

    public ExportState State =>
        LibraryPath is null || Issues.Any(i => i.Level == IssueLevel.Error) ? ExportState.Error
        : MissingDiscs.Count > 0 ? ExportState.MissingDisc
        : ExportState.Ready;

    public bool CanExport => State == ExportState.Ready;

    /// <summary>Its worst problem's level, or null if it has none.</summary>
    public IssueLevel? WorstLevel => Issues.Count == 0 ? null : Issues.Max(i => i.Level);
}

/// <summary>Every file the project's canvases make, in library order.</summary>
public static class ExportList
{
    public const string MovieGroup = "Movie";
    public const string UnexportedGroup = "Not in the library";

    /// <summary>
    /// The files to export: one per library item. Where several Output Files make the same item (e.g. an episode
    /// from both the Blu-ray and the DVD release), they're alternates, and the best one that can be exported is
    /// used: from the release highest in the project's list, unless <paramref name="picked"/> says otherwise.
    /// Alternates whose discs can't be found are passed over without complaint.
    /// </summary>
    /// <param name="libraryFolder">The Emby library folder, for where each file goes (and whether one's there already).</param>
    /// <param name="picked">The alternate the user chose for some library items: library path → <see cref="ExportFile.SourceId"/>.</param>
    public static List<ExportFile> Build(ProjectTranscodeSources sources, string? libraryFolder, IReadOnlyDictionary<string, string>? picked = null)
    {
        var project = sources.Project;
        var files = new List<ExportFile>();
        foreach (var canvas in project.Canvases.Where(c => c.Kind == CanvasKind.Canvas))
        {
            sources.Canvas = canvas;
            var result = TranscodeCompiler.Compile(canvas, sources, sources);
            foreach (var job in result.Jobs)
            {
                var issues = result.IssuesOf(job).ToList();
                string? destination = null;
                if (job.LibraryPath is null && job.Export is null)
                {
                    issues.Insert(0, new GraphIssue(IssueLevel.Error, job.Output.Id,
                        $"{NodeTypes.Get(job.Output.Type)?.Title ?? "Output File"}: It isn't connected to an Export node, so it has no place in the library."));
                }
                else if (job.LibraryPath is null)
                {
                    // Its Export says what's wrong, as long as it has something to say
                    if (issues.All(i => i.Level != IssueLevel.Error))
                        issues.Insert(0, new GraphIssue(IssueLevel.Error, job.Export!.Id, "Export: Pick the library item this file is for."));
                }
                else
                {
                    destination = DestinationIn(libraryFolder, job.LibraryPath, job.IsFolder, job.Export?.Id, issues);
                }

                files.Add(new ExportFile
                {
                    Canvas = canvas,
                    Job = job,
                    Name = job.Name,
                    LibraryPath = job.LibraryPath,
                    IsFolder = job.IsFolder,
                    Discs = job.Discs,
                    Key = job.LibraryPath ?? $"{canvas.Id}/{job.Output.Id}",
                    MissingDiscs = [.. job.MissingDiscs],
                    ServerDiscs = job.Discs.Where(d => sources.Discs.GetSource(d) is { IsHere: false }).ToList(),
                    Issues = issues,
                    Destination = destination,
                    Group = GroupOf(job.LibraryPath),
                    SourceId = $"{canvas.Id}/{job.Output.Id}",
                    Release = job.Discs.Select(project.FindRelease).OfType<Release>().MinBy(project.Releases.IndexOf),
                });
            }
        }

        // The title's files are picked in the Information step, and copied as they are
        foreach (var titleFile in project.Info.Files)
        {
            var missing = new List<ProjectDisc>();
            var issues = TitleFiles.Check(titleFile, sources, missing);
            var libraryPath = LibraryNaming.FilePath(titleFile, project);
            var disc = TitleFiles.DiscOf(titleFile, project);
            ProjectDisc[] discs = disc is not null ? [disc] : [];
            files.Add(new ExportFile
            {
                TitleFile = titleFile,
                Name = $"File: {titleFile.Name}",
                LibraryPath = libraryPath,
                IsFolder = titleFile.IsFolder,
                Discs = discs,
                Key = libraryPath,
                MissingDiscs = missing,
                ServerDiscs = discs.Where(d => sources.Discs.GetSource(d) is { IsHere: false }).ToList(),
                Issues = issues,
                Destination = DestinationIn(libraryFolder, libraryPath, titleFile.IsFolder, null, issues),
                Group = GroupOf(libraryPath),
                SourceId = $"file/{titleFile.Disc ?? "added"}/{titleFile.Path}",
                Release = disc is not null ? project.FindRelease(disc) : null,
            });
        }

        // Files from one release can't go to the same place; from different releases they're alternates
        foreach (var same in files.Where(f => f.LibraryPath is not null).GroupBy(f => f.LibraryPath))
        {
            foreach (var clash in same.GroupBy(f => f.Release).Where(g => g.Count() > 1))
            {
                foreach (var file in clash)
                {
                    var others = clash.Count() - 1;
                    if (file.TitleFile is not null)
                    {
                        file.Issues.Add(new GraphIssue(IssueLevel.Error, null, $"File: Another file is called \"{file.TitleFile.Name}\" in the Files folder, "
                            + "so one would be copied over the other. Remove one in the Information step."));
                        continue;
                    }

                    var where = string.Join(", ", clash.Where(f => f != file).Select(f => f.MadeWhere).Distinct());
                    var from = file.Release is not null ? $"from \"{file.Release.Name}\" " : "";
                    file.Issues.Add(new GraphIssue(IssueLevel.Error, file.Job?.Export?.Id,
                        $"Export: {(others == 1 ? "Another file" : $"{others} other files")} {from}({where}) {(others == 1 ? "goes" : "go")} to the "
                        + "same place in the library. Give them different library items, or each a version name. (Files from different "
                        + "releases can share one: they're alternates, and the best one you have is used.)"));
                }
            }
        }

        var chosen = new List<ExportFile>();
        foreach (var same in files.GroupBy(f => f.Key))
        {
            // Best first: by the release's place in the project's list (no discs last), then canvas order
            var alternates = same.OrderBy(f => f.Release is not null ? project.Releases.IndexOf(f.Release) : int.MaxValue).ToList();
            foreach (var file in alternates)
                file.Alternates = alternates;
            chosen.Add(Choose(alternates, picked?.GetValueOrDefault(same.Key)));
        }

        files = chosen;

        // Worst first, keeping the order within each level
        foreach (var file in files)
        {
            var sorted = file.Issues.OrderByDescending(i => i.Level).ToList();
            file.Issues.Clear();
            file.Issues.AddRange(sorted);
        }

        return files
            .OrderBy(f => GroupOrder(f.Group))
            .ThenBy(f => f.Group, StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.LibraryPath ?? f.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Which alternate to use: the one picked, if it can be exported; otherwise the best placed one that can be;
    /// otherwise the best placed one with something to fix; otherwise the best placed one, whose disc is missing.
    /// </summary>
    private static ExportFile Choose(List<ExportFile> alternates, string? picked)
    {
        if (alternates.Count == 1)
            return alternates[0];
        var pick = alternates.Find(f => f.SourceId == picked && f.CanExport);
        if (pick is not null)
        {
            pick.IsPicked = pick != alternates.First(f => f.CanExport);
            return pick;
        }

        var file = alternates.Find(f => f.CanExport)
            ?? alternates.Find(f => f.State == ExportState.Error)
            ?? alternates[0];

        // A better placed alternate whose discs are here, but which has problems, is passed over: say so
        foreach (var better in alternates.TakeWhile(f => f != file).Where(f => f.State == ExportState.Error))
        {
            file.Issues.Add(new GraphIssue(IssueLevel.Hint, null,
                $"The version from \"{better.SourceName}\" is preferred, but it has problems to fix ({better.MadeWhere}), so this one is used."));
        }

        return file;
    }

    /// <summary>Where a file goes in the library folder, if it's set; one there already is warned about (as <paramref name="node"/>'s).</summary>
    private static string? DestinationIn(string? libraryFolder, string? libraryPath, bool isFolder, int? node, List<GraphIssue> issues)
    {
        if (string.IsNullOrEmpty(libraryFolder) || libraryPath is null)
            return null;
        var destination = Path.Combine(libraryFolder, libraryPath.Replace('/', Path.DirectorySeparatorChar));
        if (isFolder ? Directory.Exists(destination) : File.Exists(destination))
        {
            issues.Add(new GraphIssue(IssueLevel.Warning, node, isFolder
                ? "There's already a folder there, which exporting will replace."
                : "There's already a file there, which exporting will replace."));
        }

        return destination;
    }

    /// <summary>The folder under the title's folder ("Season 01", "Extras", ...), or the movie itself.</summary>
    private static string GroupOf(string? libraryPath)
    {
        if (libraryPath is null)
            return UnexportedGroup;
        var parts = libraryPath.Split('/');
        return parts.Length <= 2 ? MovieGroup : parts[1];
    }

    // The movie, then seasons and specials, then extras, then what isn't exported
    private static int GroupOrder(string group) => group switch
    {
        MovieGroup => 0,
        UnexportedGroup => 4,
        "Specials" => 2,
        _ when group.StartsWith("Season ", StringComparison.Ordinal) => 1,
        _ => 3,
    };
}
