using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

// Exporting on the render server: the files go to it, with the project and any discs it hasn't got, and it exports
// them into its own library while this computer gets on with other things
public partial class ExportPage
{
    private const int ThisComputer = 0;
    private const int RenderServer = 1;

    private Label _exportOnLabel = null!;
    private OptionButton _exportOnOption = null!;
    private VSeparator _exportOnSeparator = null!;

    private RenderServerLink? _server;
    // The server's discs, by fingerprint; null until it's answered (or if it can't be reached)
    private HashSet<string>? _serverDiscs;
    private bool _onServer;
    private bool _sending;

    private void SetUpServer()
    {
        _exportOnLabel = GetNode<Label>("%ExportOnLabel");
        _exportOnOption = GetNode<OptionButton>("%ExportOnOption");
        _exportOnSeparator = GetNode<VSeparator>("%ExportOnSeparator");
    }

    private void On_ExportOnOption_ItemSelected(long index)
    {
        _onServer = index == RenderServer;
        ShowLibraryFolder();
        Rebuild();
    }

    /// <summary>Asks the render server which of the project's discs it has, so the files it can make can be picked to send it.</summary>
    private async void LookOnServer()
    {
        var folder = _folder;
        var client = _server?.Client;
        if (folder is null || client is null)
        {
            _serverDiscs = null;
            ShowServer(null, null);
            return;
        }

        HashSet<string>? found;
        string? problem = null;
        try
        {
            if (client.Info is null)
                await client.ConnectAsync();
            found = (await new RemoteDiscProvider(client).GetAvailableDiscsAsync(folder.Project.Discs.ToList()))
                .Where(f => f.Value.Source is not null).Select(f => f.Key.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        }
        catch (RenderServerException ex)
        {
            found = null;
            problem = ex.Message;
        }

        if (folder != _folder || client != _server?.Client || IsExporting)
            return;
        _serverDiscs = found;
        ShowServer(client, problem);
        UpdateSummary();
    }

    /// <summary>Offers to export on the server, if there is one; while it can't be reached, it can't be picked.</summary>
    private void ShowServer(RenderServerClient? client, string? problem)
    {
        foreach (var control in new Control[] { _exportOnLabel, _exportOnOption, _exportOnSeparator })
            control.Visible = client is not null;

        var reachable = client is not null && _serverDiscs is not null;
        _exportOnOption.SetItemText(RenderServer, client is null ? "Render server" : reachable ? client.Name : $"{client.Name} (can't be reached)");
        _exportOnOption.SetItemDisabled(RenderServer, !reachable);
        var info = client?.Info;
        _exportOnOption.SetItemTooltip(RenderServer, problem ?? (info is not null && !info.SameVersion ? info.VersionWarning : ""));
        if (_onServer && !reachable)
        {
            // Back here until it answers again
            _onServer = false;
            _exportOnOption.Select(ThisComputer);
            ShowLibraryFolder();
            Rebuild();
        }
    }

    private void UpdateServerButton(string files, int picked)
    {
        var name = _server?.Client?.Name ?? "the server";
        var info = _server?.Client?.Info;
        _exportButton.Text = IsExporting ? "Sending..." : $"Export {files} on {name}";
        _exportButton.Disabled = picked == 0 || IsExporting || _finding || _serverDiscs is null;
        _exportButton.TooltipText = IsExporting ? "Files are being sent."
            : picked == 0 ? "Select the files to export."
            : $"Send the selected files to \"{name}\" to export into its library, with the project and any discs it hasn't got."
                + (info is not null && !info.SameVersion ? $"\n\n⚠ {info.VersionWarning}" : "");
    }

    /// <summary>
    /// Sends the picked files to the render server as a job: the discs they need that it hasn't got first (for that
    /// job only: they're deleted when it's done), then the project. The server queues it and exports the files itself.
    /// </summary>
    private async void SendToServer()
    {
        var folder = _folder;
        var client = _server?.Client;
        var serverDiscs = _serverDiscs;
        if (IsExporting || folder is null || client is null || serverDiscs is null || _finding)
            return;
        var picked = PickedFiles();
        if (picked.Count == 0)
            return;

        // The discs it hasn't got go with the job (and those are here, or the files couldn't be picked)
        var discs = picked.SelectMany(f => f.Discs).Distinct()
            .Where(d => !serverDiscs.Contains(d.Fingerprint) && _discSources.GetValueOrDefault(d) is LocalDiscSource)
            .ToList();
        var steps = discs.Select(d => $"Disc \"{d.Label}\"").Append("The project").ToList();

        var cancellation = _exportCancellation = new CancellationTokenSource();
        _sending = true;
        _exportClock.Restart();
        _progressDialog.Open($"Sending to {client.Name}", steps, "Stop sending?",
            "Nothing is exported, and what's been sent so far is deleted from the server.");
        UpdateExportControls();
        SetStatus($"Sending {Plural(picked.Count, "file")} to \"{client.Name}\"...");

        void Show(int step, string stage, double fraction) => Callable.From(() =>
        {
            if (IsExporting)
                _progressDialog.ShowProgress(step, steps.Count, steps[step], stage, fraction, (step + fraction) / steps.Count, FormatDuration(_exportClock.Elapsed) + " so far");
        }).CallDeferred();

        var package = Path.Combine(ExportRunner.ExportFolder(folder), "Server" + ProjectPackage.Extension);
        RenderJobStatus? created = null, job = null;
        string? error = null;
        try
        {
            var request = new RenderJobRequest(folder.Project.Name, picked.Select(f => new RenderJobFile(f.Key, f.SourceId, f.Name)).ToList(),
                _settings.ExportNfo, _settings.ExportArtwork);
            created = await client.CreateJobAsync(request, cancellation.Token);
            for (int i = 0; i < discs.Count; i++)
            {
                var step = i;
                Show(step, "Sending the disc", 0);
                _progressDialog.SetFileState(step, "Sending", null);
                await client.SendDiscAsync(created.Id, discs[i].Fingerprint, (LocalDiscSource)_discSources[discs[i]]!, fraction => Show(step, "Sending the disc", fraction),
                    cancellation.Token);
                _progressDialog.SetFileState(step, "Sent", ExportColors.Ready);
            }

            var last = steps.Count - 1;
            Show(last, "Packing the project", 0);
            _progressDialog.SetFileState(last, "Sending", null);
            Directory.CreateDirectory(Path.GetDirectoryName(package)!);
            await Task.Run(() => ProjectPackage.Export(folder, package), cancellation.Token);
            job = await client.SendProjectAsync(created.Id, package, fraction => Show(last, "Sending the project", fraction), cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is RenderServerException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            _exportCancellation = null;
            _sending = false;
            cancellation.Dispose();
            _exportClock.Stop();
            _progressDialog.Hide();
            try
            {
                File.Delete(package);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        // A job that wasn't sent whole would wait for ever; cancelling it deletes what was sent for it
        if (created is not null && job is null)
        {
            try
            {
                await client.CancelJobAsync(created.Id);
            }
            catch (RenderServerException)
            {
            }
        }

        if (folder != _folder)
            return;
        UpdateExportControls();
        if (job is null)
        {
            Rebuild();
            SetStatus(error is null ? "Stopped sending." : $"Couldn't send the files to \"{client.Name}\": {error}");
            if (error is not null && IsVisibleInTree())
            {
                _doneDialog.Title = "Couldn't Send the Files";
                _doneDialog.DialogText = error;
                _doneDialog.PopupCentered();
            }

            return;
        }

        // What's sent is the server's to make now; it says how they went
        foreach (var file in picked)
            _picks.Set(file, false);
        Rebuild();
        SetStatus($"Sent {Plural(picked.Count, "file")} to \"{client.Name}\" to export.");
        EmitSignal(SignalName.ServerJobsRequested);
    }
}
