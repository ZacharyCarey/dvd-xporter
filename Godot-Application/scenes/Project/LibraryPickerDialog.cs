using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Remote;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Lists every disc found in the Disc Library, searchable by disc or folder name, so discs can be added to a project.
/// With a render server, its Disc Library's discs are listed too: those only there are marked, and are added as
/// <see cref="RemoteDiscSource"/>s, used through the server.
/// </summary>
public partial class LibraryPickerDialog : ConfirmationDialog
{
    // The tree's default text is already mid-grey, so available discs are brightened to contrast with dimmed ones.
    private static readonly Color AvailableColor = new(0.95f, 0.95f, 0.95f);
    private static readonly Color InProjectColor = new(0.95f, 0.95f, 0.95f, 0.3f);

    private enum PickerColumn
    {
        Disc,
        Folder,
        Format,
    }

    private sealed record Entry(DiscSource Source, string Name, string? Folder, bool InProject);

    /// <summary>Raised when discs have been chosen: they're <see cref="ChosenSources"/>.</summary>
    [Signal]
    public delegate void DiscsChosenEventHandler();

    [Signal]
    public delegate void RescanRequestedEventHandler();

    [Export] public Texture2D DiscIcon { get; set; } = null!;
    [Export] public Texture2D InProjectIcon { get; set; } = null!;
    [Export] public Texture2D ServerIcon { get; set; } = null!;

    /// <summary>The discs last chosen.</summary>
    public IReadOnlyList<DiscSource> ChosenSources { get; private set; } = [];

    /// <summary>Which of the discs chosen are to have every frame checked for interlacing.</summary>
    public EveryFrameDiscs EveryFrame => (EveryFrameDiscs)_everyFrameOption.Selected;

    private LineEdit _searchEdit = null!;
    private Tree _discTree = null!;
    private Label _emptyLabel = null!;
    private Label _countLabel = null!;
    private OptionButton _everyFrameOption = null!;

    private DiscLibrary? _library;
    private HashSet<string> _projectFingerprints = [];
    private List<Entry> _entries = [];
    private RenderServerClient? _server;
    // The server's discs, once it's answered; and why it couldn't, if it couldn't
    private List<RemoteLibraryDisc>? _serverDiscs;
    private string? _serverProblem;

    public override void _Ready()
    {
        _searchEdit = GetNode<LineEdit>("%SearchEdit");
        _discTree = GetNode<Tree>("%DiscTree");
        _emptyLabel = GetNode<Label>("%EmptyLabel");
        _countLabel = GetNode<Label>("%CountLabel");
        _everyFrameOption = GetNode<OptionButton>("%EveryFrameOption");

        // Column titles and widths can't be set in a scene (the column count is in it)
        _discTree.SetColumnTitle((int)PickerColumn.Disc, "Disc");
        _discTree.SetColumnTitle((int)PickerColumn.Folder, "Folder");
        _discTree.SetColumnTitle((int)PickerColumn.Format, "Format");
        _discTree.SetColumnExpandRatio((int)PickerColumn.Disc, 3);
        _discTree.SetColumnExpandRatio((int)PickerColumn.Folder, 3);
        _discTree.SetColumnExpand((int)PickerColumn.Format, false);
        _discTree.SetColumnCustomMinimumWidth((int)PickerColumn.Format, 80);
    }

    private void On_SearchEdit_TextChanged(string text) => ShowMatches();

    private void On_DiscTree_MultiSelected(TreeItem item, long column, bool selected) => UpdateOkButton();

    private void On_DiscTree_ItemActivated()
    {
        if (GetSelected().Count > 0)
        {
            Hide();
            ChooseSelected();
        }
    }

    private void On_RescanButton_Pressed() => EmitSignal(SignalName.RescanRequested);

    private void On_Confirmed() => ChooseSelected();

    /// <param name="server">The render server, whose Disc Library is listed too; null for none.</param>
    public void PopupFor(DiscLibrary library, IEnumerable<string> projectFingerprints, RenderServerClient? server)
    {
        _everyFrameOption.Select((int)EveryFrameDiscs.Dvds);
        _library = library;
        _projectFingerprints = projectFingerprints.ToHashSet(StringComparer.OrdinalIgnoreCase);
        _server = server;
        _serverDiscs = null;
        _serverProblem = null;
        _searchEdit.Text = "";
        Reload();
        PopupCentered();
        _searchEdit.GrabFocus();
        if (server is not null)
            LookOnServer(server);
    }

    private async void LookOnServer(RenderServerClient server)
    {
        List<RemoteLibraryDisc>? discs = null;
        string? problem = null;
        try
        {
            discs = await server.ListLibraryAsync();
        }
        catch (RenderServerException ex)
        {
            problem = ex.Message;
        }

        if (server != _server || !Visible)
            return;
        _serverDiscs = discs;
        _serverProblem = problem;
        Reload();
    }

    /// <summary>The library was scanned, here or on the server: lists both again.</summary>
    public void OnLibraryChanged()
    {
        Reload();
        var server = _server;
        if (server is not null && Visible)
            LookOnServer(server);
    }

    /// <summary>Re-reads the library, e.g. after a rescan, keeping the current search.</summary>
    public void Reload()
    {
        if (_library is null)
            return;

        var here = _library.Discs
            .Select(source => new Entry(source, source.Name, _library.GetParentFolder(source), _projectFingerprints.Contains(source.Fingerprint)))
            .ToList();
        var fingerprints = here.Select(e => e.Source.Fingerprint).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var server = _server;
        var onlyOnServer = server is null ? [] : (_serverDiscs ?? [])
            .Where(d => !fingerprints.Contains(d.Fingerprint))
            .Select(d => new Entry(new RemoteDiscSource(server, d), d.Label, d.Folder, _projectFingerprints.Contains(d.Fingerprint)));
        _entries = here.Concat(onlyOnServer)
            .OrderBy(e => e.Folder ?? "", StringComparer.OrdinalIgnoreCase)
            .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        ShowMatches();
    }

    private void ShowMatches()
    {
        _discTree.Clear();
        var root = _discTree.CreateItem();

        // Every search word must appear in the disc name or its folder.
        var words = _searchEdit.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var matches = 0;
        for (var i = 0; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (!words.All(w => Contains(entry.Name, w) || Contains(entry.Folder, w)))
                continue;

            matches++;
            var item = _discTree.CreateItem(root);
            item.SetMetadata((int)PickerColumn.Disc, i);
            item.SetText((int)PickerColumn.Disc, entry.InProject ? $"{entry.Name}  (already in project)" : entry.Name);
            item.SetIcon((int)PickerColumn.Disc, entry.InProject ? InProjectIcon : entry.Source.IsHere ? DiscIcon : ServerIcon);
            item.SetIconMaxWidth((int)PickerColumn.Disc, 20);
            item.SetText((int)PickerColumn.Folder, entry.Folder ?? "");
            item.SetText((int)PickerColumn.Format, DiscResolver.FormatName(entry.Source.Format));

            var where = entry.Source.IsHere ? entry.Source.Path
                : $"Only on \"{entry.Source.ServerName}\": it's read and played from there.\n{entry.Source.Path}";
            foreach (var column in Enum.GetValues<PickerColumn>())
            {
                item.SetTooltipText((int)column, entry.InProject ? $"Already in this project\n{where}" : where);
                item.SetSelectable((int)column, !entry.InProject);
                item.SetCustomColor((int)column, entry.InProject ? InProjectColor : AvailableColor);
            }
        }

        _emptyLabel.Text = _library?.RootPath is null && _entries.Count == 0
            ? "The Disc Library hasn't been scanned yet.\nSet it in Settings, or press Rescan."
            : _entries.Count == 0
                ? "No discs were found in the Disc Library."
                : $"No discs match \"{_searchEdit.Text.Trim()}\".";
        _emptyLabel.Visible = matches == 0;
        _discTree.Visible = matches > 0;

        var inProject = _entries.Count(e => e.InProject);
        var onlyOnServer = _entries.Count(e => !e.Source.IsHere);
        var server = _server is null ? ""
            : _serverProblem is not null ? $"  ·  Couldn't look on \"{_server.Name}\""
            : _serverDiscs is null ? $"  ·  Looking on \"{_server.Name}\"..."
            : onlyOnServer > 0 ? $"  ·  {onlyOnServer} only on \"{_server.Name}\""
            : "";
        _countLabel.Text = $"{_entries.Count} {(_entries.Count == 1 ? "disc" : "discs")} in library"
            + server
            + (inProject > 0 ? $"  ·  {inProject} already in this project" : "")
            + (words.Length > 0 ? $"  ·  {matches} shown" : "");
        _countLabel.TooltipText = _serverProblem ?? "";
        UpdateOkButton();
    }

    private void ChooseSelected()
    {
        var selected = GetSelected();
        if (selected.Count == 0)
            return;
        ChosenSources = selected.Select(e => e.Source).ToList();
        EmitSignal(SignalName.DiscsChosen);
    }

    private List<Entry> GetSelected()
    {
        var selected = new List<Entry>();
        for (var item = _discTree.GetNextSelected(null); item is not null; item = _discTree.GetNextSelected(item))
        {
            var entry = _entries[item.GetMetadata((int)PickerColumn.Disc).AsInt32()];
            if (!entry.InProject)
                selected.Add(entry);
        }

        return selected;
    }

    private void UpdateOkButton()
    {
        var count = GetSelected().Count;
        GetOkButton().Disabled = count == 0;
        GetOkButton().Text = count > 1 ? $"Add {count} Discs" : "Add Disc";
    }

    private static bool Contains(string? text, string word) =>
        text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;
}
