using System;
using System.Threading;
using System.Threading.Tasks;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Settings;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The Disc Library: the folder of the user's disc rips (a setting), asked for if it isn't set, and scanned in the
/// background to know which discs are where. Starting a scan cancels any scan already running. Rescanning has the render
/// server look through its own too.
/// </summary>
public partial class DiscLibraryScanner : Node
{
    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>What's known to be in the library changed: it was scanned, emptied, or a scan failed.</summary>
    [Signal]
    public delegate void LibraryChangedEventHandler();

    /// <summary>The user chose the library folder, which needs saving.</summary>
    [Signal]
    public delegate void SettingsChangedEventHandler();

    /// <summary>Asking where the library is is over: a folder was chosen, or the user put it off.</summary>
    [Signal]
    public delegate void PromptFinishedEventHandler();

    public DiscServices Services { get; set; } = null!;

    public RenderServerLink? Server { get; set; }

    private ConfirmationDialog _prompt = null!;
    private CancellationTokenSource? _scanCancellation;

    public override void _Ready() => _prompt = GetNode<ConfirmationDialog>("%DiscLibraryPrompt");

    public override void _ExitTree() => _scanCancellation?.Cancel();

    /// <summary>Scans the library (asking where it is if that isn't set), and has the render server look through its own.</summary>
    public void Rescan()
    {
        ScanThisComputer();
        RescanServer();
    }

    /// <summary>Scans the library, or asks where it is if that isn't set.</summary>
    public void ScanThisComputer()
    {
        if (string.IsNullOrEmpty(AppSettings.Current.DiscLibraryPath))
            _prompt.PopupCentered();
        else
            Scan();
    }

    /// <summary>Forgets every disc's fingerprint, so each is identified again, then rescans.</summary>
    public void ClearFingerprintCache()
    {
        Services.Cache.Clear();
        SetStatus("Cleared the disc fingerprint cache.");
        ScanThisComputer();
    }

    /// <summary>The library folder was changed in Settings: scans it, or forgets it if it was cleared.</summary>
    public void OnFolderChanged()
    {
        if (AppSettings.Current.DiscLibraryPath is null)
        {
            _scanCancellation?.Cancel();
            Services.Library.Clear();
            EmitSignal(SignalName.LibraryChanged);
        }
        else
        {
            Scan();
        }
    }

    private void On_DiscLibraryDialog_DirSelected(string path)
    {
        AppSettings.Current.DiscLibraryPath = path;
        EmitSignal(SignalName.SettingsChanged);
        EmitSignal(SignalName.PromptFinished);
        Scan();
    }

    /// <summary>"Later", or the folder picker closed without a folder.</summary>
    private void On_Prompt_Canceled() => EmitSignal(SignalName.PromptFinished);

    // Discs renamed or added on the server are only seen once it looks again, which it otherwise does when it starts
    private async void RescanServer()
    {
        var server = Server?.Client;
        if (server is null)
            return;

        string status;
        try
        {
            var result = await server.RescanLibraryAsync();
            var problems = result.Problems.Count > 0 ? $" ({result.Problems.Count} couldn't be read)" : "";
            status = $"\"{server.Name}\" looked through its Disc Library: {result.DiscCount} {(result.DiscCount == 1 ? "disc" : "discs")} found{problems}.";
            foreach (var problem in result.Problems)
                GD.PushWarning($"Disc Library on \"{server.Name}\": {problem}");
        }
        catch (RenderServerException ex)
        {
            status = $"Couldn't have \"{server.Name}\" look through its Disc Library: {ex.Message}";
        }

        if (!IsInstanceValid(this))
            return;
        SetStatus(status);
        EmitSignal(SignalName.LibraryChanged);
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    private async void Scan()
    {
        _scanCancellation?.Cancel();
        var cancellation = _scanCancellation = new CancellationTokenSource();
        var root = AppSettings.Current.DiscLibraryPath;
        if (string.IsNullOrEmpty(root))
            return;

        SetStatus("Scanning Disc Library...");
        var progress = new Progress<int>(count =>
        {
            if (!cancellation.IsCancellationRequested)
                SetStatus($"Scanning Disc Library... {count} {(count == 1 ? "disc" : "discs")} found");
        });

        DiscLibraryScanResult result;
        try
        {
            result = await Task.Run(() => Services.Library.Scan(root, progress, cancellation.Token));
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (Exception ex)
        {
            Services.Library.Clear();
            SetStatus($"Disc Library scan failed: {ex.Message}");
            EmitSignal(SignalName.LibraryChanged);
            return;
        }

        if (cancellation.IsCancellationRequested)
            return;

        var discWord = result.DiscCount == 1 ? "disc" : "discs";
        var problems = result.Errors.Count > 0 ? $" ({result.Errors.Count} couldn't be read)" : "";
        SetStatus($"Disc Library scanned: {result.DiscCount} {discWord} found{problems}.");
        foreach (var error in result.Errors)
            GD.PushWarning($"Disc Library: {error.Path}: {error.Message}");
        EmitSignal(SignalName.LibraryChanged);
    }
}
