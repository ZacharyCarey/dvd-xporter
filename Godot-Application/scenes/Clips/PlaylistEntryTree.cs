using System.Collections.Generic;
using DvdXporter.Core;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A playlist's video files in order, with their lengths. Files are dragged in from the disc's list, dragged to reorder,
/// and removed with their row's button.
/// </summary>
public partial class PlaylistEntryTree : Tree
{
    private enum Column
    {
        Number,
        Name,
        Duration,
    }

    private enum RowButton
    {
        Remove,
    }

    /// <summary>An entry was selected: it's <see cref="SelectedEntry"/>.</summary>
    [Signal]
    public delegate void EntrySelectedEventHandler();

    /// <summary>An entry was double-clicked, to name its file.</summary>
    [Signal]
    public delegate void EntryActivatedEventHandler();

    /// <summary>Entries were added, moved or removed.</summary>
    [Signal]
    public delegate void EntriesChangedEventHandler();

    [Export] public Texture2D RemoveIcon { get; set; } = null!;

    public PlaylistEntry? SelectedEntry => SelectedRow?.Entry;

    private Row? SelectedRow
    {
        get
        {
            var item = GetSelected();
            return item is not null ? _rows.GetValueOrDefault(item.GetInstanceId()) : null;
        }
    }

    private ProjectDisc? _disc;
    private ClipPlaylist? _playlist;
    private DiscContents? _contents;

    // By their tree item's instance ID, in the playlist's order. Rebuilt with the list.
    private readonly Dictionary<ulong, Row> _rows = [];

    public override void _Ready()
    {
        // Column titles and widths can't be set in a scene (the column count is in it), nor can drag and drop
        SetColumnTitle((int)Column.Number, "#");
        SetColumnTitle((int)Column.Name, "Video File");
        SetColumnTitle((int)Column.Duration, "Length");
        SetColumnExpand((int)Column.Number, false);
        SetColumnCustomMinimumWidth((int)Column.Number, 32);
        SetColumnExpand((int)Column.Duration, false);
        SetColumnCustomMinimumWidth((int)Column.Duration, 100);
        SetDragForwarding(
            Callable.From<Vector2, Variant>(GetDragData),
            Callable.From<Vector2, Variant, bool>(CanDrop),
            Callable.From<Vector2, Variant>(Drop));
    }

    public override void _Notification(int what)
    {
        // Drop lines only show while something is being dragged over the list
        if (what == NotificationDragEnd)
            DropModeFlags = (int)DropModeFlagsEnum.Disabled;
    }

    /// <summary>Lists <paramref name="playlist"/>'s entries (none if it's null), with lengths from <paramref name="contents"/>.</summary>
    public void Show(ProjectDisc? disc, ClipPlaylist? playlist, DiscContents? contents)
    {
        _disc = disc;
        _playlist = playlist;
        _contents = contents;
        Clear();
        _rows.Clear();
        var root = CreateItem();
        for (var i = 0; i < (playlist?.Entries.Count ?? 0); i++)
        {
            var row = new Row(this, root, i, playlist!.Entries[i]);
            _rows[row.Item.GetInstanceId()] = row;
        }
    }

    /// <summary>Shows the files' names again, after one's renamed.</summary>
    public void RefreshNames()
    {
        foreach (var row in _rows.Values)
            row.ShowName();
    }

    /// <summary>Puts <paramref name="clip"/> into the playlist at <paramref name="index"/>, and selects it.</summary>
    public void Insert(string clip, int index)
    {
        if (_playlist is null)
            return;
        Changed(_playlist.Insert(clip, index));
    }

    private void Changed(int select)
    {
        Show(_disc, _playlist, _contents);
        var items = GetRoot().GetChildren();
        if (items.Count > 0)
            items[Mathf.Clamp(select, 0, items.Count - 1)].Select((int)Column.Name);
        EmitSignal(SignalName.EntriesChanged);
    }

    private void On_EntryTree_ItemSelected()
    {
        if (SelectedEntry is not null)
            EmitSignal(SignalName.EntrySelected);
    }

    private void On_EntryTree_ItemActivated() => EmitSignal(SignalName.EntryActivated);

    private void On_EntryTree_ButtonClicked(TreeItem item, long column, long id, long mouseButtonIndex)
    {
        var row = _rows.GetValueOrDefault(item.GetInstanceId());
        if (_playlist is null || id != (int)RowButton.Remove || row is null)
            return;

        _playlist.Entries.RemoveAt(row.Index);
        Show(_disc, _playlist, _contents);
        EmitSignal(SignalName.EntriesChanged);
    }

    private Variant GetDragData(Vector2 atPosition)
    {
        var item = GetItemAtPosition(atPosition);
        if (item is null)
            return default;

        var row = _rows.GetValueOrDefault(item.GetInstanceId());
        if (row is null)
            return default;

        SetDragPreview(new Label { Text = ClipText.Describe(_disc, row.Entry.Clip) });
        return new Godot.Collections.Dictionary { ["kind"] = "entry", ["index"] = row.Index };
    }

    private bool CanDrop(Vector2 atPosition, Variant data)
    {
        if (_playlist is null || data.VariantType != Variant.Type.Dictionary)
            return false;

        var kind = data.AsGodotDictionary().GetValueOrDefault("kind").AsString();
        if (kind is not ("clip" or "entry"))
            return false;

        DropModeFlags = (int)DropModeFlagsEnum.Inbetween;
        return true;
    }

    private void Drop(Vector2 atPosition, Variant data)
    {
        if (_playlist is null)
            return;

        // Where the drop line was: before or after the row under the mouse, or at the end
        var target = _playlist.Entries.Count;
        var item = GetItemAtPosition(atPosition);
        if (item is not null)
        {
            var row = _rows.GetValueOrDefault(item.GetInstanceId());
            if (row is not null)
                target = row.Index + (GetDropSectionAtPosition(atPosition) > 0 ? 1 : 0);
        }

        var dictionary = data.AsGodotDictionary();
        if (dictionary["kind"].AsString() == "clip")
            Insert(dictionary["clip"].AsString(), target);

        var moved = _playlist.Move(dictionary["index"].AsInt32(), target);
        if (moved is not null)
            Changed(moved.Value);
    }

    /// <summary>A playlist entry's row: its place, its file's name, and how long it plays.</summary>
    private sealed class Row
    {
        private readonly PlaylistEntryTree _tree;

        public Row(PlaylistEntryTree tree, TreeItem parent, int index, PlaylistEntry entry)
        {
            _tree = tree;
            Index = index;
            Entry = entry;
            Item = tree.CreateItem(parent);
            Item.SetText((int)Column.Number, $"{index + 1}");
            Item.SetCustomColor((int)Column.Number, ClipText.DimTextColor);
            ShowName();
            Item.SetTooltipText((int)Column.Name, entry.Clip);
            var length = entry.DurationIn(tree._contents);
            var duration = length is not null ? ClipText.FormatTime(length.Value) : "";
            Item.SetText((int)Column.Duration, entry.IsTrimmed ? $"{duration} (part)" : duration);
            Item.SetTextAlignment((int)Column.Duration, HorizontalAlignment.Right);
            Item.AddButton((int)Column.Duration, tree.RemoveIcon, (int)RowButton.Remove, false, "Remove from the playlist");
            if (entry.IsTrimmed)
            {
                Item.SetTooltipText((int)Column.Duration,
                    $"Plays part of the file: {entry.InTime.ToPreciseTime()?.ToString() ?? "start"} to {entry.OutTime.ToPreciseTime()?.ToString() ?? "end"}, on the disc's own timeline.");
            }
        }

        /// <summary>Its place in the playlist.</summary>
        public int Index { get; }

        public PlaylistEntry Entry { get; }

        public TreeItem Item { get; }

        public void ShowName() => ClipText.ShowName(Item, (int)Column.Name, _tree._disc, Entry.Clip);
    }
}
