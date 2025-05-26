using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

// The render server's discs: which of the project's it has, and copying one there to keep in its Disc Library (discs
// sent with a job are only kept for it)
public partial class ProjectPage
{
    private Button _copyToServerButton = null!;
    private ConfirmationDialog _copyToServerDialog = null!;

    // The server's discs, by fingerprint; null if there's no server, or it couldn't be reached
    private HashSet<string>? _serverDiscs;
    // The disc being copied to the server, and how to stop it
    private ProjectDisc? _copying;
    private CancellationTokenSource? _copyCancellation;
    // The disc the user asked to copy, until they say yes
    private (ProjectDisc Disc, LocalDiscSource Source)? _copyAsked;

    private void SetUpServer()
    {
        _copyToServerButton = GetNode<Button>("%CopyToServerButton");
        _copyToServerDialog = GetNode<ConfirmationDialog>("%CopyToServerDialog");
    }

    public override void _ExitTree() => _copyCancellation?.Cancel();

    /// <summary>Offers to copy the selected disc to the server, if it has a Disc Library and hasn't the disc; or to stop copying one.</summary>
    private void UpdateCopyButton()
    {
        var client = Server?.Client;
        _copyToServerButton.Visible = _copying is not null || (client?.Info is { HasDiscLibrary: true } && _serverDiscs is not null);
        if (_copying is not null)
        {
            _copyToServerButton.Text = "Stop Copying";
            _copyToServerButton.Disabled = false;
            _copyToServerButton.TooltipText = $"Stop copying \"{_copying.Label}\" to the render server. What's been sent is kept there, so copying it again carries on.";
            return;
        }

        var name = client?.Name ?? "the server";
        _copyToServerButton.Text = "Copy to Server...";
        var disc = _discTree.Selected as ProjectDisc;
        var found = disc is not null && _discTree.ResolutionOf(disc) is { Status: DiscStatus.Found, Source.IsHere: true };
        var there = disc is not null && _serverDiscs?.Contains(disc.Fingerprint) == true;
        _copyToServerButton.Disabled = !found || there;
        _copyToServerButton.TooltipText = disc is null ? $"Select a disc to copy to \"{name}\"'s Disc Library."
            : there ? $"\"{name}\" has this disc already."
            : !found ? "Only a disc that's on this computer (and is the project's very disc) can be copied."
            : $"Copy this disc to \"{name}\"'s Disc Library, to keep there for any job. (Discs sent with a job are deleted when it's done.)";
    }

    private void On_CopyToServerButton_Pressed()
    {
        if (_copying is not null)
        {
            _copyCancellation?.Cancel();
            return;
        }

        var disc = _discTree.Selected as ProjectDisc;
        // Only a disc here can be copied there
        var source = disc is not null ? _discTree.ResolutionOf(disc)?.Source as LocalDiscSource : null;
        var client = Server?.Client;
        if (disc is null || source is null || client is null)
            return;

        _copyAsked = (disc, source);
        _copyToServerDialog.DialogText = $"Copy \"{disc.Label}\" ({NodeTypes.FormatSize(SizeOf(source))}) to \"{client.Name}\"'s Disc Library?\n\n"
            + "It's kept there for every job and project that needs it. It's sent in the background, and this computer "
            + "has to stay on until it's done; if it's stopped, copying it again carries on from where it got to.";
        _copyToServerDialog.PopupCentered();
    }

    private async void On_CopyToServerDialog_Confirmed()
    {
        var client = Server?.Client;
        if (_copyAsked is null || client is null || _copying is not null)
            return;
        var (disc, source) = _copyAsked.Value;
        _copyAsked = null;

        var cancellation = _copyCancellation = new CancellationTokenSource();
        _copying = disc;
        UpdateCopyButton();
        SetStatus($"Copying \"{disc.Label}\" to \"{client.Name}\"...");

        var shown = -1;
        void Progress(double fraction) => Callable.From(() =>
        {
            var percent = (int)(fraction * 100);
            if (percent != shown && _copying == disc)
            {
                shown = percent;
                SetStatus($"Copying \"{disc.Label}\" to \"{client.Name}\": {percent}%...");
            }
        }).CallDeferred();

        string? error = null;
        try
        {
            await client.SendDiscAsync(null, disc.Fingerprint, source, Progress, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            error = "";
        }
        catch (Exception ex) when (ex is RenderServerException or IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }
        finally
        {
            _copying = null;
            _copyCancellation = null;
            cancellation.Dispose();
        }

        if (!IsInsideTree())
            return;
        SetStatus(error is null ? $"Copied \"{disc.Label}\" to \"{client.Name}\"'s Disc Library."
            : error.Length == 0 ? $"Stopped copying \"{disc.Label}\" to \"{client.Name}\"."
            : $"Couldn't copy \"{disc.Label}\" to \"{client.Name}\": {error}");
        if (error is not null && error.Length > 0)
            ShowError("Couldn't Copy the Disc", error);
        LookOnServer();
    }

    // Unknown (0) if its files can't be listed
    private static long SizeOf(LocalDiscSource source)
    {
        try
        {
            return source.GetSize();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }
}
