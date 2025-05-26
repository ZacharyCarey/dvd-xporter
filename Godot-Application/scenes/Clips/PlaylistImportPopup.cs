using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// The disc's own playlists to import from, by name. Each one unfolds to show its video files in order, by the
/// names they've been given; a file played over and over in a row (a loop) shows once, with how many times it plays.
/// </summary>
public partial class PlaylistImportPopup : PopupPanel
{
    private static readonly Color DimTextColor = new(1, 1, 1, 0.55f);

    private enum Column
    {
        Name,
        Length,
        Files,
    }

    /// <summary>A playlist was chosen, by its index in the disc's contents.</summary>
    [Signal]
    public delegate void PlaylistChosenEventHandler(int index);

    private Tree _tree = null!;
    private Button _importButton = null!;

    public override void _Ready()
    {
        _tree = GetNode<Tree>("%Tree");
        _importButton = GetNode<Button>("%ImportButton");

        // Column titles and widths can't be set in a scene (the column count is in it)
        _tree.SetColumnTitle((int)Column.Name, "Playlist");
        _tree.SetColumnTitle((int)Column.Length, "Length");
        _tree.SetColumnTitle((int)Column.Files, "Files");
        _tree.SetColumnExpand((int)Column.Name, true);
        _tree.SetColumnClipContent((int)Column.Name, true);
        foreach (var column in new[] { Column.Length, Column.Files })
        {
            _tree.SetColumnExpand((int)column, false);
            _tree.SetColumnCustomMinimumWidth((int)column, column == Column.Files ? 140 : 100);
        }
    }

    /// <summary>Lists the disc's playlists and opens below <paramref name="anchor"/>.</summary>
    public void Open(ProjectDisc disc, DiscContents contents, Control anchor)
    {
        _tree.Clear();
        var root = _tree.CreateItem();
        var imported = disc.Playlists.Select(p => p.ImportedFrom).OfType<string>().ToHashSet();

        // By name, which keeps a title set's menus, titles and angles together (e.g. "VTS_05_TitlePGC_0001_Angle2")
        var ordered = contents.Playlists
            .Select((playlist, index) => (playlist, index))
            .OrderBy(p => p.playlist.Name, StringComparer.OrdinalIgnoreCase);
        foreach (var (playlist, index) in ordered)
        {
            var item = _tree.CreateItem(root);
            item.SetMetadata(0, index);
            item.Collapsed = true;
            var runs = Runs(playlist.Entries);
            var wasImported = imported.Contains(playlist.Name);
            item.SetText((int)Column.Name, playlist.Name + (wasImported ? "  (imported)" : ""));
            item.SetText((int)Column.Length, FormatTime(playlist.Duration.ToPreciseTime()));
            item.SetTextAlignment((int)Column.Length, HorizontalAlignment.Right);
            var looped = runs.Count < playlist.Entries.Count;
            item.SetText((int)Column.Files, runs.Count == 1 && looped ? $"1 file x {runs[0].Count}"
                : looped ? $"{runs.Count} files, looped"
                : runs.Count == 1 ? "1 file" : $"{runs.Count} files");
            item.SetTextAlignment((int)Column.Files, HorizontalAlignment.Right);
            var tooltip = wasImported ? $"{playlist.Name}: already imported" : playlist.Name;
            if (looped)
                tooltip += $"\nPlays {playlist.Entries.Count} files in all, with repeats; importing keeps every one";
            if (wasImported)
                item.SetCustomColor((int)Column.Name, DimTextColor);
            foreach (var column in Enum.GetValues<Column>())
                item.SetTooltipText((int)column, tooltip);

            foreach (var (entry, count) in runs)
                AddEntry(item, index, disc, contents, entry, count);
        }

        UpdateButton();
        // Below the button, kept inside the window
        var size = new Vector2I(660, 460);
        // In the same terms as GetScreenPosition, which are the window's own while popups are drawn inside it
        var origin = (Vector2I)(anchor.GetScreenPosition() - anchor.GetGlobalTransformWithCanvas().Origin);
        var limit = origin + (Vector2I)anchor.GetViewportRect().Size - size;
        var position = (Vector2I)(anchor.GetScreenPosition() + new Vector2(0, anchor.Size.Y));
        position = new Vector2I(Math.Clamp(position.X, origin.X, Math.Max(origin.X, limit.X)),
            Math.Clamp(position.Y, origin.Y, Math.Max(origin.Y, limit.Y)));
        Popup(new Rect2I(position, size));
        _tree.GrabFocus();
    }

    private void AddEntry(TreeItem parent, int playlist, ProjectDisc disc, DiscContents contents, PlaylistEntry entry, int count)
    {
        var item = _tree.CreateItem(parent);
        item.SetMetadata(0, playlist);
        var name = disc.GetClipName(entry.Clip);
        item.SetText((int)Column.Name, (name ?? entry.Clip) + (count > 1 ? $"  x{count}" : ""));
        if (name is null)
            item.SetCustomColor((int)Column.Name, DimTextColor);

        var length = entry.TrimmedDuration ?? contents.FindClip(entry.Clip)?.Duration;
        item.SetText((int)Column.Length, length is not null ? FormatTime(length.Value.ToPreciseTime()) + (count > 1 ? " each" : "") : "");
        item.SetTextAlignment((int)Column.Length, HorizontalAlignment.Right);

        var tooltip = name is null ? entry.Clip : $"{name}\n{entry.Clip}";
        if (entry.IsTrimmed)
            tooltip += "\nPlays part of the file";
        if (count > 1)
            tooltip += $"\nPlayed {count} times in a row (the disc loops it), shown once";
        foreach (var column in Enum.GetValues<Column>())
            item.SetTooltipText((int)column, tooltip);
    }

    /// <summary>The entries with back-to-back repeats of the same file (and part of it) counted together.</summary>
    private static List<(PlaylistEntry Entry, int Count)> Runs(IReadOnlyList<PlaylistEntry> entries)
    {
        var runs = new List<(PlaylistEntry Entry, int Count)>();
        foreach (var entry in entries)
        {
            if (runs.Count > 0 && runs[^1].Entry == entry)
                runs[^1] = (entry, runs[^1].Count + 1);
            else
                runs.Add((entry, 1));
        }

        return runs;
    }

    /// <summary>The playlist of the selected row (a file row stands for its playlist).</summary>
    private int? SelectedPlaylist => _tree.GetSelected()?.GetMetadata(0).AsInt32();

    private void On_Tree_ItemSelected() => UpdateButton();

    private void On_Tree_ItemActivated() => Choose();

    private void On_ImportButton_Pressed() => Choose();

    private void UpdateButton() => _importButton.Disabled = SelectedPlaylist is null;

    private void Choose()
    {
        var index = SelectedPlaylist;
        if (index is null)
            return;
        Hide();
        EmitSignal(SignalName.PlaylistChosen, index.Value);
    }

    private static string FormatTime(PreciseTime time)
    {
        var span = time.AsTimeSpan();
        return span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes}:{span.Seconds:00}";
    }
}
