using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A disc's playlists: made by hand or imported from the disc's own, renamed, reordered by dragging and removed, and the
/// selected one's video files in order.
/// </summary>
public partial class PlaylistPanel : PanelContainer
{
    /// <summary>A playlist was added, changed or removed, which needs saving.</summary>
    [Signal]
    public delegate void EditedEventHandler();

    /// <summary>Another playlist was selected (it's <see cref="Playlist"/>), or it was renamed.</summary>
    [Signal]
    public delegate void PlaylistChangedEventHandler();

    /// <summary>One of the playlist's files was selected: it's <see cref="SelectedClip"/>.</summary>
    [Signal]
    public delegate void ClipSelectedEventHandler();

    /// <summary>One of the playlist's files was double-clicked, to name it.</summary>
    [Signal]
    public delegate void ClipActivatedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    /// <summary>The selected playlist.</summary>
    public ClipPlaylist? Playlist { get; private set; }

    /// <summary>The file of the playlist's selected entry.</summary>
    public string? SelectedClip => _entryTree.SelectedEntry?.Clip;

    private Tree _playlistList = null!;
    private Button _newPlaylistButton = null!;
    private Button _importButton = null!;
    private Button _removePlaylistButton = null!;
    private LineEdit _playlistNameEdit = null!;
    private Label _playlistSourceLabel = null!;
    private PlaylistEntryTree _entryTree = null!;
    private Label _entryHint = null!;
    private PlaylistImportPopup _importPopup = null!;
    private ConfirmationDialog _removePlaylistDialog = null!;

    private ProjectDisc? _disc;
    private DiscContents? _contents;

    public override void _Ready()
    {
        _playlistList = GetNode<Tree>("%PlaylistList");
        _newPlaylistButton = GetNode<Button>("%NewPlaylistButton");
        _importButton = GetNode<Button>("%ImportButton");
        _removePlaylistButton = GetNode<Button>("%RemovePlaylistButton");
        _playlistNameEdit = GetNode<LineEdit>("%PlaylistNameEdit");
        _playlistSourceLabel = GetNode<Label>("%PlaylistSourceLabel");
        _entryTree = GetNode<PlaylistEntryTree>("%EntryTree");
        _entryHint = GetNode<Label>("%EntryHint");
        _importPopup = GetNode<PlaylistImportPopup>("%PlaylistImportPopup");
        _removePlaylistDialog = GetNode<ConfirmationDialog>("%RemovePlaylistDialog");
        // Drag and drop can't be set in a scene
        _playlistList.SetDragForwarding(
            Callable.From<Vector2, Variant>(GetPlaylistDragData),
            Callable.From<Vector2, Variant, bool>(CanDropPlaylist),
            Callable.From<Vector2, Variant>(DropPlaylist));
    }

    public override void _Notification(int what)
    {
        // Drop lines only show while a playlist is being dragged over the list
        if (what == NotificationDragEnd)
            _playlistList.DropModeFlags = (int)Tree.DropModeFlagsEnum.Disabled;
    }

    /// <summary>Shows <paramref name="disc"/>'s playlists, with its first selected.</summary>
    public void ShowDisc(ProjectDisc? disc, DiscContents? contents)
    {
        _disc = disc;
        _contents = contents;
        Playlist = null;
        RebuildList();
        Select(disc?.Playlists.FirstOrDefault());
    }

    /// <summary>Shows the disc's playlists again, keeping the selection, after what's known about the disc changed.</summary>
    public void Refresh(DiscContents? contents)
    {
        _contents = contents;
        RebuildList();
        ShowEntries();
    }

    /// <summary>Shows the files' names again, after one's renamed.</summary>
    public void RefreshNames() => _entryTree.RefreshNames();

    /// <summary>Adds <paramref name="clip"/> to the end of the selected playlist.</summary>
    public void AddClip(string clip)
    {
        if (Playlist is not null)
            _entryTree.Insert(clip, Playlist.Entries.Count);
    }

    private void On_PlaylistList_ItemSelected()
    {
        var playlist = _disc?.Playlists.ElementAtOrDefault(_playlistList.GetSelected()?.GetIndex() ?? -1);
        // Selecting it from code says so too
        if (playlist != Playlist)
            Select(playlist);
    }

    private Variant GetPlaylistDragData(Vector2 atPosition)
    {
        var item = _playlistList.GetItemAtPosition(atPosition);
        if (item is null)
            return default;

        _playlistList.SetDragPreview(new Label { Text = item.GetText(0) });
        return new Godot.Collections.Dictionary { ["kind"] = "playlist", ["index"] = item.GetIndex() };
    }

    private bool CanDropPlaylist(Vector2 atPosition, Variant data)
    {
        if (_disc is null || data.VariantType != Variant.Type.Dictionary)
            return false;
        if (data.AsGodotDictionary().GetValueOrDefault("kind").AsString() != "playlist")
            return false;

        _playlistList.DropModeFlags = (int)Tree.DropModeFlagsEnum.Inbetween;
        return true;
    }

    private void DropPlaylist(Vector2 atPosition, Variant data)
    {
        if (_disc is null)
            return;

        // Where the drop line was: before or after the row under the mouse, or at the end
        var target = _disc.Playlists.Count;
        var item = _playlistList.GetItemAtPosition(atPosition);
        if (item is not null)
            target = item.GetIndex() + (_playlistList.GetDropSectionAtPosition(atPosition) > 0 ? 1 : 0);

        var from = data.AsGodotDictionary()["index"].AsInt32();
        if (_disc.MovePlaylist(from, target) is null)
            return;
        EmitSignal(SignalName.Edited);
        RebuildList();
    }

    private void On_NewPlaylistButton_Pressed()
    {
        if (_disc is null)
            return;

        var playlist = _disc.NewPlaylist();
        EmitSignal(SignalName.Edited);
        RebuildList();
        Select(playlist);
        _playlistNameEdit.GrabFocus();
        _playlistNameEdit.SelectAll();
    }

    private void On_ImportButton_Pressed()
    {
        if (_disc is not null && _contents is not null)
            _importPopup.Open(_disc, _contents, _importButton);
    }

    private void On_PlaylistImportPopup_PlaylistChosen(int index)
    {
        var source = _contents?.Playlists.ElementAtOrDefault(index);
        if (_disc is null || source is null)
            return;

        var playlist = _disc.ImportPlaylist(source);
        EmitSignal(SignalName.Edited);
        RebuildList();
        Select(playlist);
        EmitSignal(SignalName.StatusChanged,
            $"Imported {source.Name} ({playlist.Entries.Count} {(playlist.Entries.Count == 1 ? "file" : "files")}).");
    }

    private void On_RemovePlaylistButton_Pressed()
    {
        if (Playlist is null)
            return;
        _removePlaylistDialog.DialogText = $"Remove the playlist \"{Playlist.Name}\"? The video files and their names aren't affected.";
        _removePlaylistDialog.PopupCentered();
    }

    private void On_RemovePlaylistDialog_Confirmed()
    {
        if (_disc is null || Playlist is null)
            return;

        var index = _disc.Playlists.IndexOf(Playlist);
        _disc.Playlists.Remove(Playlist);
        Playlist = null;
        EmitSignal(SignalName.Edited);
        RebuildList();
        Select(_disc.Playlists.ElementAtOrDefault(Math.Min(index, _disc.Playlists.Count - 1)));
    }

    private void On_PlaylistNameEdit_TextSubmitted(string text) => Rename();

    private void On_PlaylistNameEdit_FocusExited() => Rename();

    private void On_EntryTree_EntrySelected() => EmitSignal(SignalName.ClipSelected);

    private void On_EntryTree_EntryActivated() => EmitSignal(SignalName.ClipActivated);

    private void On_EntryTree_EntriesChanged()
    {
        EmitSignal(SignalName.Edited);
        UpdateSelectedTooltip();
    }

    private void RebuildList()
    {
        _playlistList.Clear();
        var root = _playlistList.CreateItem();
        foreach (var playlist in _disc?.Playlists ?? [])
        {
            var item = _playlistList.CreateItem(root);
            item.SetText(0, playlist.Name);
            item.SetTooltipText(0, Describe(playlist));
        }

        if (Playlist is not null && _disc?.Playlists.IndexOf(Playlist) is >= 0 and var selected)
            root.GetChild(selected).Select(0);

        _newPlaylistButton.Disabled = _disc is null;
        _importButton.Disabled = _disc is null || _contents is null || _contents.Playlists.Count == 0;
        _importButton.TooltipText = _contents is null
            ? "Wait for the disc to be read first"
            : "Add one of the disc's defined playlists";
    }

    private void Select(ClipPlaylist? playlist)
    {
        CommitName();
        Playlist = playlist;
        if (playlist is not null && _disc?.Playlists.IndexOf(playlist) is >= 0 and var index)
            _playlistList.GetRoot().GetChild(index).Select(0);
        else
            _playlistList.DeselectAll();

        _playlistNameEdit.Editable = playlist is not null;
        _playlistNameEdit.Text = playlist?.Name ?? "";
        _playlistSourceLabel.Text = playlist?.ImportedFrom is not null ? $"Imported from {playlist.ImportedFrom}" : "";
        _removePlaylistButton.Disabled = playlist is null;
        ShowEntries();
        EmitSignal(SignalName.PlaylistChanged);
    }

    private void ShowEntries()
    {
        _entryTree.Show(_disc, Playlist, _contents);
        _entryTree.Visible = Playlist is not null;
        _entryHint.Text = Playlist is null
            ? (_disc is null ? "" : "Create a playlist, or import one of the disc's.")
            : "Drag video files here from the list on the left. Drag rows to reorder.";
        UpdateSelectedTooltip();
    }

    private void UpdateSelectedTooltip()
    {
        var item = _playlistList.GetSelected();
        if (Playlist is not null && item is not null)
            item.SetTooltipText(0, Describe(Playlist));
    }

    private void Rename()
    {
        CommitName();
        _playlistNameEdit.Text = Playlist?.Name ?? "";
    }

    private void CommitName()
    {
        if (Playlist is null || _disc is null)
            return;

        var name = _playlistNameEdit.Text.Trim();
        if (name.Length == 0 || name == Playlist.Name)
            return;

        Playlist.Name = name;
        EmitSignal(SignalName.Edited);
        var index = _disc.Playlists.IndexOf(Playlist);
        if (index >= 0)
            _playlistList.GetRoot().GetChild(index).SetText(0, name);
        EmitSignal(SignalName.PlaylistChanged);
    }

    private string Describe(ClipPlaylist playlist)
    {
        var count = playlist.Entries.Count;
        var description = $"{count} {(count == 1 ? "file" : "files")}";
        var duration = playlist.DurationIn(_contents);
        if (duration is not null)
            description += $", {ClipText.FormatTime(duration.Value)}";
        var source = playlist.ImportedFrom;
        if (source is not null)
            description += $"\nImported from {source}";
        return description;
    }
}
