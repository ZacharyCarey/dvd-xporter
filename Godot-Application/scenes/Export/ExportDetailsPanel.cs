using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>The selected file's details and problems, and how its export went, or a library folder's summary.</summary>
public partial class ExportDetailsPanel : PanelContainer
{
    /// <summary>Asks to open a canvas in Transcoding (by its ID), with one of its nodes picked out (unless -1).</summary>
    [Signal]
    public delegate void ShowInTranscodingRequestedEventHandler(string canvasId, int nodeId);

    private Label _title = null!;
    private Label _state = null!;
    private Label _info = null!;
    private Label _problemsLabel = null!;
    private ItemList _problemList = null!;
    private Button _showInTranscodingButton = null!;
    private Button _showFileButton = null!;
    private Button _openLogButton = null!;

    private ExportFile? _file;
    private ExportFileRun? _run;

    public override void _Ready()
    {
        _title = GetNode<Label>("%DetailTitle");
        _state = GetNode<Label>("%DetailState");
        _info = GetNode<Label>("%DetailInfo");
        _problemsLabel = GetNode<Label>("%ProblemsLabel");
        _problemList = GetNode<ItemList>("%ProblemList");
        _showInTranscodingButton = GetNode<Button>("%ShowInTranscodingButton");
        _showFileButton = GetNode<Button>("%ShowFileButton");
        _openLogButton = GetNode<Button>("%OpenLogButton");
        ShowNothing(false);
    }

    /// <summary>Asks for a file or folder to be picked, or says there's nothing to export.</summary>
    public void ShowNothing(bool anyFiles)
    {
        Reset(null, null);
        _title.Text = anyFiles ? "Pick a file to see its details." : "Nothing to export yet.";
        _problemsLabel.Visible = false;
        _problemList.Visible = false;
        _showInTranscodingButton.Visible = false;
    }

    /// <summary>A library folder: how many of its <paramref name="files"/> are picked, ready and not.</summary>
    public void ShowGroup(string group, IReadOnlyList<ExportFile> files, ExportPicks picks)
    {
        Reset(null, null);
        _title.Text = group;
        var picked = files.Count(picks.IsPicked);
        _state.Text = $"{picked} of {files.Count} {(files.Count == 1 ? "file" : "files")} selected.";
        var lines = new List<string>();
        void Count(Func<ExportFile, bool> which, string what)
        {
            if (files.Count(which) is > 0 and var count)
                lines.Add($"{count} {what}");
        }

        Count(f => f.CanExport, "ready to export");
        Count(f => f.State == ExportState.Error, "with errors");
        Count(f => f.MissingDiscs.Count > 0, "missing a disc");
        _info.Text = string.Join("\n", lines);
        _problemsLabel.Visible = false;
        _problemList.Visible = false;
        _showInTranscodingButton.Visible = false;
    }

    /// <summary>A file: where it goes, what it's made from, its tracks and problems, and how its export went (<paramref name="run"/>).</summary>
    public void ShowFile(ExportFile file, ExportFileRun? run)
    {
        Reset(file, run);
        var job = file.Job;
        _title.Text = file.Name;
        var (state, color) = file.State switch
        {
            ExportState.Ready when file.Issues.Any(i => i.Level == IssueLevel.Warning) =>
                ("Ready to export, but check the warnings below.", TranscodeNodeView.IssueColor(IssueLevel.Warning)),
            ExportState.Ready => ("Ready to export.", ExportColors.Ready),
            ExportState.MissingDisc =>
                ($"Can't be exported until {ExportText.DiscNames(file.MissingDiscs)} {(file.MissingDiscs.Count == 1 ? "is" : "are")} found.", ExportColors.Missing),
            _ => (file.LibraryPath is null ? "Can't be exported: it has no place in the library." : "Fix the errors below to export it.",
                TranscodeNodeView.IssueColor(IssueLevel.Error)),
        };
        SetState(state, color);

        var lines = new List<string>
        {
            $"Goes to: {file.Destination ?? file.LibraryPath ?? "nowhere yet"}",
            file.Canvas is not null ? $"Canvas: {file.Canvas.Name}" : "Set up in the Information step",
        };
        if (file.Alternates.Count > 1)
            lines.Add(ExportText.DescribeAlternates(file) + (file.Alternates.Count(a => a.CanExport) > 1 ? "\nPick another in the Source column." : ""));
        else if (file.Release is not null)
            lines.Add($"Release: {file.Release.Name}");
        if (job is not null)
        {
            var format = new List<string> { job.Extension.ToUpperInvariant() };
            var duration = job.Duration;
            if (duration is not null)
                format.Add(ExportText.FormatLength(duration.Value.AsTimeSpan()));
            if (job.Chapters.Count > 0)
                format.Add($"{job.Chapters.Count} chapters");
            lines.Add(string.Join("  ·  ", format));
        }

        if (file.Discs.Count > 0)
        {
            var discs = file.Discs.Select(d => file.MissingDiscs.Contains(d) ? $"{d.Label} (missing)"
                : file.ServerDiscs.Contains(d) ? $"{d.Label} (on the server)" : d.Label);
            lines.Add($"{(file.Discs.Count == 1 ? "Disc" : "Discs")}: {string.Join(", ", discs)}");
        }

        if (file.TitleFile is not null)
            lines.Add($"Copied as it is from {(file.TitleFile.IsAdded ? "this computer (kept in the project)" : $"\"{file.TitleFile.Path}\" on the disc")}");

        if (job is not null && job.Tracks.Count > 0)
        {
            lines.Add("Tracks:");
            lines.AddRange(job.Tracks.Select((t, i) => $"   {i + 1}. {t.Description}{(t.Language is not null ? $" ({t.Language})" : "")}"));
        }

        if (run is not null)
            ShowRun(run, lines);
        _info.Text = string.Join("\n", lines);

        _problemsLabel.Visible = true;
        _problemsLabel.Text = file.Issues.Count == 0 ? "No problems." : $"Problems ({file.Issues.Count})";
        foreach (var issue in file.Issues)
        {
            var index = _problemList.AddItem($"{TranscodeNodeView.IssueIcon(issue.Level)} {issue.Message}");
            _problemList.SetItemCustomFgColor(index, TranscodeNodeView.IssueColor(issue.Level));
            _problemList.SetItemMetadata(index, issue.NodeId ?? -1);
            _problemList.SetItemTooltip(index, issue.Message);
        }

        _problemList.Visible = file.Issues.Count > 0;
        // A title's file isn't on a canvas
        _showInTranscodingButton.Visible = file.Canvas is not null;
        _showInTranscodingButton.Disabled = false;
    }

    private void Reset(ExportFile? file, ExportFileRun? run)
    {
        _file = file;
        _run = run;
        _problemList.Clear();
        _info.Text = "";
        _state.Text = "";
        _state.RemoveThemeColorOverride("font_color");
        _showInTranscodingButton.Disabled = true;
        _showFileButton.Visible = false;
        _openLogButton.Visible = false;
    }

    private void SetState(string text, Color color)
    {
        _state.Text = text;
        _state.AddThemeColorOverride("font_color", color);
    }

    /// <summary>How its export went, in place of its state, with its log above its other details.</summary>
    private void ShowRun(ExportFileRun run, List<string> lines)
    {
        var (text, color) = run.Outcome switch
        {
            ExportOutcome.Done => ($"Exported to {run.Destination}", ExportColors.Ready),
            ExportOutcome.Failed => ("The export failed:", TranscodeNodeView.IssueColor(IssueLevel.Error)),
            ExportOutcome.Skipped => ("It was skipped:", TranscodeNodeView.IssueColor(IssueLevel.Error)),
            ExportOutcome.Cancelled => ("The export was cancelled before it was finished.", ExportColors.Checking),
            _ when run.Active => (run.Text, ExportColors.Checking),
            _ => ("Waiting its turn to be exported.", ExportColors.Checking),
        };
        SetState(text + (run.Error is not null ? "\n" + run.Error : ""), color);
        _showFileButton.Visible = run.Outcome == ExportOutcome.Done && run.Destination is not null && File.Exists(run.Destination);
        var logPath = run.LogPath;
        _openLogButton.Visible = logPath is not null && File.Exists(logPath);
        if (logPath is not null)
            lines.Insert(0, $"Log: {logPath}");
    }

    private void On_ProblemList_ItemActivated(long index)
    {
        var node = _problemList.GetItemMetadata((int)index).AsInt32();
        ShowInTranscoding(node >= 0 ? node : null);
    }

    private void On_ShowInTranscodingButton_Pressed() => ShowInTranscoding(_file?.Issues.FirstOrDefault()?.NodeId);

    private void On_ShowFileButton_Pressed()
    {
        var path = _run?.Destination;
        if (path is not null && File.Exists(path))
            OS.ShellShowInFileManager(path);
    }

    private void On_OpenLogButton_Pressed()
    {
        var log = _run?.LogPath;
        if (log is not null && File.Exists(log))
            OS.ShellOpen(log);
    }

    private void ShowInTranscoding(int? node)
    {
        var canvas = _file?.Canvas;
        var job = _file?.Job;
        if (canvas is null || job is null)
            return;

        // A template row's own nodes aren't on the canvas; its problems are on the Template node
        node ??= canvas.FindNode(job.Output.Id) is not null ? job.Output.Id : null;
        EmitSignal(SignalName.ShowInTranscodingRequested, canvas.Id, node is not null && canvas.FindNode(node.Value) is not null ? node.Value : -1);
    }
}
