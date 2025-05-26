using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The files to export, under their library folders, each with a tick for whether to export it, its problems, the
/// release it's made from (a dropdown when there's a choice) and how its export is going.
/// </summary>
public partial class ExportFileTree : Tree
{
    private enum Column
    {
        Pick,
        Problems,
        Disc,
        File,
        Source,
        Canvas,
        Length,
        Run,
    }

    [Signal]
    public delegate void SelectionChangedEventHandler();

    /// <summary>Files were ticked or unticked (see <see cref="ExportPicks"/>).</summary>
    [Signal]
    public delegate void PicksChangedEventHandler();

    /// <summary><see cref="PickedSource"/> was picked in a row's Source dropdown, to make <see cref="SourceRowFile"/> from.</summary>
    [Signal]
    public delegate void SourcePickedEventHandler();

    [Export] public Texture2D MissingIcon { get; set; } = null!;

    /// <summary>The file whose version was last picked in its Source dropdown.</summary>
    public ExportFile? SourceRowFile { get; private set; }

    /// <summary>The version picked for <see cref="SourceRowFile"/>.</summary>
    public ExportFile? PickedSource { get; private set; }

    public ExportFile? SelectedFile => SelectedRow is FileRow row ? row.File : null;

    public string? SelectedGroup => SelectedRow is GroupRow row ? row.Group : null;

    private Row? SelectedRow
    {
        get
        {
            var item = GetSelected();
            return item is not null ? _rows.GetValueOrDefault(item.GetInstanceId()) : null;
        }
    }

    private ExportPicks _picks = new();
    private IReadOnlyDictionary<string, ExportFileRun> _runs = new Dictionary<string, ExportFileRun>();
    private bool _exporting;
    private readonly List<GroupRow> _groupRows = [];

    // By their tree item's instance ID. Rebuilt with the list.
    private readonly Dictionary<ulong, Row> _rows = [];
    private readonly HashSet<string> _collapsed = [];
    // The selected row's key, kept across rebuilds
    private string? _selectedKey;

    public override void _Ready()
    {
        (Column Column, string Title, int Width, bool Expand)[] columns =
        [
            // Room for a file's tickbox past its group's indent and fold arrow; any narrower and the box is drawn
            // over the next column, where clicking it does nothing
            (Column.Pick, "", 56, false),
            (Column.Problems, "", 30, false),
            (Column.Disc, "", 30, false),
            (Column.File, "File", 260, true),
            (Column.Source, "Source", 220, false),
            (Column.Canvas, "Canvas", 140, false),
            (Column.Length, "Length", 80, false),
            (Column.Run, "Export", 130, false),
        ];
        // Column titles and widths can't be set in a scene (the column count is in it)
        foreach (var (column, title, width, expand) in columns)
        {
            SetColumnTitle((int)column, title);
            SetColumnCustomMinimumWidth((int)column, width);
            SetColumnExpand((int)column, expand);
            SetColumnClipContent((int)column, true);
        }

        SetColumnTitleTooltipText((int)Column.Pick, "Whether to export the file");
        SetColumnTitleTooltipText((int)Column.Problems, "Problems with the file");
        SetColumnTitleTooltipText((int)Column.Disc, "Whether its discs are on this computer");
        SetColumnTitleTooltipText((int)Column.Run, "How its export went");
        SetColumnTitleTooltipText((int)Column.Source,
            "The release it's made from. When several releases have it, the one highest on the project overview is used; "
            + "click to pick another of the ones you have.");
        SetColumnExpandRatio((int)Column.File, 3);
    }

    /// <summary>For another project: forgets which groups were folded and which row was selected.</summary>
    public void Forget()
    {
        _collapsed.Clear();
        _selectedKey = null;
    }

    /// <summary>
    /// Shows <paramref name="files"/> (or <paramref name="emptyText"/> if there are none), keeping the selection.
    /// While <paramref name="exporting"/>, nothing can be changed.
    /// </summary>
    public void Show(IReadOnlyList<ExportFile> files, ExportPicks picks, IReadOnlyDictionary<string, ExportFileRun> runs,
        bool exporting, string emptyText)
    {
        _picks = picks;
        _runs = runs;
        _exporting = exporting;
        Clear();
        _rows.Clear();
        _groupRows.Clear();
        var root = CreateItem();
        if (files.Count == 0)
            AddEmptyRow(root, emptyText);

        foreach (var group in files.Select(f => f.Group).Distinct())
        {
            var groupRow = Add(new GroupRow(this, root, group, files.Count(f => f.Group == group)));
            _groupRows.Add(groupRow);
            foreach (var file in files.Where(f => f.Group == group))
                groupRow.Files.Add(Add(new FileRow(this, groupRow, file)));
        }

        UpdateChecks();
        var selected = _rows.Values.FirstOrDefault(r => r.Key == _selectedKey);
        if (selected is not null)
        {
            selected.Item.Select((int)Column.File);
            ScrollToItem(selected.Item);
        }
        else
        {
            _selectedKey = null;
        }
    }

    /// <summary>Shows what's picked: each file's tick, and each group's (partly ticked if some are).</summary>
    public void UpdateChecks()
    {
        foreach (var groupRow in _groupRows)
            groupRow.UpdateChecks();
    }

    /// <summary>Shows how <paramref name="file"/>'s export is going in its row.</summary>
    public void UpdateRun(ExportFile file)
    {
        foreach (var row in _rows.Values.OfType<FileRow>().Where(r => r.File == file))
            row.UpdateRun();
    }

    private T Add<T>(T row) where T : Row
    {
        _rows[row.Item.GetInstanceId()] = row;
        return row;
    }

    private void AddEmptyRow(TreeItem root, string text)
    {
        var empty = CreateItem(root);
        empty.SetText((int)Column.File, text);
        empty.SetCustomColor((int)Column.File, ExportColors.Dim);
        foreach (var column in Enum.GetValues<Column>())
            empty.SetSelectable((int)column, false);
    }

    /// <summary>Ticks or unticks a file, or every file in a group that can be exported.</summary>
    private void SetPicked(Row row, bool picked)
    {
        if (row is FileRow { File.CanExport: true } fileRow)
            _picks.Set(fileRow.File, picked);
        else if (row is GroupRow groupRow)
            _picks.Set(groupRow.Exportable, picked);

        // Godot updates the tick it was clicked in itself; the rest follow
        Callable.From(() =>
        {
            UpdateChecks();
            EmitSignal(SignalName.PicksChanged);
        }).CallDeferred();
    }

    private void On_FileTree_ItemSelected()
    {
        _selectedKey = SelectedRow?.Key;
        EmitSignal(SignalName.SelectionChanged);
    }

    private void On_FileTree_ItemActivated()
    {
        switch (SelectedRow)
        {
            case FileRow { File.CanExport: true } row:
                SetPicked(row, !_picks.IsPicked(row.File));
                break;
            case GroupRow { Exportable.Count: > 0 } row:
                SetPicked(row, !row.Exportable.All(_picks.IsPicked));
                break;
        }
    }

    private void On_FileTree_ItemCollapsed(TreeItem item)
    {
        if (_rows.GetValueOrDefault(item.GetInstanceId()) is not GroupRow row)
            return;
        if (item.Collapsed)
            _collapsed.Add(row.Group);
        else
            _collapsed.Remove(row.Group);
    }

    private void On_FileTree_ItemEdited()
    {
        var item = GetEdited();
        if (item is null)
            return;
        var row = _rows.GetValueOrDefault(item.GetInstanceId());
        if (row is null)
            return;

        if (GetEditedColumn() == (int)Column.Pick)
        {
            SetPicked(row, item.IsChecked((int)Column.Pick));
        }
        else if (GetEditedColumn() == (int)Column.Source && row is FileRow fileRow)
        {
            var source = fileRow.PickedSource();
            if (source is null)
                return;
            SourceRowFile = fileRow.File;
            PickedSource = source;
            EmitSignal(SignalName.SourcePicked);
        }
    }
}
