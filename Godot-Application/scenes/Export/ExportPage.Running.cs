using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

// Running an export: the progress window (which holds the app until it's done), each file's outcome, and the summary
public partial class ExportPage
{
    // How long an export has to have run before it guesses how long is left
    private static readonly TimeSpan EstimateAfter = TimeSpan.FromSeconds(20);

    private ExportProgressDialog _progressDialog = null!;
    private CheckBox _nfoCheck = null!;
    private CheckBox _artworkCheck = null!;
    private AcceptDialog _doneDialog = null!;

    // The export running, if one is
    private CancellationTokenSource? _exportCancellation;
    private readonly Stopwatch _exportClock = new();
    // The latest export's files, by key, and their keys in the order they're made
    private readonly Dictionary<string, ExportFileRun> _runs = [];
    private readonly List<string> _runOrder = [];

    /// <summary>Whether files are being exported now.</summary>
    public bool IsExporting => _exportCancellation is not null;

    /// <summary>Stops the export, if one's running (the file being made is thrown away).</summary>
    public void CancelExport() => _exportCancellation?.Cancel();

    /// <summary>
    /// Asks the user whether to stop the export, so they can <paramref name="what"/> (e.g. "quit"), and if they agree, stops
    /// it and does <paramref name="action"/>. While exporting, the progress window holds the app, so it asks.
    /// </summary>
    public void AskToStop(string what, Action action)
    {
        if (!IsExporting)
        {
            action();
            return;
        }

        _progressDialog.AskToStop(_sending
            ? $"Files are still being sent to the render server. Stop sending them and {what}?"
            : $"Files are still being exported. Stop exporting and {what}?", action);
    }

    private void SetUpRunning()
    {
        _nfoCheck = GetNode<CheckBox>("%NfoCheck");
        _artworkCheck = GetNode<CheckBox>("%ArtworkCheck");
        _doneDialog = GetNode<AcceptDialog>("%DoneDialog");
        _progressDialog = GetNode<ExportProgressDialog>("%ExportProgressDialog");
    }

    private void On_ExportProgressDialog_CancelRequested() => CancelExport();

    private void On_NfoCheck_Toggled(bool on)
    {
        _settings.ExportNfo = on;
        EmitSignal(SignalName.SettingsChanged);
    }

    private void On_ArtworkCheck_Toggled(bool on)
    {
        _settings.ExportArtwork = on;
        EmitSignal(SignalName.SettingsChanged);
    }

    public override void _ExitTree() => CancelExport();

    /// <summary>Encodes the picked files into <paramref name="root"/>, each at its library path there.</summary>
    private async void Export(string root)
    {
        var folder = _folder;
        var sources = _sources;
        if (IsExporting || folder is null || sources is null || _finding)
            return;
        var picked = PickedFiles();
        if (picked.Count == 0)
            return;

        _runs.Clear();
        _runOrder.Clear();
        foreach (var file in picked)
        {
            _runs[file.Key] = new ExportFileRun { Stage = "Waiting" };
            _runOrder.Add(file.Key);
        }

        var cancellation = _exportCancellation = new CancellationTokenSource();
        _exportClock.Restart();
        _progressDialog.Open($"Exporting to {root}", picked.Select(f => f.Name), "Stop exporting?",
            "The file being made is thrown away; the ones already finished stay where they are.");
        UpdateExportControls();
        Rebuild();
        SetStatus($"Exporting {Plural(picked.Count, "file")} to {root}...");

        var options = new ExportOptions { WriteNfo = _settings.ExportNfo, CopyArtwork = _settings.ExportArtwork };
        ExportRun run;
        try
        {
            run = await ExportRunner.RunAsync(folder, sources, picked, root, options,
                progress => Callable.From(() => OnProgress(progress)).CallDeferred(),
                result => Callable.From(() => OnFinished(result)).CallDeferred(),
                cancellation.Token);
        }
        finally
        {
            _exportCancellation = null;
            cancellation.Dispose();
            _exportClock.Stop();
            _progressDialog.Hide();
        }

        if (folder != _folder)
            return;
        foreach (var result in run.Results)
            OnFinished(result);
        // What's made is done with; what failed stays picked to try again
        foreach (var result in run.Results.Where(r => r.Outcome == ExportOutcome.Done))
            _picks.Set(result.File, false);

        UpdateExportControls();
        Rebuild();
        // Not if the user's gone on to something else (closing the project stops an export)
        if (IsVisibleInTree())
            ReportDone(run, root);
        else
            SetStatus(run.Cancelled ? "Export cancelled." : "Export finished.");
    }

    private void OnProgress(ExportProgress progress)
    {
        var state = _runs.GetValueOrDefault(progress.File.Key);
        if (!IsExporting || state is null)
            return;
        foreach (var other in _runs.Values)
            other.Active = false;
        state.Active = true;
        state.Stage = progress.Stage;
        state.Fraction = progress.Fraction;

        var elapsed = _exportClock.Elapsed;
        var left = elapsed > EstimateAfter && progress.Overall > 0.01
            ? TimeSpan.FromSeconds(elapsed.TotalSeconds * (1 - progress.Overall) / progress.Overall)
            : (TimeSpan?)null;
        var time = left is not null ? $"{FormatDuration(elapsed)} so far  ·  about {FormatDuration(left.Value)} left" : $"{FormatDuration(elapsed)} so far";
        _progressDialog.ShowProgress(progress.Index, progress.Count, progress.File.Name, state.Text, progress.Fraction, progress.Overall, time);
        _progressDialog.SetFileState(progress.Index, state.Text, null);
        ShowRun(progress.File);
    }

    private void OnFinished(ExportFileResult result)
    {
        var state = _runs.GetValueOrDefault(result.File.Key);
        if (state is null)
            return;
        state.Outcome = result.Outcome;
        state.Active = false;
        state.Error = result.Error;
        state.LogPath = result.LogPath;
        state.Destination = result.Destination.Length > 0 ? result.Destination : null;
        _progressDialog.SetFileState(_runOrder.IndexOf(result.File.Key), state.Text, state.Color);
        ShowRun(result.File);
    }

    private void ReportDone(ExportRun run, string root)
    {
        int Count(ExportOutcome outcome) => run.Results.Count(r => r.Outcome == outcome);
        var done = Count(ExportOutcome.Done);
        var failed = run.Results.Where(r => r.Outcome is ExportOutcome.Failed or ExportOutcome.Skipped).ToList();
        var lines = new List<string>();
        if (run.Cancelled)
        {
            lines.Add(done == 0 ? "The export was cancelled before any files were finished."
                : $"The export was cancelled. {Plural(done, "file")} {(done == 1 ? "was" : "were")} finished first, and {(done == 1 ? "is" : "are")} in {root}.");
        }
        else
        {
            lines.Add($"Exported {done} of {Plural(run.Results.Count, "file")} to {root} in {FormatDuration(run.Elapsed)}.");
        }

        if (failed.Count > 0)
        {
            lines.Add("");
            lines.Add($"{Plural(failed.Count, "file")} couldn't be made: {string.Join(", ", failed.Take(4).Select(r => $"\"{r.File.Name}\""))}"
                + (failed.Count > 4 ? ", ..." : "") + ". Select one to see why.");
        }

        if (run.MetadataWritten > 0)
            lines.Add($"Wrote {Plural(run.MetadataWritten, "details or artwork file")} beside them.");
        if (run.MetadataErrors.Count > 0)
            lines.Add($"Couldn't write {Plural(run.MetadataErrors.Count, "details or artwork file")}: {run.MetadataErrors[0]}");

        _doneDialog.Title = run.Cancelled ? "Export Cancelled" : failed.Count > 0 ? "Export Finished, with Problems" : "Export Finished";
        _doneDialog.DialogText = string.Join("\n", lines);
        _doneDialog.PopupCentered();
        SetStatus(run.Cancelled ? "Export cancelled." : failed.Count > 0
            ? $"Exported {done} of {Plural(run.Results.Count, "file")}; {failed.Count} failed."
            : $"Exported {Plural(done, "file")}.");
    }

    /// <summary>Shows what can be done while exporting (only following along and cancelling) or not.</summary>
    private void UpdateExportControls()
    {
        var exporting = IsExporting;
        foreach (var control in new BaseButton[]
                 {
                     GetNode<Button>("%SelectAllButton"), GetNode<Button>("%SelectNoneButton"), GetNode<Button>("%ChooseFolderButton"),
                     _checkAgainButton, _nfoCheck, _artworkCheck, _exportOnOption,
                 })
        {
            control.Disabled = exporting;
        }

        _nfoCheck.SetPressedNoSignal(_settings.ExportNfo);
        _artworkCheck.SetPressedNoSignal(_settings.ExportArtwork);
        UpdateSummary();
    }

    /// <summary>Shows how <paramref name="file"/>'s export is going in its row, and its details if they're shown.</summary>
    private void ShowRun(ExportFile file)
    {
        _fileTree.UpdateRun(file);
        if (_fileTree.SelectedFile == file)
            ShowDetails();
    }

    private static string Plural(int count, string what) => $"{count} {what}{(count == 1 ? "" : "s")}";

    private static string FormatDuration(TimeSpan time) => time.TotalHours >= 1
        ? $"{(int)time.TotalHours} h {time.Minutes} min"
        : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes} min" : $"{Math.Max(1, (int)time.TotalSeconds)} s";
}
