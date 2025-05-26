using DiscDecoders;
using DvdXporter.Core.Emby;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

public sealed class ExportOptions
{
    /// <summary>Whether to write .nfo files with the Information step's details.</summary>
    public bool WriteNfo { get; init; } = true;

    /// <summary>Whether to copy the Information step's artwork.</summary>
    public bool CopyArtwork { get; init; } = true;
}

public enum ExportOutcome
{
    Done,
    Failed,
    Cancelled,

    /// <summary>It couldn't be started (it had problems by the time its turn came).</summary>
    Skipped,
}

/// <param name="Error">What went wrong, for a file that failed or was skipped.</param>
/// <param name="LogPath">ffmpeg's log for it, if it got that far.</param>
public sealed record ExportFileResult(ExportFile File, ExportOutcome Outcome, string Destination, string? Error, string? LogPath, TimeSpan Elapsed);

/// <summary>How far an export has got.</summary>
/// <param name="Index">The file being made, from 0.</param>
/// <param name="Stage">What's happening to it, e.g. "Encoding" or "Measuring the loudness".</param>
/// <param name="Fraction">How far through it, 0–1.</param>
/// <param name="Overall">How far through the whole export, 0–1, weighted by length.</param>
public sealed record ExportProgress(int Index, int Count, ExportFile File, string Stage, double Fraction, double Overall);

public sealed class ExportRun
{
    public List<ExportFileResult> Results { get; } = [];

    /// <summary>.nfo and artwork files written.</summary>
    public int MetadataWritten { get; set; }

    /// <summary>.nfo and artwork files that couldn't be written, and why.</summary>
    public List<string> MetadataErrors { get; } = [];

    public bool Cancelled { get; set; }

    public TimeSpan Elapsed { get; set; }
}

/// <summary>
/// Makes exported files: each one encoded in turn into its place under a library folder, and then the .nfo files
/// and artwork that go with them.
/// </summary>
/// <remarks>
/// Each file is written into a hidden <see cref="PartialFolderName"/> folder in its title's folder (which Emby
/// skips), and moved into place only once it's whole, so Emby never sees half a file and a failed or cancelled
/// one leaves nothing behind. A file already there is replaced.
/// </remarks>
public static class ExportRunner
{
    public const string PartialFolderName = ".dvd-xporter-partial";

    // Emby skips folders with a file of this name in them
    private const string IgnoreFileName = ".ignore";

    // A file whose length isn't known counts as this long, for overall progress
    private static readonly double UnknownLengthSeconds = 30 * 60;

    // A file copied as it is counts as this long: it's quick next to encoding
    private static readonly double CopySeconds = 10;

    /// <summary>Where the project keeps exports' working files and logs.</summary>
    public static string ExportFolder(ProjectFolder folder) => Path.Combine(folder.FolderPath, ProjectFolder.VideoFilesFolderName, "Export");

    /// <summary>Where a file goes under <paramref name="root"/>.</summary>
    public static string DestinationOf(ExportFile file, string root) =>
        Path.Combine(root, file.LibraryPath!.Replace('/', Path.DirectorySeparatorChar));

    /// <summary>Exports <paramref name="files"/> into <paramref name="root"/>, one at a time.</summary>
    /// <param name="progress">Called (from other threads) as it goes.</param>
    /// <param name="finished">Called as each file is done with, however it went.</param>
    public static async Task<ExportRun> RunAsync(ProjectFolder folder, ProjectTranscodeSources sources, IReadOnlyList<ExportFile> files,
        string root, ExportOptions options, Action<ExportProgress>? progress, Action<ExportFileResult>? finished, CancellationToken cancellation)
    {
        var run = new ExportRun();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var weights = files.Select(f => f.Job is null ? CopySeconds
            : f.Job.Duration is not null && f.Job.Duration.Value > PreciseTime.Zero ? f.Job.Duration.Value.AsTimeSpan().TotalSeconds : UnknownLengthSeconds).ToList();
        var total = weights.Sum();
        double done = 0;
        var partialFolders = new HashSet<string>();

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            var index = i;
            var before = done;
            void Report(string stage, double fraction) =>
                progress?.Invoke(new ExportProgress(index, files.Count, file, stage, fraction, total > 0 ? (before + weights[index] * fraction) / total : 0));

            var result = cancellation.IsCancellationRequested
                ? new ExportFileResult(file, ExportOutcome.Cancelled, "", null, null, TimeSpan.Zero)
                : await ExportFile(folder, sources, file, i, root, partialFolders, Report, cancellation);
            run.Results.Add(result);
            finished?.Invoke(result);
            done += weights[i];
        }

        foreach (var partial in partialFolders)
            RemovePartialFolder(partial);

        run.Cancelled = cancellation.IsCancellationRequested;
        if (!run.Cancelled)
            WriteMetadata(folder, run, root, options);
        run.Elapsed = clock.Elapsed;
        return run;
    }

    private static async Task<ExportFileResult> ExportFile(ProjectFolder folder, ProjectTranscodeSources sources, ExportFile file, int index,
        string root, HashSet<string> partialFolders, Action<string, double> report, CancellationToken cancellation)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var libraryPath = file.LibraryPath;
        if (!file.CanExport || libraryPath is null)
        {
            var reason = file.Issues.FirstOrDefault(i => i.Level == IssueLevel.Error)?.Message
                ?? (file.MissingDiscs.Count > 0 ? "Its disc isn't on this computer." : "It isn't ready to export.");
            return new ExportFileResult(file, ExportOutcome.Skipped, "", reason, null, TimeSpan.Zero);
        }

        var destination = DestinationOf(file, root);
        var titleFolder = Path.Combine(root, libraryPath.Split('/')[0]);
        var partialFolder = Path.Combine(titleFolder, PartialFolderName);
        // Numbered, as extras in different folders can share a name; the extension stays last, for ffmpeg (a gallery's
        // is a folder)
        var partial = Path.Combine(partialFolder, $"{index + 1:000} {Path.GetFileName(destination)}");
        var exportFolder = ExportFolder(folder);
        var work = Path.Combine(exportFolder, "work", $"{index + 1:000}");
        var log = Path.Combine(exportFolder, "Logs", ProjectFolder.ToSafeFileName(libraryPath.Replace('/', ' ')) + ".log");

        ExportFileResult Result(ExportOutcome outcome, string? error) =>
            new(file, outcome, destination, error, File.Exists(log) ? log : null, clock.Elapsed);

        try
        {
            await PreparePartialFolderAsync(partialFolder, partialFolders, cancellation);
            if (Directory.Exists(work))
                Directory.Delete(work, true);

            var error = file.TitleFile is not null
                ? await CopyTitleFileAsync(sources, file, partial, destination, report, cancellation)
                : await EncodeAsync(sources, file, partial, destination, work, log, report, cancellation);
            return Result(error is null ? ExportOutcome.Done : ExportOutcome.Failed, error);
        }
        catch (OperationCanceledException)
        {
            TryDelete(partial);
            return Result(ExportOutcome.Cancelled, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DiscException)
        {
            TryDelete(partial);
            return Result(ExportOutcome.Failed, ex.Message);
        }
        finally
        {
            try
            {
                if (Directory.Exists(work))
                    Directory.Delete(work, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>
    /// Makes the hidden folder files are made in, beside the title's (so they're moved into place, not copied), and
    /// marks it for Emby to skip. What an earlier export left there when it was stopped short (the app closing in the
    /// middle) goes, the first time this run uses it.
    /// </summary>
    private static async Task PreparePartialFolderAsync(string partialFolder, HashSet<string> partialFolders, CancellationToken cancellation)
    {
        Directory.CreateDirectory(partialFolder);
        if (partialFolders.Add(partialFolder))
        {
            foreach (var stale in Directory.EnumerateFiles(partialFolder).Where(f => Path.GetFileName(f) != IgnoreFileName))
                TryDelete(stale);
            foreach (var stale in Directory.EnumerateDirectories(partialFolder))
                TryDelete(stale);
        }

        var ignore = Path.Combine(partialFolder, IgnoreFileName);
        if (!File.Exists(ignore))
            await File.WriteAllTextAsync(ignore, "", cancellation);
    }

    /// <summary>One of the title's files: copies it (off its disc, or out of the project) and moves it into place.</summary>
    private static async Task<string?> CopyTitleFileAsync(ProjectTranscodeSources sources, ExportFile file, string partial, string destination,
        Action<string, double> report, CancellationToken cancellation)
    {
        report("Copying", 0);
        TryDelete(partial);
        await TitleFiles.CopyAsync(file.TitleFile!, sources, partial, fraction => report("Copying", fraction), cancellation);
        report("Moving into place", 1);
        MoveIntoPlace(file.IsFolder, partial, destination);
        return null;
    }

    /// <summary>
    /// Encodes the file with ffmpeg (measuring its loudness first if it needs it) and moves it into place. Returns why
    /// it failed, if it did.
    /// </summary>
    private static async Task<string?> EncodeAsync(ProjectTranscodeSources sources, ExportFile file, string partial, string destination,
        string work, string log, Action<string, double> report, CancellationToken cancellation)
    {
        var job = file.Job!;
        sources.Canvas = file.Canvas!;
        IReadOnlyDictionary<string, string>? measured = null;
        if (job.Measurements.Count > 0)
        {
            report("Measuring the loudness", 0);
            measured = await LoudnessMeasurer.MeasureAsync(file.Canvas!, sources, sources, job, null, Path.Combine(work, "loudness"), cancellation);
        }

        var stage = job.IsImageSequence ? "Saving the pictures" : "Encoding";
        report(stage, 0);
        TryDelete(partial);
        var args = job.BuildArguments(partial, work, writeFiles: true, measured: measured);
        var error = await FFmpegRunner.RunAsync(args, job.Duration, log, fraction => report(stage, fraction), cancellation);
        if (error is not null)
        {
            TryDelete(partial);
            return error;
        }

        report("Moving into place", 1);
        if (job.IsImageSequence)
            GalleryImages.Tidy(job, partial, log);
        MoveIntoPlace(job.IsFolder, partial, destination);
        return null;
    }

    /// <summary>Puts a finished file in its place; a folder (a gallery's, or one copied off a disc) replaces the one there whole.</summary>
    private static void MoveIntoPlace(bool isFolder, string partial, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        if (isFolder)
        {
            if (Directory.Exists(destination))
                Directory.Delete(destination, recursive: true);
            Directory.Move(partial, destination);
        }
        else
        {
            File.Move(partial, destination, overwrite: true);
        }
    }

    /// <summary>Writes the .nfo files and artwork for the files that were made.</summary>
    private static void WriteMetadata(ProjectFolder folder, ExportRun run, string root, ExportOptions options)
    {
        var exported = run.Results
            .Where(r => r.Outcome == ExportOutcome.Done && r.File.Job?.Export is not null)
            .Select(r => (r.File.Job!.Export!, r.File.LibraryPath!));
        foreach (var file in EmbyMetadata.For(folder.Project, folder.Images, exported, options.WriteNfo, options.CopyArtwork))
        {
            var path = Path.Combine(root, file.RelativePath.Replace('/', Path.DirectorySeparatorChar));
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var source = file.CopyFrom;
                if (source is not null)
                    File.Copy(source, path, overwrite: true);
                else
                    File.WriteAllText(path, file.Text);
                run.MetadataWritten++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                run.MetadataErrors.Add($"{file.RelativePath}: {ex.Message}");
            }
        }
    }

    private static void RemovePartialFolder(string folder)
    {
        try
        {
            if (!Directory.Exists(folder))
                return;
            if (Directory.EnumerateFileSystemEntries(folder).All(e => Path.GetFileName(e) == IgnoreFileName))
                Directory.Delete(folder, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Deletes a file, or a folder (a gallery's, or one copied off a disc) and what's in it, if it's there.</summary>
    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
            else
                File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }
}
