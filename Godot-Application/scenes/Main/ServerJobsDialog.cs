using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The render server's jobs, running, waiting and finished, each with its files, kept up to date while it's open. A
/// job can be cancelled, and finished ones taken off the list.
/// </summary>
public partial class ServerJobsDialog : AcceptDialog
{
    private enum Column
    {
        Name,
        Status,
        Progress,
    }

    private static readonly Color FailedColor = new(0.89f, 0.44f, 0.42f);

    /// <summary>The render server, as Settings has it.</summary>
    public RenderServerLink? Server { get; set; }

    private Label _statusLabel = null!;
    private Tree _jobTree = null!;
    private Button _cancelJobButton = null!;
    private Button _removeJobButton = null!;
    private Button _clearFinishedButton = null!;
    private Timer _refreshTimer = null!;
    private ConfirmationDialog _cancelConfirmDialog = null!;

    // The jobs shown, and each row's job (a file's row is its job's), by the row's instance ID
    private List<RenderJobStatus> _jobs = [];
    private readonly Dictionary<ulong, RenderJobStatus> _rows = [];
    // Jobs the user has folded or unfolded, by ID, kept as the list is refreshed
    private readonly Dictionary<string, bool> _collapsed = [];
    private bool _refreshing;

    public override void _Ready()
    {
        _statusLabel = GetNode<Label>("%StatusLabel");
        _jobTree = GetNode<Tree>("%JobTree");
        _cancelJobButton = GetNode<Button>("%CancelJobButton");
        _removeJobButton = GetNode<Button>("%RemoveJobButton");
        _clearFinishedButton = GetNode<Button>("%ClearFinishedButton");
        _refreshTimer = GetNode<Timer>("%RefreshTimer");
        _cancelConfirmDialog = GetNode<ConfirmationDialog>("%CancelConfirmDialog");

        // Column titles and widths can't be set in a scene (the column count is in it)
        _jobTree.SetColumnTitle((int)Column.Name, "Job / File");
        _jobTree.SetColumnTitle((int)Column.Status, "Status");
        _jobTree.SetColumnTitle((int)Column.Progress, "Progress");
        _jobTree.SetColumnExpandRatio((int)Column.Name, 3);
        _jobTree.SetColumnExpandRatio((int)Column.Status, 2);
        _jobTree.SetColumnExpand((int)Column.Progress, false);
        _jobTree.SetColumnCustomMinimumWidth((int)Column.Progress, 90);
    }

    public void Open()
    {
        _jobs = [];
        ShowJobs();
        PopupCentered();
        Refresh();
    }

    private void On_VisibilityChanged()
    {
        if (Visible)
            _refreshTimer.Start();
        else
            _refreshTimer.Stop();
    }

    /// <summary>Asks the server for its jobs again.</summary>
    private async void Refresh()
    {
        var client = Server?.Client;
        if (client is null)
        {
            _jobs = [];
            ShowJobs();
            _statusLabel.Text = Server?.Problem ?? "There's no render server. Set one up in Settings › Server.";
            return;
        }

        if (_refreshing)
            return;
        _refreshing = true;
        try
        {
            _jobs = await client.GetJobsAsync();
            if (client.Info is null)
                await client.ConnectAsync();
            _statusLabel.Text = _jobs.Count == 0 ? $"\"{client.Name}\" has no jobs. Send it files to export from the Export step."
                : $"Jobs on \"{client.Name}\", newest first. One runs at a time.";
        }
        catch (RenderServerException ex)
        {
            _statusLabel.Text = ex.Message;
        }
        finally
        {
            _refreshing = false;
        }

        ShowJobs();
    }

    private void ShowJobs()
    {
        var selected = SelectedJob?.Id;
        foreach (var item in _jobTree.GetRoot()?.GetChildren() ?? [])
            _collapsed[_rows[item.GetInstanceId()].Id] = item.Collapsed;
        _jobTree.Clear();
        _rows.Clear();
        var root = _jobTree.CreateItem();
        foreach (var job in _jobs)
        {
            var row = AddRow(root, job, $"{job.ProjectName}  ·  {job.Submitted.ToLocalTime():g}", Describe(job), job.State == RenderJobState.Running ? job.Overall : null);
            row.SetTooltipText((int)Column.Status, job.Message ?? Describe(job));
            if (job.State == RenderJobState.Failed)
                row.SetCustomColor((int)Column.Status, FailedColor);
            // Finished jobs are folded away, but those that went wrong
            row.Collapsed = _collapsed.GetValueOrDefault(job.Id, job.State is RenderJobState.Done or RenderJobState.Cancelled);

            foreach (var file in job.Files)
            {
                var fileRow = AddRow(row, job, file.Name, file.Error is not null ? $"{file.Stage}: {file.Error}" : file.Stage,
                    file.Outcome is null && file.Fraction > 0 ? file.Fraction : null);
                fileRow.SetTooltipText((int)Column.Status, file.Error ?? file.Stage);
                if (file.Outcome is ExportOutcome.Failed or ExportOutcome.Skipped)
                    fileRow.SetCustomColor((int)Column.Status, FailedColor);
                else if (file.Outcome is null && file.Fraction == 0)
                    fileRow.SetCustomColor((int)Column.Status, ExportColors.Dim);
            }

            if (job.Id == selected)
                row.Select((int)Column.Name);
        }

        UpdateButtons();
    }

    private TreeItem AddRow(TreeItem parent, RenderJobStatus job, string name, string status, double? fraction)
    {
        var row = _jobTree.CreateItem(parent);
        row.SetText((int)Column.Name, name);
        row.SetTooltipText((int)Column.Name, name);
        row.SetText((int)Column.Status, status);
        row.SetText((int)Column.Progress, fraction is not null ? $"{fraction:P0}" : "");
        _rows[row.GetInstanceId()] = job;
        return row;
    }

    private static string Describe(RenderJobStatus job)
    {
        // The file being made, if one is under way
        var file = job.Files.FirstOrDefault(f => f.Outcome is null && f.Fraction > 0);
        return job.State switch
        {
            RenderJobState.Receiving => "Being sent",
            RenderJobState.Queued => "Waiting its turn",
            RenderJobState.Running => file is not null ? $"{file.Stage}: {file.Name}" : "Exporting",
            RenderJobState.Done => job.Message ?? "Done",
            RenderJobState.Cancelled => "Cancelled",
            _ => job.Message is not null ? $"Failed: {job.Message}" : "Failed",
        };
    }

    private RenderJobStatus? SelectedJob
    {
        get
        {
            var item = _jobTree.GetSelected();
            return item is not null ? _rows.GetValueOrDefault(item.GetInstanceId()) : null;
        }
    }

    private void UpdateButtons()
    {
        var job = SelectedJob;
        _cancelJobButton.Disabled = job is null || job.IsFinished;
        _removeJobButton.Disabled = job is not { IsFinished: true };
        _clearFinishedButton.Disabled = !_jobs.Any(j => j.IsFinished);
    }

    private void On_JobTree_ItemSelected() => UpdateButtons();

    private void On_CancelJobButton_Pressed()
    {
        var job = SelectedJob;
        if (job is null)
            return;
        _cancelConfirmDialog.DialogText = job.State == RenderJobState.Running
            ? $"Stop exporting \"{job.ProjectName}\"?\n\nThe file being made is thrown away; the ones already finished stay where they are."
            : $"Take \"{job.ProjectName}\" off the queue?";
        _cancelConfirmDialog.PopupCentered();
    }

    private async void On_CancelConfirmDialog_Confirmed()
    {
        var job = SelectedJob;
        var client = Server?.Client;
        if (job is null || client is null)
            return;
        try
        {
            await client.CancelJobAsync(job.Id);
        }
        catch (RenderServerException ex)
        {
            _statusLabel.Text = ex.Message;
        }

        Refresh();
    }

    private async void On_RemoveJobButton_Pressed()
    {
        var job = SelectedJob;
        if (job is not null)
            await Remove([job]);
    }

    private async void On_ClearFinishedButton_Pressed() => await Remove(_jobs.Where(j => j.IsFinished).ToList());

    private async Task Remove(List<RenderJobStatus> jobs)
    {
        var client = Server?.Client;
        if (client is null)
            return;
        try
        {
            foreach (var job in jobs)
                await client.RemoveJobAsync(job.Id);
        }
        catch (RenderServerException ex)
        {
            _statusLabel.Text = ex.Message;
        }

        Refresh();
    }
}
