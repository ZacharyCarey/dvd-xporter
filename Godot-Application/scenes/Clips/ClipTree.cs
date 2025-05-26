using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A disc's video files, by the names they've been given, with their lengths. Short unnamed ones (menu loops, logos)
/// can be hidden. Files are dragged from here into a playlist.
/// </summary>
public partial class ClipTree : Tree
{
    private enum Column
    {
        Name,
        Duration,
    }

    /// <summary>A file was selected: it's <see cref="SelectedClip"/>.</summary>
    [Signal]
    public delegate void ClipSelectedEventHandler();

    /// <summary>A file was double-clicked, to name it.</summary>
    [Signal]
    public delegate void ClipActivatedEventHandler();

    /// <summary>The selected file's name on the disc, e.g. "00012.m2ts".</summary>
    public string? SelectedClip => SelectedRow?.Clip;

    private Row? SelectedRow
    {
        get
        {
            var item = GetSelected();
            return (item is not null) ? _rows.GetValueOrDefault(item.GetInstanceId()) : null;
        }
    }

    private ProjectDisc? _disc;

    // By their tree item's instance ID. Rebuilt with the list.
    private readonly Dictionary<ulong, Row> _rows = [];

    public override void _Ready()
    {
        // Column titles and widths can't be set in a scene (the column count is in it), nor can drag and drop
        SetColumnTitle((int)Column.Name, "Video File");
        SetColumnTitle((int)Column.Duration, "Length");
        SetColumnExpand((int)Column.Duration, false);
        SetColumnCustomMinimumWidth((int)Column.Duration, 70);
        SetDragForwarding(Callable.From<Vector2, Variant>(GetDragData), default, default);
    }

    /// <summary>
    /// Lists <paramref name="contents"/>' files (none if it's null), keeping the selection. With
    /// <paramref name="hideShort"/>, leaves out unnamed ones shorter than <paramref name="shortSeconds"/>, and with
    /// <paramref name="hideUnnamed"/> the rest of the unnamed ones. Returns how many each left out.
    /// </summary>
    public (int Short, int Unnamed) Show(ProjectDisc? disc, DiscContents? contents, bool hideShort, int shortSeconds, bool hideUnnamed)
    {
        var selected = SelectedClip;
        _disc = disc;
        Clear();
        _rows.Clear();
        var root = CreateItem();
        if (contents is null || disc is null)
            return (0, 0);

        var usage = contents.PlaylistsByClip();
        var (hiddenShort, hiddenUnnamed) = (0, 0);
        foreach (var clip in contents.Clips)
        {
            if (disc.GetClipName(clip.File) is null)
            {
                if (hideShort && clip.Duration.ToPreciseTime() < new PreciseTime(0, 0, shortSeconds, 0))
                {
                    hiddenShort++;
                    continue;
                }

                if (hideUnnamed)
                {
                    hiddenUnnamed++;
                    continue;
                }
            }

            var row = new Row(this, root, clip, usage.GetValueOrDefault(clip.File));
            _rows[row.Item.GetInstanceId()] = row;
            if (clip.File.Equals(selected, StringComparison.OrdinalIgnoreCase))
                row.Item.Select((int)Column.Name);
        }

        return (hiddenShort, hiddenUnnamed);
    }

    /// <summary>Shows the files' names again, after one's renamed.</summary>
    public void RefreshNames()
    {
        foreach (var row in _rows.Values)
            row.ShowName();
    }

    /// <summary>Selects the file after <paramref name="clip"/>, if it's selected and isn't the last. Returns whether it did.</summary>
    public bool SelectNextAfter(string clip)
    {
        var item = GetSelected();
        if (item is null || SelectedClip != clip)
            return false;
        
        var next = item.GetNext();
        if (next is null)
            return false;

        next.Select((int)Column.Name);
        ScrollToItem(next);
        return true;
    }

    private void On_ClipTree_ItemSelected()
    {
        if (SelectedClip is not null)
            EmitSignal(SignalName.ClipSelected);
    }

    private void On_ClipTree_ItemActivated() => EmitSignal(SignalName.ClipActivated);

    private Variant GetDragData(Vector2 atPosition)
    {
        var item = GetItemAtPosition(atPosition);
        if (item is null)
            return default;

        var row = _rows.GetValueOrDefault(item.GetInstanceId());
        if (row is null)
            return default;

        SetDragPreview(new Label { Text = ClipText.Describe(_disc, row.Clip) });
        return new Godot.Collections.Dictionary { ["kind"] = "clip", ["clip"] = row.Clip };
    }

    /// <summary>A video file's row: its name, length, and which of the disc's playlists play it.</summary>
    private sealed class Row
    {
        private readonly ClipTree _tree;

        public Row(ClipTree tree, TreeItem parent, ClipSummary clip, List<string>? playlists)
        {
            _tree = tree;
            Clip = clip.File;
            Item = tree.CreateItem(parent);
            ShowName();
            Item.SetText((int)Column.Duration, ClipText.FormatTime(clip.Duration.ToPreciseTime()));
            Item.SetTextAlignment((int)Column.Duration, HorizontalAlignment.Right);

            var tooltip = $"{clip.File}\n{clip.Description}";
            if (playlists is { Count: > 0 })
                tooltip += $"\nIn the disc's playlists: {string.Join(", ", playlists)}";
            foreach (var column in Enum.GetValues<Column>())
                Item.SetTooltipText((int)column, tooltip);
        }

        public string Clip { get; }

        public TreeItem Item { get; }

        public void ShowName() => ClipText.ShowName(Item, (int)Column.Name, _tree._disc, Clip);
    }
}
