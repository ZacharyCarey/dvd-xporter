using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A project's releases, each with its discs beneath it, and each disc's status on this computer (or on the render
/// server, for one that's only there).
/// Discs are renamed in place; the rows' buttons ask for the rest.
/// </summary>
public partial class DiscTree : Tree
{
    private enum Column
    {
        Status,
        Label,
        Format,
        Location,
    }

    private enum RowButton
    {
        AddDisc,
        EditRelease,
        Locate,
        Rescan,

        // Only says the render server has the disc
        OnServer,
    }

    [Signal]
    public delegate void SelectionChangedEventHandler();

    /// <summary>The user wants to add discs to <see cref="ClickedRelease"/>.</summary>
    [Signal]
    public delegate void AddDiscRequestedEventHandler();

    /// <summary>The user wants to edit <see cref="ClickedRelease"/>.</summary>
    [Signal]
    public delegate void EditReleaseRequestedEventHandler();

    /// <summary>The user wants to say where <see cref="ClickedDisc"/> is.</summary>
    [Signal]
    public delegate void LocateRequestedEventHandler();

    [Signal]
    public delegate void RescanRequestedEventHandler();

    /// <summary>A disc's label was changed.</summary>
    [Signal]
    public delegate void DiscRenamedEventHandler();

    [Export] public Texture2D DiscIcon { get; set; } = null!;
    [Export] public Texture2D ReleaseIcon { get; set; } = null!;
    [Export] public Texture2D FoundIcon { get; set; } = null!;
    [Export] public Texture2D ProbableIcon { get; set; } = null!;
    [Export] public Texture2D MissingIcon { get; set; } = null!;
    [Export] public Texture2D CheckingIcon { get; set; } = null!;
    [Export] public Texture2D ServerIcon { get; set; } = null!;
    [Export] public Texture2D LocateIcon { get; set; } = null!;
    [Export] public Texture2D RescanIcon { get; set; } = null!;
    [Export] public Texture2D AddIcon { get; set; } = null!;
    [Export] public Texture2D EditIcon { get; set; } = null!;

    /// <summary>Why the order of releases matters, added to their tooltips when there's more than one.</summary>
    public string OrderHelp { get; set; } = "";

    /// <summary>The release whose row was last clicked or double-clicked.</summary>
    public Release? ClickedRelease { get; private set; }

    /// <summary>The disc whose row button was last clicked.</summary>
    public ProjectDisc? ClickedDisc { get; private set; }

    /// <summary>The selected release or disc.</summary>
    public object? Selected
    {
        get
        {
            var item = GetSelected();
            return item is not null ? _rows.GetValueOrDefault(item.GetInstanceId())?.Target : null;
        }
    }

    /// <summary>The selected release, the release of the selected disc, or the only release.</summary>
    public Release? SelectedRelease => Selected switch
    {
        Release release => release,
        ProjectDisc disc => _project?.FindRelease(disc),
        _ => _project?.Releases.Count == 1 ? _project.Releases[0] : null,
    };

    private Project? _project;
    private string? _libraryRoot;

    // Null = still being checked
    private readonly Dictionary<ProjectDisc, DiscResolution?> _resolutions = [];

    // The render server's discs, by fingerprint; null if there's no server, or it couldn't be reached
    private IReadOnlySet<string>? _serverDiscs;
    private string _serverName = "";

    // By their tree item's instance ID. Rebuilt on every refresh.
    private readonly Dictionary<ulong, Row> _rows = [];

    public override void _Ready()
    {
        // Column titles and widths can't be set in a scene (the column count is in it)
        SetColumnTitle((int)Column.Status, "Status");
        SetColumnTitle((int)Column.Label, "Release / Disc");
        SetColumnTitle((int)Column.Format, "Format");
        SetColumnTitle((int)Column.Location, "Location");
        SetColumnExpand((int)Column.Status, false);
        SetColumnCustomMinimumWidth((int)Column.Status, 175);
        SetColumnExpandRatio((int)Column.Label, 2);
        SetColumnExpand((int)Column.Format, false);
        SetColumnCustomMinimumWidth((int)Column.Format, 110);
        SetColumnExpandRatio((int)Column.Location, 3);
        SetColumnClipContent((int)Column.Location, true);
    }

    /// <summary>
    /// Shows <paramref name="project"/>'s releases (none for null), with discs in the Disc Library at
    /// <paramref name="libraryRoot"/> shown relative to it.
    /// </summary>
    public void ShowProject(Project? project, string? libraryRoot)
    {
        _project = project;
        _libraryRoot = libraryRoot;
        _resolutions.Clear();
        _serverDiscs = null;
        Refresh();
    }

    /// <summary>
    /// Marks the discs the render server called <paramref name="serverName"/> has (by fingerprint). Null shows nothing,
    /// for no server, or one that couldn't be reached.
    /// </summary>
    public void ShowServerDiscs(IReadOnlySet<string>? fingerprints, string serverName)
    {
        _serverDiscs = fingerprints;
        _serverName = serverName;
        foreach (var row in _rows.Values.OfType<DiscRow>())
            row.Update();
    }

    /// <summary>Shows the discs as being checked.</summary>
    public void MarkChecking(IEnumerable<ProjectDisc> discs)
    {
        foreach (var disc in discs)
            SetResolution(disc, null);
    }

    /// <summary>Shows where <paramref name="disc"/> was found (null: still being checked).</summary>
    public void SetResolution(ProjectDisc disc, DiscResolution? resolution)
    {
        _resolutions[disc] = resolution;
        foreach (var row in _rows.Values.OfType<DiscRow>().Where(r => r.Disc == disc))
        {
            row.Update();
            row.ReleaseRow.Update();
        }
    }

    /// <summary>Rebuilds the rows after releases or discs are added, removed or moved, keeping the selection.</summary>
    public void Refresh()
    {
        var selected = Selected;
        Clear();
        _rows.Clear();
        if (_project is null)
            return;

        var root = CreateItem();
        foreach (var release in _project.Releases)
        {
            var releaseRow = Add(new ReleaseRow(this, root, release));
            foreach (var disc in release.Discs)
                Add(new DiscRow(this, releaseRow, disc));
        }

        if (selected is not null)
            Select(selected, scroll: false);
    }

    /// <summary>Selects the row showing <paramref name="target"/>, a release or disc.</summary>
    public void Select(object target, bool scroll = true)
    {
        var row = _rows.Values.FirstOrDefault(r => r.Target == target);
        if (row is null)
            return;

        row.Item.Select((int)Column.Label);
        if (scroll)
            ScrollToItem(row.Item);
    }

    /// <summary>Starts renaming the selected disc.</summary>
    public void RenameSelected() => EditSelected(true);

    private T Add<T>(T row) where T : Row
    {
        _rows[row.Item.GetInstanceId()] = row;
        return row;
    }

    /// <summary>Where the disc was found, or null while it's being checked.</summary>
    public DiscResolution? ResolutionOf(ProjectDisc disc) => _resolutions.GetValueOrDefault(disc);


    private void On_DiscTree_ItemSelected() => EmitSignal(SignalName.SelectionChanged);

    private void On_DiscTree_NothingSelected()
    {
        DeselectAll();
        EmitSignal(SignalName.SelectionChanged);
    }

    private void On_DiscTree_ItemActivated()
    {
        if (Selected is not Release release)
            return;

        ClickedRelease = release;
        EmitSignal(SignalName.EditReleaseRequested);
    }

    private void On_DiscTree_ItemEdited()
    {
        var item = GetEdited();
        if (item is not null && _rows.GetValueOrDefault(item.GetInstanceId()) is DiscRow row && row.Rename())
            EmitSignal(SignalName.DiscRenamed);
    }

    private void On_DiscTree_ButtonClicked(TreeItem item, long column, long id, long mouseButtonIndex)
    {
        if ((RowButton)id == RowButton.OnServer)
            return;

        var target = _rows.GetValueOrDefault(item.GetInstanceId())?.Target;
        ClickedRelease = target as Release;
        ClickedDisc = target as ProjectDisc;
        var signal = (RowButton)id switch
        {
            RowButton.AddDisc => SignalName.AddDiscRequested,
            RowButton.EditRelease => SignalName.EditReleaseRequested,
            RowButton.Locate => SignalName.LocateRequested,
            _ => SignalName.RescanRequested,
        };
        EmitSignal(signal);
    }
}
