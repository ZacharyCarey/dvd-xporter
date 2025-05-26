using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Settings;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The last step: every file the project's canvases make, grouped by library folder, with what's wrong with each.
/// The files that can be made now are picked to export, and the user can leave any out. They go into the user's
/// Emby library (a setting, one for movies and one for shows), or into any folder, laid out the same way; or to the
/// render server, if there is one, to export into its library.
/// </summary>
public partial class ExportPage : MarginContainer, IStepPage
{
    [Signal]
    public delegate void BackRequestedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>Asks to open a canvas in Transcoding (by its ID), with one of its nodes picked out (unless -1).</summary>
    [Signal]
    public delegate void ShowInTranscodingRequestedEventHandler(string canvasId, int nodeId);

    /// <summary>The user changed a setting here (the Emby library folder), which needs saving.</summary>
    [Signal]
    public delegate void SettingsChangedEventHandler();

    /// <summary>Files were sent to the render server: shows its jobs, to follow them.</summary>
    [Signal]
    public delegate void ServerJobsRequestedEventHandler();

    private ExportFileTree _fileTree = null!;
    private ExportDetailsPanel _details = null!;
    private Label _summaryLabel = null!;
    private Label _libraryFolderLabel = null!;
    private LineEdit _libraryFolderEdit = null!;
    private Button _exportButton = null!;
    private Button _exportToFolderButton = null!;
    private Button _checkAgainButton = null!;
    private FileDialog _libraryFolderDialog = null!;
    private FileDialog _exportFolderDialog = null!;

    private ProjectFolder? _folder;
    private DiscServices? _discServices;
    private AppSettings _settings = new();
    // Where Export to Folder last went, offered next time
    private string? _lastExportFolder;
    private readonly Dictionary<ProjectDisc, DiscSource?> _discSources = [];
    private ProjectTranscodeSources? _sources;
    private List<ExportFile> _files = [];
    private readonly ExportPicks _picks = new();
    private bool _finding;

    public override void _Ready()
    {
        _fileTree = GetNode<ExportFileTree>("%FileTree");
        _details = GetNode<ExportDetailsPanel>("%DetailsPanel");
        _summaryLabel = GetNode<Label>("%SummaryLabel");
        _libraryFolderLabel = GetNode<Label>("%LibraryFolderLabel");
        _libraryFolderEdit = GetNode<LineEdit>("%LibraryFolderEdit");
        _exportButton = GetNode<Button>("%ExportButton");
        _exportToFolderButton = GetNode<Button>("%ExportToFolderButton");
        _checkAgainButton = GetNode<Button>("%CheckAgainButton");
        _libraryFolderDialog = GetNode<FileDialog>("%LibraryFolderDialog");
        _exportFolderDialog = GetNode<FileDialog>("%ExportFolderDialog");

        SetUpRunning();
        SetUpServer();
    }

    // ---- Signals connected in the scene ----

    private void On_BackButton_Pressed() => EmitSignal(SignalName.BackRequested);

    private void On_SelectAllButton_Pressed() => SelectAllValid();

    private void On_SelectNoneButton_Pressed() => SelectNone();

    private void On_ChooseFolderButton_Pressed()
    {
        var current = LibraryFolder;
        if (current is not null && Directory.Exists(current))
            _libraryFolderDialog.CurrentDir = current;
        _libraryFolderDialog.Title = $"Choose Your Emby {LibraryName} Library";
        _libraryFolderDialog.PopupCentered(new Vector2I(760, 480));
    }

    private void On_LibraryFolderDialog_DirSelected(string path) => SetLibraryFolder(path);

    private void On_ExportToFolderButton_Pressed()
    {
        var last = _lastExportFolder;
        if (last is not null && Directory.Exists(last))
            _exportFolderDialog.CurrentDir = last;
        _exportFolderDialog.PopupCentered(new Vector2I(760, 480));
    }

    private void On_ExportFolderDialog_DirSelected(string path)
    {
        _lastExportFolder = path;
        Export(path);
    }

    private void On_CheckAgainButton_Pressed()
    {
        var folder = _folder;
        if (folder is not null)
            FindDiscs(folder);
    }

    private void On_ExportButton_Pressed()
    {
        if (_onServer)
        {
            SendToServer();
            return;
        }

        var library = LibraryFolder;
        if (library is not null)
            Export(library);
    }

    private void On_FileTree_SelectionChanged() => ShowDetails();

    private void On_FileTree_PicksChanged()
    {
        UpdateSummary();
        ShowDetails();
    }

    private void On_DetailsPanel_ShowInTranscodingRequested(string canvasId, int nodeId) =>
        EmitSignal(SignalName.ShowInTranscodingRequested, canvasId, nodeId);

    public void ShowProject(ProjectFolder folder, DiscServices discServices, RenderServerLink server, AppSettings settings)
    {
        if (folder != _folder)
        {
            _picks.Clear();
            _fileTree.Forget();
        }

        _folder = folder;
        _discServices = discServices;
        _server = server;
        _settings = settings;
        ShowLibraryFolder();
        UpdateExportControls();
        // While exporting, the files stay as they were when it started
        if (IsExporting)
            Rebuild();
        else
            FindDiscs(folder);
    }

    /// <summary>Shows the Emby library folder and the render server again, after they were changed in Settings.</summary>
    public void OnLibraryChanged()
    {
        ShowLibraryFolder();
        // The discs on the server may be another server's now, or none
        if (_folder is not null && !IsExporting)
            FindDiscs(_folder);
        else
            Rebuild();
    }

    /// <summary>The Emby library the project's files go into, or null if it isn't set.</summary>
    private string? LibraryFolder => _folder is not null && _settings.LibraryPath(_folder.Project.Kind) is { Length: > 0 } path ? path : null;

    // "Movies" or "TV Shows", as Emby calls its libraries
    private string LibraryName => _folder?.Project.Kind == ProjectKind.Movie ? "Movies" : "TV Shows";

    private void ShowLibraryFolder()
    {
        foreach (var control in new Control[] { _libraryFolderLabel, _libraryFolderEdit, GetNode<Button>("%ChooseFolderButton"), _exportToFolderButton })
            control.Visible = !_onServer;
        var libraryFolder = LibraryFolder;
        _libraryFolderLabel.Text = $"Emby {LibraryName} library";
        _libraryFolderEdit.Text = libraryFolder ?? "";
        _libraryFolderEdit.PlaceholderText = $"Choose the folder of your Emby {LibraryName} library";
        _libraryFolderEdit.TooltipText = (libraryFolder is not null ? libraryFolder + "\n\n" : "")
            + $"Every {(_folder?.Project.Kind == ProjectKind.Movie ? "movie" : "show")} project exports into this library, "
            + "each into its title's folder there. It's also in Settings › Library.";
    }

    /// <summary>Looks for the discs again, e.g. after the Disc Library was scanned.</summary>
    public void OnDiscLibraryChanged()
    {
        var folder = _folder;
        if (folder is not null)
            FindDiscs(folder);
    }

    // ---- Checking the files ----

    private async void FindDiscs(ProjectFolder folder)
    {
        var services = _discServices;
        if (services is null || _finding || IsExporting)
            return;

        _finding = true;
        _checkAgainButton.Disabled = true;
        Rebuild();
        SetStatus("Looking for the project's discs...");
        Dictionary<ProjectDisc, DiscResolution> found;
        try
        {
            found = await DiscFinder.For(folder, services, _server?.Client).GetAvailableDiscsAsync(folder.Project.Discs.ToList());
        }
        finally
        {
            _finding = false;
            _checkAgainButton.Disabled = false;
        }

        if (folder != _folder)
            return;
        _discSources.Clear();
        foreach (var (disc, resolution) in found)
            _discSources[disc] = resolution.Source;
        _sources = new ProjectTranscodeSources(folder, _discSources);
        Rebuild();
        SetStatus("Ready.");
        LookOnServer();
    }

    // ---- The list ----

    /// <summary>Checks every file again and shows the list (while exporting, only shows it, as the files are being made).</summary>
    private void Rebuild()
    {
        if (!IsExporting)
        {
            _files = _sources is not null && !_finding && _folder is not null
                ? ExportList.Build(_sources, _onServer ? null : LibraryFolder, _folder.Cache.ExportSources())
                : [];
        }

        _fileTree.Show(_files, _picks, _runs, IsExporting,
            _finding ? "Looking for the project's discs..." : "No files yet. Add Output Files and Exports to a canvas in Transcoding.");
        UpdateSummary();
        ShowDetails();
    }

    // ---- Picking files ----

    private List<ExportFile> PickedFiles() => _files.Where(_picks.IsPicked).ToList();

    private void UpdateSummary()
    {
        var picked = PickedFiles().Count;
        var parts = new List<string> { $"{picked} of {_files.Count} {(_files.Count == 1 ? "file" : "files")} selected" };
        if (_files.Count(f => f.State == ExportState.Error) is > 0 and var errors)
            parts.Add($"{errors} with errors");
        if (_files.Count(f => f.MissingDiscs.Count > 0) is > 0 and var missing)
            parts.Add($"{missing} missing a disc");
        var server = _onServer ? _server?.Client?.Info : null;
        if (server is not null && !server.SameVersion)
            parts.Add($"⚠ \"{server.Name}\" is a different version: update it");
        _summaryLabel.Text = string.Join("  ·  ", parts);

        var files = picked == 1 ? "1 File" : $"{picked} Files";
        if (_onServer)
        {
            UpdateServerButton(files, picked);
            return;
        }

        _exportButton.Text = IsExporting ? "Exporting..." : $"Export {files} to Emby";
        _exportButton.Disabled = picked == 0 || LibraryFolder is null || IsExporting || _finding;
        _exportButton.TooltipText = IsExporting ? "An export is running."
            : LibraryFolder is null ? $"Choose your Emby {LibraryName} library folder first."
            : picked == 0 ? "Select the files to export."
            : $"Encode the selected files straight into your Emby {LibraryName} library.";
        _exportToFolderButton.Disabled = picked == 0 || IsExporting || _finding;
        _exportToFolderButton.TooltipText = IsExporting ? "An export is running."
            : picked == 0 ? "Select the files to export."
            : "Encode the selected files into a folder you choose, laid out as an Emby library would be, to copy in later.";
    }

    /// <summary>Uses the version picked in a row's Source dropdown, remembering it for this user.</summary>
    private void On_FileTree_SourcePicked()
    {
        var folder = _folder;
        if (folder is null || _fileTree.SourceRowFile is not { Job.LibraryPath: { } path } file)
            return;
        var pick = _fileTree.PickedSource;
        if (pick is null)
            return;

        // The best placed one needs no remembering
        folder.Cache.SetExportSource(path, pick == file.Alternates.First(a => a.CanExport) ? null : pick.SourceId);
        try
        {
            folder.Cache.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SetStatus($"Couldn't save the choice: {ex.Message}");
        }

        SetStatus($"\"{Path.GetFileName(path)}\" will be made from \"{pick.SourceName}\".");
        // Rebuilding frees this row, so wait until its edit has finished
        Callable.From(Rebuild).CallDeferred();
    }

    private void SelectAllValid()
    {
        _picks.Set(_files.Where(f => f.CanExport), true);
        _fileTree.UpdateChecks();
        On_FileTree_PicksChanged();
    }

    private void SelectNone()
    {
        _picks.Set(_files, false);
        _fileTree.UpdateChecks();
        On_FileTree_PicksChanged();
    }

    // ---- Details ----

    private void ShowDetails()
    {
        var file = _fileTree.SelectedFile;
        var group = _fileTree.SelectedGroup;
        if (file is not null)
            _details.ShowFile(file, _runs.GetValueOrDefault(file.Key));
        else if (group is not null)
            _details.ShowGroup(group, _files.Where(f => f.Group == group).ToList(), _picks);
        else
            _details.ShowNothing(_files.Count > 0);
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    // ---- Exporting ----

    private void SetLibraryFolder(string path)
    {
        var folder = _folder;
        if (folder is null)
            return;
        if (folder.Project.Kind == ProjectKind.Movie)
            _settings.MoviesLibraryPath = path;
        else
            _settings.ShowsLibraryPath = path;
        EmitSignal(SignalName.SettingsChanged);
        OnLibraryChanged();
    }

    // ---- As a step page ----

    public ProjectStep Step => ProjectStep.Export;

    void IStepPage.ShowProject(ProjectFolder folder, AppServices services) => ShowProject(folder, services.Discs, services.Server, AppSettings.Current);

    void IStepPage.Leave()
    {
    }

    void IStepPage.OnDiscLibraryChanged()
    {
        if (Visible)
            OnDiscLibraryChanged();
    }

    /// <summary>Its files go into the Emby library folder.</summary>
    void IStepPage.OnSettingsChanged()
    {
        if (Visible)
            OnLibraryChanged();
    }
}
