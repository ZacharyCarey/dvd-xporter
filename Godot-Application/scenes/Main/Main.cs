using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Online;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Settings;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Application shell: menu bar, status bar, top-level dialogs, app-wide services, and switching between
/// the start page and an open project.
/// </summary>
public partial class Main : Control
{
    private const string AppName = "DVD Xporter";

    private enum FileMenuId
    {
        CloseProject,
        ExportProject,
        Settings,
        Quit,
        ServerJobs,
    }

    // Workflow steps use their ProjectStep value as the ID; these sit after them.
    private enum ProjectMenuId
    {
        NewRelease = 100,
        AddDiscFromLibrary,
        AddDiscFromFolder,
        AddDiscFromImage,
        RescanDiscLibrary,
        Overview,
    }

    private enum HelpMenuId
    {
        About,
    }

    private MenuBar _menuBar = null!;
    private PopupMenu _fileMenu = null!;
    private PopupMenu _projectMenu = null!;
    private PopupMenu _helpMenu = null!;
    private StartPage _startPage = null!;
    private ProjectPage _projectPage = null!;
    private TranscodingPage _transcodingPage = null!;
    private ExportPage _exportPage = null!;
    // Information, Clip Renamer, Transcoding and Export
    private List<IStepPage> _steps = [];
    private Label _statusLabel = null!;
    private ProgressBar _readProgress = null!;
    private Control _serverReadRow = null!;
    private Label _serverReadLabel = null!;
    private ProgressBar _serverReadProgress = null!;
    private readonly DiscReader _discReader = new();
    private ConfirmationDialog _stopReadingDialog = null!;
    private Action? _afterStopReading;
    private SettingsDialog _settingsDialog = null!;
    private DiscLibraryScanner _discLibrary = null!;
    private ProjectPackages _packages = null!;
    private AcceptDialog _errorDialog = null!;
    private AcceptDialog _aboutDialog = null!;
    // Shown at start-up if programs the app needs are missing
    private DependenciesDialog _dependenciesDialog = null!;
    private TesseractDialog _tesseractDialog = null!;
    private ServerJobsDialog _serverJobsDialog = null!;

    private RecentProjectList _recentProjects = null!;
    private AppServices _services = null!;
    private readonly OnlineServices _online = new();
    private readonly RenderServerLink _server = new();
    private ProjectFolder? _openProject;

    public override void _Ready()
    {
        _menuBar = GetNode<MenuBar>("%MenuBar");
        _fileMenu = GetNode<PopupMenu>("%File");
        _projectMenu = GetNode<PopupMenu>("%Project");
        _helpMenu = GetNode<PopupMenu>("%Help");
        _startPage = GetNode<StartPage>("%StartPage");
        _projectPage = GetNode<ProjectPage>("%ProjectPage");
        _transcodingPage = GetNode<TranscodingPage>("%TranscodingPage");
        _exportPage = GetNode<ExportPage>("%ExportPage");
        _steps = [GetNode<InfoPage>("%InfoPage"), GetNode<ClipRenamerPage>("%ClipRenamerPage"), _transcodingPage, _exportPage];
        _statusLabel = GetNode<Label>("%StatusLabel");
        _readProgress = GetNode<ProgressBar>("%ReadProgress");
        _serverReadRow = GetNode<Control>("%ServerReadRow");
        _serverReadLabel = GetNode<Label>("%ServerReadLabel");
        _serverReadProgress = GetNode<ProgressBar>("%ServerReadProgress");
        _settingsDialog = GetNode<SettingsDialog>("%SettingsDialog");
        _discLibrary = GetNode<DiscLibraryScanner>("%DiscLibraryScanner");
        _packages = GetNode<ProjectPackages>("%ProjectPackages");
        _stopReadingDialog = GetNode<ConfirmationDialog>("%StopReadingDialog");
        _errorDialog = GetNode<AcceptDialog>("%ErrorDialog");
        _aboutDialog = GetNode<AcceptDialog>("%AboutDialog");
        _dependenciesDialog = GetNode<DependenciesDialog>("%DependenciesDialog");
        _tesseractDialog = GetNode<TesseractDialog>("%TesseractDialog");
        _serverJobsDialog = GetNode<ServerJobsDialog>("%ServerJobsDialog");

        AppSettings.Load(AppPaths.Settings);
        _recentProjects = RecentProjectList.Load(AppPaths.RecentProjects);
        _services = new AppServices(new DiscServices(AppPaths.FingerprintCache), _online, _server);
        _discLibrary.Services = _services.Discs;
        _discLibrary.Server = _server;
        _settingsDialog.Online = _online;
        _online.Configure(AppSettings.Current.TmdbApiKey, AppSettings.Current.TvdbApiKey, AppSettings.Current.TvdbPin,
            AppSettings.Current.OmdbApiKey, AppSettings.Current.MetadataLanguage);
        _server.Configure(AppSettings.Current.RenderServerAddress, AppSettings.Current.RenderServerKey);
        _projectPage.Server = _server;
        _serverJobsDialog.Server = _server;

        SetMenuShortcuts();

        _discReader.StatusChanged += SetStatus;
        _discReader.ProgressChanged += fraction =>
        {
            _readProgress.Visible = fraction is not null;
            _readProgress.Value = fraction ?? 0;
        };
        // A disc being read on the render server has a line of its own, there only while it's reading
        _discReader.ServerStatusChanged += message =>
        {
            _serverReadLabel.Text = message;
            _serverReadLabel.TooltipText = message;
        };
        _discReader.ServerProgressChanged += fraction =>
        {
            _serverReadRow.Visible = fraction is not null;
            _serverReadProgress.Value = fraction ?? 0;
        };
        _projectPage.Reader = _discReader;
        foreach (var step in _steps)
            step.OnSettingsChanged();

        // Closing the window asks first while exporting
        GetTree().AutoAcceptQuit = false;

        RefreshRecentProjects();
        ShowStartPage();
        SetStatus("Ready.");
        _dependenciesDialog.Check();
    }

    /// <summary>The rest of starting up, once the programs the app needs are known to be there.</summary>
    private void FinishStartUp()
    {
        SetStatus("Ready.");
        // Asks where the Disc Library is, if that isn't set, once the window's up; the optional programs are suggested
        // after that (the Disc Library scanner's PromptFinished, connected in the scene), so it's one question at a time
        if (string.IsNullOrEmpty(AppSettings.Current.DiscLibraryPath))
        {
            Callable.From(_discLibrary.ScanThisComputer).CallDeferred();
        }
        else
        {
            _discLibrary.ScanThisComputer();
            _tesseractDialog.SuggestOnce();
        }
    }

    public override void _ExitTree()
    {
        _discReader.Cancel();
        _online.Dispose();
        _server.Dispose();
    }

    /// <summary>The menus' keyboard shortcuts, which a scene can't hold (their items are in it).</summary>
    private void SetMenuShortcuts()
    {
        (PopupMenu Menu, int Id, Key Key)[] shortcuts =
        [
            (_fileMenu, (int)FileMenuId.CloseProject, Shortcut(Key.W)),
            (_fileMenu, (int)FileMenuId.Settings, Shortcut(Key.Comma)),
            (_fileMenu, (int)FileMenuId.Quit, Shortcut(Key.Q)),
            (_projectMenu, (int)ProjectMenuId.Overview, Shortcut(Key.Key0)),
            (_projectMenu, (int)ProjectStep.Information, Shortcut(Key.Key1)),
            (_projectMenu, (int)ProjectStep.ClipRenamer, Shortcut(Key.Key2)),
            (_projectMenu, (int)ProjectStep.Transcoding, Shortcut(Key.Key3)),
            (_projectMenu, (int)ProjectStep.Export, Shortcut(Key.Key4)),
            (_projectMenu, (int)ProjectMenuId.NewRelease, Shortcut(Key.R, shift: true)),
            (_projectMenu, (int)ProjectMenuId.AddDiscFromLibrary, Shortcut(Key.D)),
            (_projectMenu, (int)ProjectMenuId.RescanDiscLibrary, Key.F5),
        ];
        foreach (var (menu, id, key) in shortcuts)
            menu.SetItemAccelerator(menu.GetItemIndex(id), key);
    }

    private void On_Help_IdPressed(long id)
    {
        if ((HelpMenuId)id == HelpMenuId.About)
            _aboutDialog.PopupCentered();
    }

    private void On_File_IdPressed(long id)
    {
        switch ((FileMenuId)id)
        {
            case FileMenuId.CloseProject:
                WhenIdle("close the project", CloseProject);
                break;
            case FileMenuId.ExportProject:
                var folder = _openProject;
                if (folder is not null)
                    _packages.ShowExportDialog(folder);
                break;
            case FileMenuId.ServerJobs:
                ShowServerJobs();
                break;
            case FileMenuId.Settings:
                _settingsDialog.Open(AppSettings.Current);
                break;
            case FileMenuId.Quit:
                WhenIdle("quit", Quit);
                break;
        }
    }

    private void On_Project_IdPressed(long id)
    {
        // Release and disc actions happen on the overview, so go there first.
        if (id is >= (long)ProjectMenuId.NewRelease and <= (long)ProjectMenuId.Overview)
            ShowOverview();

        switch (id)
        {
            case (long)ProjectMenuId.Overview:
                break;
            case (long)ProjectMenuId.NewRelease:
                _projectPage.ShowNewReleaseDialog();
                break;
            case (long)ProjectMenuId.AddDiscFromLibrary:
                _projectPage.BeginAddDisc(AddDiscSource.Library);
                break;
            case (long)ProjectMenuId.AddDiscFromFolder:
                _projectPage.BeginAddDisc(AddDiscSource.Folder);
                break;
            case (long)ProjectMenuId.AddDiscFromImage:
                _projectPage.BeginAddDisc(AddDiscSource.Image);
                break;
            case (long)ProjectMenuId.RescanDiscLibrary:
                _discLibrary.Rescan();
                break;
            default:
                OpenStep((ProjectStep)id);
                break;
        }
    }

    private void On_ClearRecentProjects()
    {
        _recentProjects.Clear();
        RefreshRecentProjects();
        SetStatus("Cleared recent projects.");
    }

    private void RefreshRecentProjects()
    {
        var entries = _recentProjects.Entries;
        _startPage.SetRecentProjects(entries);
    }

    public override void _Notification(int what)
    {
        if (what == NotificationWMCloseRequest)
            WhenIdle("quit", Quit);
    }

    private void Quit()
    {
        // ffmpeg would go on writing after the app's gone otherwise (a test encode, say)
        FFmpegRunner.StopAll();
        GetTree().Quit();
    }

    /// <summary>Does something that leaves the project, once any export or disc being read has been stopped (if the user agrees).</summary>
    /// <param name="what">What the user's doing, e.g. "quit", for the question.</param>
    private void WhenIdle(string what, Action action) => WhenNotExporting(what, () => WhenNotReading(what, action));

    private void On_StopReadingDialog_Confirmed()
    {
        _discReader.Cancel();
        var action = _afterStopReading;
        _afterStopReading = null;
        action?.Invoke();
    }

    /// <summary>Does something that leaves the project, once the disc being read (if one is) has been stopped, if the user agrees.</summary>
    private void WhenNotReading(string what, Action action)
    {
        var disc = _discReader.Current;
        if (disc is null)
        {
            action();
            return;
        }

        _afterStopReading = action;
        var others = _discReader.Waiting;
        _stopReadingDialog.DialogText = others == 0
            ? $"\"{disc.Label}\" is still being read. Stop reading it to {what}?\n\n"
                + "Nothing read from it so far is kept: it's read again from the start next time the project is opened."
            : $"\"{disc.Label}\" and {others} more {(others == 1 ? "disc are" : "discs are")} still being read. Stop reading them to {what}?\n\n"
                + "Nothing read from them so far is kept: they're read again from the start next time the project is opened.";
        _stopReadingDialog.PopupCentered();
    }

    private void WhenNotExporting(string what, Action action)
    {
        if (!_exportPage.IsExporting)
        {
            action();
            return;
        }

        _exportPage.AskToStop(what, () =>
        {
            // The export winds down in the background; its ffmpeg mustn't outlive the app
            FFmpegRunner.StopAll();
            action();
        });
    }

    private void On_OpenProject(string projectFilePath)
    {
        if (projectFilePath.EndsWith(ProjectPackage.Extension, StringComparison.OrdinalIgnoreCase))
            ImportPackage(projectFilePath);
        else
            WhenIdle("open another project", () => OpenProjectNow(projectFilePath));
    }

    private void OpenProjectNow(string projectFilePath)
    {
        ProjectFolder folder;
        try
        {
            folder = ProjectFolder.Open(projectFilePath);
        }
        catch (Exception ex)
        {
            if (ex is FileNotFoundException or DirectoryNotFoundException)
            {
                _recentProjects.Remove(projectFilePath);
                RefreshRecentProjects();
            }

            ShowError("Couldn't Open Project", ex.Message);
            return;
        }

        ShowProject(folder);
        SetStatus($"Opened \"{folder.Project.Name}\".");
    }

    private void ShowProject(ProjectFolder folder)
    {
        _openProject = folder;
        _recentProjects.Touch(folder);
        RefreshRecentProjects();

        _projectPage.ShowProject(folder, _services.Discs);
        ShowPage(_projectPage);
        UpdateMenusForProject();
        GetWindow().Title = $"{folder.Project.Name} — {AppName}";
    }

    private void CloseProject()
    {
        if (_openProject is null)
            return;

        // Changes are saved as they're made, so there is nothing to prompt about.
        var name = _openProject.Project.Name;
        ShowStartPage();
        SetStatus($"Closed \"{name}\".");
    }

    private void ShowStartPage()
    {
        _openProject = null;
        _projectPage.CloseProject();
        ShowPage(_startPage);
        UpdateMenusForProject();
        GetWindow().Title = AppName;
    }

    private void UpdateMenusForProject()
    {
        var hasProject = _openProject is not null;
        _menuBar.SetMenuHidden(_projectMenu.GetIndex(), !hasProject);
        // MenuBar doesn't re-layout on its own after a menu is unhidden.
        _menuBar.UpdateMinimumSize();
        _menuBar.QueueRedraw();
        _fileMenu.SetItemDisabled(_fileMenu.GetItemIndex((int)FileMenuId.CloseProject), !hasProject);
        _fileMenu.SetItemDisabled(_fileMenu.GetItemIndex((int)FileMenuId.ExportProject), !hasProject);
        UpdateServerMenu();

        if (_openProject is not null)
        {
            _projectMenu.SetItemText(_projectMenu.GetItemIndex((int)ProjectStep.Information),
                _openProject.Project.Kind == ProjectKind.Movie ? "1. Movie Information" : "1. Show Information");
        }
    }

    /// <summary>
    /// Shows one of the main pages and hides the rest, saving any pending edits on the page being left. While a disc is
    /// being read, the page stays as it is (the other steps need what's read), saying so.
    /// </summary>
    private void ShowPage(Control page)
    {
        if (_discReader.Current is not null && !page.Visible)
        {
            var waiting = _discReader.Waiting switch
            {
                0 => "",
                1 => " (and 1 more disc)",
                var n => $" (and {n} more discs)",
            };
            ShowError("Still Reading a Disc",
                $"\"{_discReader.Current.Label}\" is still being read{waiting}. Please wait until it's finished.");
            return;
        }

        var leaving = ShownStep();
        if (leaving is not null && leaving != page)
            leaving.Leave();

        _startPage.Visible = page == _startPage;
        _projectPage.Visible = page == _projectPage;
        foreach (var step in _steps.Cast<Control>())
            step.Visible = step == page;
    }

    /// <summary>The step page shown, if one is.</summary>
    private IStepPage? ShownStep() => _steps.FirstOrDefault(s => ((Control)s).Visible);

    private void ShowOverview()
    {
        if (_openProject is not null)
            ShowPage(_projectPage);
    }

    private void OpenStep(ProjectStep step)
    {
        if (_openProject is null)
            return;

        var page = _steps.Find(s => s.Step == step);
        if (page is null)
            return;

        page.ShowProject(_openProject, _services);
        ShowPage((Control)page);
    }

    /// <param name="nodeId">The node to pick out, or -1 for none.</param>
    private void ShowInTranscoding(string canvasId, int nodeId)
    {
        if (_openProject is null)
            return;

        var canvas = _openProject.Project.Canvases.Find(c => c.Id == canvasId);
        if (canvas is null)
            return;

        ((IStepPage)_transcodingPage).ShowProject(_openProject, _services);
        ShowPage(_transcodingPage);
        _transcodingPage.ShowNode(canvas, nodeId >= 0 ? nodeId : null);
    }

    /// <summary>The Disc Library was scanned or changed, so discs may be found in other places.</summary>
    private void On_DiscLibraryScanner_LibraryChanged()
    {
        _projectPage.OnDiscLibraryChanged();
        foreach (var step in _steps)
            step.OnDiscLibraryChanged();
    }

    // ---- Project packages ----

    /// <summary>Unpacks a project package into the projects folder, then opens it.</summary>
    private void ImportPackage(string path) => WhenIdle("open the imported project", async () =>
    {
        var projectFile = await _packages.ImportAsync(path);
        if (projectFile is null)
            return;

        OpenProjectNow(projectFile);
        if (_openProject?.ProjectFilePath == projectFile)
            SetStatus($"Imported \"{_openProject.Project.Name}\" into {Path.GetDirectoryName(projectFile)}.");
    });

    // ---- Render server ----

    /// <summary>Shows the render server's jobs, e.g. after files were sent to it.</summary>
    private void ShowServerJobs() => _serverJobsDialog.Open();

    // Its jobs can be seen once there's one
    private void UpdateServerMenu()
    {
        var index = _fileMenu.GetItemIndex((int)FileMenuId.ServerJobs);
        _fileMenu.SetItemDisabled(index, _server.Client is null);
        _fileMenu.SetItemTooltip(index, _server.Client is null ? "Set up a render server in Settings › Server first" : "");
    }

    // ---- Settings ----

    private void ApplySettings()
    {
        var discLibraryChanged = _settingsDialog.Apply(AppSettings.Current);
        SaveSettings();
        if (discLibraryChanged)
            _discLibrary.OnFolderChanged();
        _online.Configure(AppSettings.Current.TmdbApiKey, AppSettings.Current.TvdbApiKey, AppSettings.Current.TvdbPin,
            AppSettings.Current.OmdbApiKey, AppSettings.Current.MetadataLanguage);
        if (_server.Configure(AppSettings.Current.RenderServerAddress, AppSettings.Current.RenderServerKey))
        {
            UpdateServerMenu();
            _projectPage.RecheckDiscs();
        }

        foreach (var step in _steps)
            step.OnSettingsChanged();

        SetStatus("Settings saved.");
    }

    private void SaveSettings()
    {
        try
        {
            AppSettings.Save(AppPaths.Settings);
        }
        catch (Exception ex)
        {
            ShowError("Couldn't Save Settings", ex.Message);
        }
    }

    private void ShowError(string title, string message)
    {
        _errorDialog.Title = title;
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    private static Key Shortcut(Key key, bool shift = false)
    {
        var mask = KeyModifierMask.MaskCmdOrCtrl;
        if (shift)
            mask |= KeyModifierMask.MaskShift;
        return (Key)mask | key;
    }

    private void SetStatus(string message)
    {
        _statusLabel.Text = message;
        // The whole message, in case it's cut off (beside the progress bar, say)
        _statusLabel.TooltipText = message;
    }

    private void On_CreateProject(string name, string location, ProjectKind kind)
    {
        ProjectFolder folder;
        try
        {
            folder = ProjectFolder.Create(location, new Project(name, kind));
        }
        catch (Exception ex)
        {
            ShowError("Couldn't Create Project", ex.Message);
            return;
        }

        AppSettings.Current.LastProjectLocation = location;
        SaveSettings();
        ShowProject(folder);
        SetStatus($"Created \"{name}\" in {folder.FolderPath}.");
    }
}
