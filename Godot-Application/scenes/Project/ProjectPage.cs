using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Project overview: title, the numbered workflow steps, and the project's releases and discs
/// with each disc's status on this computer.
/// </summary>
public partial class ProjectPage : MarginContainer
{
    [Signal]
    public delegate void StepSelectedEventHandler(ProjectStep step);

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    [Signal]
    public delegate void LibraryRescanRequestedEventHandler();

    /// <summary>Reads the discs found where they are (or added) that haven't been read yet.</summary>
    public DiscReader? Reader { get; set; }

    /// <summary>The render server, whose discs are marked beside this computer's.</summary>
    public RenderServerLink? Server { get; set; }

    private Label _titleLabel = null!;
    private Label _subtitleLabel = null!;
    private StepButton _informationStep = null!;
    private Button _addDiscButton = null!;
    private Button _editButton = null!;
    private Button _removeButton = null!;
    private Button _moveUpButton = null!;
    private Button _moveDownButton = null!;
    private Label _orderHint = null!;
    private Label _serverWarning = null!;
    private DiscTree _discTree = null!;
    private Label _discsEmptyLabel = null!;
    private AddDiscPicker _addDiscPicker = null!;
    private LocateDiscPicker _locateDiscPicker = null!;
    private ConfirmationDialog _removeDialog = null!;
    private AcceptDialog _errorDialog = null!;
    private ReleaseDialog _releaseDialog = null!;

    private ProjectFolder? _folder;
    private DiscServices? _discServices;
    private AddDiscSource? _addAfterCreatingRelease;
    private object? _removeTarget;

    public override void _Ready()
    {
        _titleLabel = GetNode<Label>("%ProjectTitle");
        _subtitleLabel = GetNode<Label>("%ProjectSubtitle");
        _informationStep = GetNode<StepButton>("%Information");
        _addDiscButton = GetNode<Button>("%AddDiscButton");
        _editButton = GetNode<Button>("%EditButton");
        _removeButton = GetNode<Button>("%RemoveButton");
        _moveUpButton = GetNode<Button>("%MoveUpButton");
        _moveDownButton = GetNode<Button>("%MoveDownButton");
        SetUpServer();
        _orderHint = GetNode<Label>("%OrderHint");
        _serverWarning = GetNode<Label>("%ServerWarning");
        _discTree = GetNode<DiscTree>("%DiscTree");
        _discsEmptyLabel = GetNode<Label>("%DiscsEmptyLabel");
        _addDiscPicker = GetNode<AddDiscPicker>("%AddDiscPicker");
        _locateDiscPicker = GetNode<LocateDiscPicker>("%LocateDiscPicker");
        _removeDialog = GetNode<ConfirmationDialog>("%RemoveDialog");
        _errorDialog = GetNode<AcceptDialog>("%ErrorDialog");
        _releaseDialog = GetNode<ReleaseDialog>("%ReleaseDialog");

        _discTree.OrderHelp = _orderHint.TooltipText;
    }

    void On_ShowInfoStepButton_Pressed() => EmitSignal(SignalName.StepSelected, (int)ProjectStep.Information);
    void On_ClipRenamerStepButton_Pressed() => EmitSignal(SignalName.StepSelected, (int)ProjectStep.ClipRenamer);
    void On_TranscodingStepButton_Pressed() => EmitSignal(SignalName.StepSelected, (int)ProjectStep.Transcoding);
    void On_ExportStepButton_Pressed() => EmitSignal(SignalName.StepSelected, (int)ProjectStep.Export);

    public void ShowProject(ProjectFolder folder, DiscServices discServices)
    {
        _folder = folder;
        _discServices = discServices;
        _titleLabel.Text = folder.Project.Name;
        _titleLabel.TooltipText = folder.FolderPath;
        var movie = folder.Project.Kind == ProjectKind.Movie;
        _informationStep.Title = movie ? "Movie Information" : "Show Information";
        _informationStep.Description = movie
            ? "Title, year and other details for the movie."
            : "Title, year, seasons and episode list for the show.";

        _discTree.ShowProject(folder.Project, discServices.Library.RootPath);
        RecheckDiscs();
    }

    /// <summary>Lets go of the project, so checks and reads still under way for it are dropped when they finish.</summary>
    public void CloseProject()
    {
        _folder = null;
        _addDiscPicker.Cancel();
        _discTree.ShowProject(null, null);
    }

    public void ShowNewReleaseDialog() => _releaseDialog.PopupForNew("");

    /// <summary>
    /// Adds discs to the selected release (or the only one). With no releases yet, creates one first.
    /// </summary>
    public void BeginAddDisc(AddDiscSource source) => BeginAddDisc(source, null);

    /// <summary>
    /// Call after the Disc Library is rescanned or changed.
    /// </summary>
    public void OnDiscLibraryChanged()
    {
        _addDiscPicker.OnDiscLibraryChanged();
        RecheckDiscs();
    }

    /// <summary>
    /// Re-verifies every disc's location, e.g. after the project opens or the Disc Library is rescanned.
    /// </summary>
    public async void RecheckDiscs()
    {
        var folder = _folder;
        var services = _discServices;
        if (folder is null || services is null)
            return;

        var discs = folder.Project.Discs.ToList();
        _discTree.MarkChecking(discs);
        RefreshDiscs();

        // Shown as they're found: those here straight away, those on the server once it answers
        void show(IReadOnlyDictionary<ProjectDisc, DiscResolution> found)
        {
            if (folder != _folder)
                return;
            foreach (var (disc, resolution) in found)
                _discTree.SetResolution(disc, resolution);
            UpdateCopyButton();
            SetStatus(DescribeDiscs(discs));
            Reader?.ReadUnread(folder, found.Select(f => (f.Key, f.Value.Source)).ToList());
        }

        // Asked at the same time, so a server that can't be reached only holds up its own discs
        var lookingOnServer = LookOnServerAsync();
        await DiscFinder.For(folder, services, Server?.Client).GetAvailableDiscsAsync(discs, show);
        await lookingOnServer;
    }

    /// <summary>
    /// Asks the render server which of the project's discs it has, to show those that are there too (and so needn't
    /// be copied there). Discs are found there, when they aren't here, by <see cref="RecheckDiscs"/>.
    /// </summary>
    public async void LookOnServer() => await LookOnServerAsync();

    private async Task LookOnServerAsync()
    {
        var folder = _folder;
        var client = Server?.Client;
        if (folder is null)
            return;

        HashSet<string>? found = null;
        string? problem = null;
        if (client is not null)
        {
            try
            {
                found = (await new RemoteDiscProvider(client).GetAvailableDiscsAsync(folder.Project.Discs.ToList()))
                    .Where(f => f.Value.Source is not null).Select(f => f.Key.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            catch (RenderServerException ex)
            {
                problem = ex.Message;
            }
        }

        if (folder != _folder || client != Server?.Client)
            return;
        _serverWarning.Visible = problem is not null;
        _serverWarning.Text = $"\"{client?.Name}\" can't be reached, so discs that are only on it are missing.";
        _serverWarning.TooltipText = problem ?? "";
        _serverDiscs = found;
        _discTree.ShowServerDiscs(_serverDiscs, client?.Name ?? "");
        UpdateCopyButton();
    }

    /// <summary>Rebuilds the tree, and what depends on what's in it, after releases or discs are added, removed or moved.</summary>
    private void RefreshDiscs()
    {
        _discTree.Refresh();
        if (_folder is null)
            return;

        var project = _folder.Project;
        var releaseCount = project.Releases.Count;
        var discCount = project.Discs.Count();
        _orderHint.Visible = releaseCount > 1;
        _discTree.Visible = releaseCount > 0;
        _discsEmptyLabel.Visible = releaseCount == 0;
        _subtitleLabel.Text = $"{project.Kind.DisplayName()} project  ·  "
            + $"{releaseCount} {(releaseCount == 1 ? "release" : "releases")}  ·  {discCount} {(discCount == 1 ? "disc" : "discs")}";
        UpdateToolbar();
    }

    private void UpdateToolbar()
    {
        var selected = _discTree.Selected;
        _editButton.Disabled = selected is null;
        _removeButton.Disabled = selected is null;
        // A disc moves within its release, a release within the project
        var release = _discTree.SelectedRelease;
        var (at, count) = selected switch
        {
            ProjectDisc disc when release is not null => (release.Discs.IndexOf(disc), release.Discs.Count),
            Release selectedRelease => (_folder?.Project.Releases.IndexOf(selectedRelease) ?? -1, _folder?.Project.Releases.Count ?? 0),
            _ => (-1, 0),
        };
        _moveUpButton.Disabled = at <= 0;
        _moveDownButton.Disabled = at < 0 || at >= count - 1;
        _moveUpButton.TooltipText = selected is ProjectDisc
            ? "Move the selected disc up its release's list"
            : "Move the selected release up the list, so it's preferred over those below it when several have the same episode or movie.";
        _moveDownButton.TooltipText = selected is ProjectDisc
            ? "Move the selected disc down its release's list"
            : "Move the selected release down the list, so those above it are preferred when several have the same episode or movie.";
        _editButton.TooltipText = selected is ProjectDisc ? "Rename the selected disc" : "Edit the selected release";
        _removeButton.TooltipText = selected is ProjectDisc
            ? "Remove the selected disc from the project"
            : "Remove the selected release and its discs from the project";
        UpdateCopyButton();
    }

    // ---- The tree ----

    private void On_DiscTree_SelectionChanged() => UpdateToolbar();

    private void On_DiscTree_AddDiscRequested() => _addDiscPicker.ShowMenu(_discTree.ClickedRelease, AtMouse());

    private void On_DiscTree_EditReleaseRequested()
    {
        var release = _discTree.ClickedRelease;
        if (release is not null)
            _releaseDialog.PopupForEdit(release);
    }

    private void On_DiscTree_LocateRequested()
    {
        var disc = _discTree.ClickedDisc;
        if (disc is not null)
            _locateDiscPicker.ShowMenu(disc, AtMouse());
    }

    private void On_DiscTree_RescanRequested() => EmitSignal(SignalName.LibraryRescanRequested);

    private void On_DiscTree_DiscRenamed() => SaveProject();

    // ---- Toolbar ----

    private void On_NewReleaseButton_Pressed() => ShowNewReleaseDialog();

    /// <summary>Adding to the selected release: see <see cref="BeginAddDisc(AddDiscSource, Release?)"/>.</summary>
    private void On_AddDiscButton_Pressed()
    {
        var below = _addDiscButton.GetScreenPosition() + new Vector2(0, _addDiscButton.Size.Y);
        _addDiscPicker.ShowMenu(null, new Rect2I((Vector2I)below, Vector2I.Zero));
    }

    private void On_EditButton_Pressed()
    {
        switch (_discTree.Selected)
        {
            case Release release:
                _releaseDialog.PopupForEdit(release);
                break;
            case ProjectDisc:
                _discTree.RenameSelected();
                break;
        }
    }

    private void On_MoveUpButton_Pressed() => MoveSelected(-1);

    private void On_MoveDownButton_Pressed() => MoveSelected(1);

    private void MoveSelected(int by)
    {
        if (_discTree.Selected is ProjectDisc disc)
            MoveSelectedDisc(disc, by);
        else
            MoveSelectedRelease(by);
    }

    private void MoveSelectedDisc(ProjectDisc disc, int by)
    {
        var release = _discTree.SelectedRelease;
        var moved = release?.MoveDisc(disc, by);
        if (release is null || moved is null)
            return;

        SaveProject();
        RefreshDiscs();
        SelectRow(disc);
        SetStatus($"Moved \"{disc.Label}\" to {Ordinal(moved.Value + 1)} place in \"{release.Name}\".");
    }

    private void MoveSelectedRelease(int by)
    {
        var folder = _folder;
        var release = _discTree.SelectedRelease;
        if (folder is null || release is null)
            return;
        var moved = folder.Project.MoveRelease(release, by);
        if (moved is null)
            return;
        var to = moved.Value;

        SaveProject();
        RefreshDiscs();
        SelectRow(release);
        SetStatus(to == 0
            ? $"\"{release.Name}\" is now the preferred release."
            : $"Moved \"{release.Name}\" to {Ordinal(to + 1)} place.");
    }

    // ---- Releases ----

    private void On_ReleaseDialog_Canceled() => _addAfterCreatingRelease = null;

    private void On_ReleaseDialog_Confirmed()
    {
        var folder = _folder;
        if (folder is null)
            return;

        var editing = _releaseDialog.EditingRelease is not null;
        var release = _releaseDialog.Apply(folder.Project);
        SetStatus(editing ? $"Updated release \"{release.Name}\"." : $"Created release \"{release.Name}\".");
        SaveProject();
        RefreshDiscs();
        SelectRow(release);

        var source = _addAfterCreatingRelease;
        if (source is not null)
        {
            _addAfterCreatingRelease = null;
            BeginAddDisc(source.Value, release);
        }
    }

    // ---- Adding discs ----

    private void On_AddDiscPicker_SourceChosen(AddDiscSource source) => BeginAddDisc(source, _addDiscPicker.Release);

    private void On_AddDiscPicker_LibraryRescanRequested() => EmitSignal(SignalName.LibraryRescanRequested);

    private void BeginAddDisc(AddDiscSource source, Release? release)
    {
        var folder = _folder;
        var services = _discServices;
        if (folder is null || services is null)
            return;

        release ??= _discTree.SelectedRelease;
        if (release is not null)
        {
            _addDiscPicker.Open(source, release, folder.Project, services, Server?.Client);
        }
        else if (folder.Project.Releases.Count == 0)
        {
            // Every disc belongs to a release, so make the first one now and carry on afterwards.
            _addAfterCreatingRelease = source;
            _releaseDialog.PopupForNew("");
        }
        else
        {
            ShowError("Choose a Release", "Select the release to add discs to (or one of its discs), then try again.");
        }
    }

    private void On_AddDiscPicker_DiscsChosen()
    {
        var folder = _folder;
        var services = _discServices;
        if (folder is null || services is null)
            return;

        var release = _addDiscPicker.Release;
        var problems = _addDiscPicker.Problems;
        if (release is null || !folder.Project.Releases.Contains(release))
        {
            ShowError("Couldn't Add Disc", "The release these discs were being added to no longer exists.");
            return;
        }

        var added = new List<(ProjectDisc Disc, DiscSource? Source)>();
        foreach (var source in _addDiscPicker.ChosenSources)
        {
            var existing = folder.Project.FindDisc(source.Fingerprint);
            if (existing is not null)
            {
                var owner = folder.Project.FindRelease(existing)?.Name;
                problems.Add($"{source.Name}: this disc is already in the project as \"{existing.Label}\" ({owner}).");
                continue;
            }

            var fromLibrary = services.Library.Find(source.Fingerprint)?.Path == source.Path;
            var disc = folder.AddDisc(release, source, isManual: !fromLibrary);
            disc.CheckEveryFrame = _addDiscPicker.ChecksEveryFrame(source.Format);
            _discTree.SetResolution(disc, new DiscResolution(DiscStatus.Found, source, null));
            added.Add((disc, source));
        }

        if (added.Count > 0)
        {
            SaveProject();
            RefreshDiscs();
            SetStatus(added.Count == 1
                ? $"Added disc \"{added[0].Disc.Label}\" to \"{release.Name}\"."
                : $"Added {added.Count} discs to \"{release.Name}\".");
            Reader?.ReadUnread(folder, added);
            LookOnServer();
        }

        if (problems.Count > 0)
            ShowError(problems.Count == 1 ? "Couldn't Add Disc" : "Some Discs Couldn't Be Added", string.Join("\n\n", problems));
    }

    // ---- Locating a disc ----

    private async void On_LocateDiscPicker_PathChosen(string path)
    {
        var disc = _locateDiscPicker.Disc;
        var folder = _folder;
        var services = _discServices;
        if (disc is null || folder is null || services is null)
            return;

        DiscResolution resolution;
        try
        {
            resolution = await Task.Run(() =>
            {
                var located = services.Resolver.Locate(disc, path, folder.Cache);
                folder.Cache.Save();
                services.Cache.Save();
                return located;
            });
        }
        catch (Exception ex)
        {
            ShowError("Can't Use This Disc", ex.Message);
            return;
        }

        if (folder != _folder || folder.Project.FindRelease(disc) is null)
            return;

        _discTree.SetResolution(disc, resolution);
        SetStatus(resolution.Status == DiscStatus.Found
            ? $"Located \"{disc.Label}\"."
            : $"Located \"{disc.Label}\", but it may be a different release of this disc.");
        // Locating a disc also tries again to read one that couldn't be
        Reader?.ReadUnread(folder, [(disc, resolution.Source)], retry: true);
    }

    // ---- Removing ----

    private void On_RemoveButton_Pressed()
    {
        _removeTarget = _discTree.Selected;
        switch (_removeTarget)
        {
            case Release release:
                var discCount = release.Discs.Count;
                _removeDialog.Title = "Remove Release";
                _removeDialog.DialogText = discCount == 0
                    ? $"Remove the release \"{release.Name}\" from this project?"
                    : $"Remove the release \"{release.Name}\" and its {discCount} {(discCount == 1 ? "disc" : "discs")} from this project?"
                        + "\n\nThe discs' files on your computer will not be deleted.";
                break;
            case ProjectDisc disc:
                _removeDialog.Title = "Remove Disc";
                _removeDialog.DialogText =
                    $"Remove \"{disc.Label}\" from this project?\n\nThe disc's files on your computer will not be deleted.";
                break;
            default:
                return;
        }

        _removeDialog.PopupCentered();
    }

    private void On_RemoveDialog_Confirmed()
    {
        var folder = _folder;
        if (folder is null)
            return;

        List<ProjectDisc> removed;
        switch (_removeTarget)
        {
            case Release release:
                removed = [.. release.Discs];
                folder.RemoveRelease(release);
                SetStatus($"Removed release \"{release.Name}\".");
                break;
            case ProjectDisc disc:
                removed = [disc];
                folder.RemoveDisc(disc);
                SetStatus($"Removed disc \"{disc.Label}\".");
                break;
            default:
                return;
        }

        // A removed disc that no release has any more isn't read
        foreach (var disc in removed.Where(d => !folder.Project.Discs.Any(other => other.Fingerprint == d.Fingerprint)))
            Reader?.Stop(disc);
        _removeTarget = null;
        SaveProject();
        RefreshDiscs();
    }

    // ---- Helpers ----

    private void SaveProject()
    {
        try
        {
            _folder?.Save();
            _folder?.Cache.Save();
        }
        catch (Exception ex)
        {
            ShowError("Couldn't Save Project", ex.Message);
        }
    }

    private void SelectRow(object target)
    {
        _discTree.Select(target);
        UpdateToolbar();
    }

    /// <summary>Where to show a menu for a row button just clicked.</summary>
    private Rect2I AtMouse() => new((Vector2I)(GetScreenPosition() + GetLocalMousePosition()), Vector2I.Zero);

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    private void ShowError(string title, string message)
    {
        _errorDialog.Title = title;
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    private static string Ordinal(int n) => n + ((n % 100) is 11 or 12 or 13 ? "th" : (n % 10) switch { 1 => "st", 2 => "nd", 3 => "rd", _ => "th" });

    /// <summary>How many of the discs were found (and how many of those are on the render server).</summary>
    private string DescribeDiscs(IReadOnlyList<ProjectDisc> discs)
    {
        if (discs.Count == 0)
            return "No discs yet. Create a release and add its discs to get started.";

        var resolutions = discs.Select(_discTree.ResolutionOf).ToList();
        var servers = resolutions.Select(r => r?.Source?.ServerName).OfType<string>().ToList();
        var server = servers.Count > 0 ? $" ({servers.Count} on \"{servers[0]}\")" : "";
        if (resolutions.All(r => r?.Status == DiscStatus.Found))
            return (discs.Count == 1 ? "Disc found" : $"All {discs.Count} discs found") + server + ".";

        var parts = new List<string>();
        AddPart(DiscStatus.Found, "found" + server);
        AddPart(DiscStatus.ProbableMatch, "probable match");
        AddPart(DiscStatus.Missing, "missing");
        return string.Join(", ", parts) + ".";

        void AddPart(DiscStatus status, string label)
        {
            var count = resolutions.Count(r => (r?.Status ?? DiscStatus.Missing) == status);
            if (count > 0)
                parts.Add($"{count} {label}");
        }
    }
}
