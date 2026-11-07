using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using DvdXporter.Core;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;
using DvdXporter.Core.Settings;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// Step 2: preview each disc's video files, give them names, and arrange them into playlists.
/// A disc's files are copied or demuxed into the project folder on request before they can be previewed.
/// </summary>
public partial class ClipRenamerPage : MarginContainer, IStepPage
{
    [Signal]
    public delegate void BackRequestedEventHandler();

    [Signal]
    public delegate void StatusChangedEventHandler(string status);

    private OptionButton _discOption = null!;
    private VBoxContainer _preparePanel = null!;
    private Label _prepareLabel = null!;
    private CheckBox _hideShortCheck = null!;
    private CheckBox _hideUnnamedCheck = null!;

    // The disc's files besides its video (a PDF, a PC program), which the Information step's Files can export
    private Label _otherFilesLabel = null!;
    private int _shortClipSeconds = AppSettings.DefaultShortClipSeconds;
    private ClipTree _clipTree = null!;
    private Label _clipDetails = null!;
    private Control _interlacingPanel = null!;
    private Label _interlacingLabel = null!;
    private ItemList _interlacedList = null!;
    private Button _addToPlaylistButton = null!;
    private Button _openExternalButton = null!;
    private Button _saveFrameButton = null!;
    private FileDialog _saveFrameDialog = null!;

    private VlcPlayer _player = null!;
    private LineEdit _clipNameEdit = null!;
    private LineEdit _audioNameEdit = null!;
    private LineEdit _subtitleNameEdit = null!;
    private PlaylistPanel _playlistPanel = null!;

    private AcceptDialog _errorDialog = null!;
    private Timer _previewTimer = null!;

    private ProjectFolder? _folder;
    private DiscServices? _discServices;
    private RenderServerLink? _server;
    private readonly List<ProjectDisc> _discs = [];

    // The disc being shown, what's known about it, and where it is, here or on the render server (null = can't preview)
    private ProjectDisc? _disc;
    private DiscCache? _cache;
    private DiscContents? _contents;
    private DiscSource? _source;
    // The render server it was looked for on, to look again if that changes
    private RenderServerClient? _lookedOn;
    private bool _sourceChecked;

    // The video file being previewed and named, picked from either the file list or a playlist
    private string? _currentClip;
    private string? _pendingPreview;
    // The video file playing, whose tracks the player lists, and the tracks being named (picked in the player)
    private string? _previewClip;
    private ClipTrack? _audioNamed;
    private ClipTrack? _subtitleNamed;
    // Counts previews asked for, so one that opens after another was asked for (or previewing stopped) isn't shown
    private int _previewRequest;
    // The frame being saved, while its file is picked
    private (ProjectDisc Disc, DiscSource Source, string Clip, PreciseTime Time)? _frameToSave;

    public override void _Ready()
    {
        _discOption = GetNode<OptionButton>("%DiscOption");
        _preparePanel = GetNode<VBoxContainer>("%PreparePanel");
        _prepareLabel = GetNode<Label>("%PrepareLabel");
        _hideShortCheck = GetNode<CheckBox>("%HideShortCheck");
        _hideUnnamedCheck = GetNode<CheckBox>("%HideUnnamedCheck");
        _otherFilesLabel = GetNode<Label>("%OtherFilesLabel");
        _clipTree = GetNode<ClipTree>("%ClipTree");
        _clipDetails = GetNode<Label>("%ClipDetails");
        _interlacingPanel = GetNode<Control>("%InterlacingPanel");
        _interlacingLabel = GetNode<Label>("%InterlacingLabel");
        _interlacedList = GetNode<ItemList>("%InterlacedList");
        _addToPlaylistButton = GetNode<Button>("%AddToPlaylistButton");
        _openExternalButton = GetNode<Button>("%OpenExternalButton");
        _saveFrameButton = GetNode<Button>("%SaveFrameButton");
        _saveFrameDialog = GetNode<FileDialog>("%SaveFrameDialog");

        _player = GetNode<VlcPlayer>("%VlcPlayer");
        _clipNameEdit = GetNode<LineEdit>("%ClipNameEdit");
        _audioNameEdit = GetNode<LineEdit>("%AudioNameEdit");
        _subtitleNameEdit = GetNode<LineEdit>("%SubtitleNameEdit");
        _playlistPanel = GetNode<PlaylistPanel>("%PlaylistPanel");

        _errorDialog = GetNode<AcceptDialog>("%ErrorDialog");
        _previewTimer = GetNode<Timer>("%PreviewTimer");
    }

    // ---- Signals connected in the scene ----

    private void On_BackButton_Pressed() => EmitSignal(SignalName.BackRequested);

    /// <summary>Previewing waits for the selection to settle, so arrowing through the list doesn't open every file.</summary>
    private void On_PreviewTimer_Timeout()
    {
        var clip = _pendingPreview;
        if (clip is not null)
            Preview(clip);
    }

    private void On_DiscOption_ItemSelected(long index) => ShowDisc(_discs[(int)index]);

    private void On_HideShortCheck_Toggled(bool on) => RebuildClipTree();

    private void On_HideUnnamedCheck_Toggled(bool on) => RebuildClipTree();

    private void On_AddToPlaylistButton_Pressed()
    {
        var clip = _currentClip;
        if (clip is not null)
            _playlistPanel.AddClip(clip);
    }

    private void On_OpenExternalButton_Pressed() => OpenSelectedClipExternally();

    private void On_SaveFrameButton_Pressed() => ChooseWhereToSaveFrame();

    private void On_SaveFrameDialog_FileSelected(string path) => SaveFrame(path);

    private void On_ClipNameEdit_TextSubmitted(string text) => NameClipAndMoveOn();

    private void On_ClipNameEdit_FocusExited() => CommitClipName();

    private void On_VlcPlayer_TracksChanged()
    {
        CommitTrackNames();
        ShowTrackNames();
    }

    private void On_AudioNameEdit_TextSubmitted(string text) => CommitTrackNames();

    private void On_AudioNameEdit_FocusExited() => CommitTrackNames();

    private void On_SubtitleNameEdit_TextSubmitted(string text) => CommitTrackNames();

    private void On_SubtitleNameEdit_FocusExited() => CommitTrackNames();

    private void On_ClipTree_ClipSelected() => SetCurrentClip(_clipTree.SelectedClip);

    private void On_ClipTree_ClipActivated() => FocusClipName();

    private void On_PlaylistPanel_Edited() => Save();

    private void On_PlaylistPanel_PlaylistChanged() => UpdateClipButtons();

    private void On_PlaylistPanel_ClipSelected() => SetCurrentClip(_playlistPanel.SelectedClip);

    private void On_PlaylistPanel_ClipActivated() => FocusClipName();

    private void On_PlaylistPanel_StatusChanged(string status) => SetStatus(status);

    /// <summary>Clips shorter than this many seconds are short, and hidden with "Hide short clips" (a user setting).</summary>
    public int ShortClipSeconds
    {
        get => _shortClipSeconds;
        set
        {
            _shortClipSeconds = Math.Max(1, value);
            _hideShortCheck.TooltipText =
                $"Hide unnamed video files shorter than {_shortClipSeconds} seconds, such as menu loops and logos. The length is in Settings › General.";
            if (IsVisibleInTree())
                RebuildClipTree();
        }
    }

    public void ShowProject(ProjectFolder folder, DiscServices discServices, RenderServerLink server)
    {
        var sameProject = folder == _folder;
        _folder = folder;
        _discServices = discServices;
        _server = server;

        _discs.Clear();
        _discOption.Clear();
        foreach (var release in folder.Project.Releases)
        {
            foreach (var disc in release.Discs)
            {
                _discOption.AddItem($"{release.Name} › {disc.Label} ({DiscResolver.FormatName(disc.Format)})");
                _discs.Add(disc);
            }
        }

        _discOption.Disabled = _discs.Count == 0;
        var disc0 = sameProject && _disc is not null && _discs.Contains(_disc) ? _disc : _discs.FirstOrDefault();
        if (disc0 is not null)
            _discOption.Select(_discs.IndexOf(disc0));
        ShowDisc(disc0);
    }

    /// <summary>Stops the preview, e.g. when leaving the page.</summary>
    public void StopPreview()
    {
        _previewTimer.Stop();
        _pendingPreview = null;
        _previewRequest++;
        _player.Close();
        _previewClip = null;
        ShowTrackNames();
    }

    // ---- Disc ----

    private void ShowDisc(ProjectDisc? disc)
    {
        StopPreview();
        SetCurrentClip(null);
        _disc = disc;
        _cache = disc is not null ? _folder?.GetDiscCache(disc) : null;
        _contents = _cache?.LoadContents();

        _source = null;
        _sourceChecked = false;
        _otherFilesLabel.Visible = false;

        RefreshDiscState();
        RebuildClipTree();
        _playlistPanel.ShowDisc(disc, _contents);

        if (disc is not null)
            FindSource(disc);
    }

    /// <summary>Works out where the disc is, in the background: on this computer, or the render server (played from there).</summary>
    private async void FindSource(ProjectDisc disc)
    {
        var folder = _folder;
        var services = _discServices;
        if (folder is null || services is null)
            return;

        var client = _lookedOn = _server?.Client;
        var source = (await DiscFinder.For(folder, services, client).GetAvailableDiscsAsync([disc]))[disc].Source;
        if (disc != _disc)
            return;

        _source = source;
        _sourceChecked = true;
        RefreshDiscState();
        if (source is { IsHere: false })
            SetStatus($"\"{disc.Label}\" isn't on this computer, so its clips are played from \"{source.ServerName}\".");
        if (source is not null && _cache is not null)
            ShowOtherFiles(_cache, source);
    }

    /// <summary>Points out the disc's other files, if it has any, and how to export them.</summary>
    private async void ShowOtherFiles(DiscCache cache, DiscSource source)
    {
        var files = await Task.Run(source.ListOtherFiles);
        if (cache == _cache)
            ShowOtherFiles(files);
    }

    private void ShowOtherFiles(IReadOnlyList<DiscDecoders.DiscFile> files)
    {
        if (files.Count == 0)
            return;

        const int shown = 3;
        var names = files.Take(shown).Select(f => f.Path).ToList();
        var what = files.Count == 1 ? $"1 other file: {files[0].Path} ({NodeTypes.FormatSize(files[0].Length)})"
            : $"{files.Count} other files ({NodeTypes.FormatSize(files.Sum(f => f.Length))}): {string.Join(", ", names)}"
                + (files.Count > shown ? ", ..." : "");
        _otherFilesLabel.Text = $"This disc also has {what}. To export {(files.Count == 1 ? "it" : "them")}, use the Files section "
            + "of the Information step.";
        const int listed = 40;
        _otherFilesLabel.TooltipText = string.Join("\n", files.Take(listed).Select(f => $"{f.Path}  ({NodeTypes.FormatSize(f.Length)})"))
            + (files.Count > listed ? $"\n... and {files.Count - listed} more" : "");
        _otherFilesLabel.Visible = true;
    }

    private void RefreshDiscState()
    {
        _preparePanel.Visible = _disc is not null && _contents is null;
        _prepareLabel.Text = _disc is null ? "" : DescribeDiscState();

        UpdateClipButtons();
    }

    // Discs are only read on the project overview, so one that hasn't been is missing or couldn't be read there
    private string DescribeDiscState()
    {
        if (!_sourceChecked)
            return "Looking for the disc...";
        if (_source is null)
            return "This disc isn't on this computer. Use Locate on the project overview to find it, then come back here.";
        return "This disc couldn't be read. Use Locate on the project overview to read it again, then come back here.";
    }

    // ---- Video files ----

    private void RebuildClipTree()
    {
        var (hiddenShort, hiddenUnnamed) = _clipTree.Show(_disc, _contents, _hideShortCheck.ButtonPressed, _shortClipSeconds, _hideUnnamedCheck.ButtonPressed);
        _hideShortCheck.Text = hiddenShort > 0 ? $"Hide short clips ({hiddenShort} hidden)" : "Hide short clips";
        _hideUnnamedCheck.Text = hiddenUnnamed > 0 ? $"Hide unnamed clips ({hiddenUnnamed} hidden)" : "Hide unnamed clips";
        if (_contents is not null && _disc is not null)
            UpdateClipDetails();
        UpdateClipButtons();
    }

    /// <summary>Makes <paramref name="clip"/> the file being previewed and named.</summary>
    private void SetCurrentClip(string? clip)
    {
        CommitClipName();
        _currentClip = clip;
        _clipNameEdit.Editable = clip is not null;
        _clipNameEdit.Text = clip is not null ? _disc?.GetClipName(clip) ?? "" : "";
        UpdateClipDetails();
        UpdateClipButtons();
        if (clip is not null)
            QueuePreview(clip);
    }

    private void FocusClipName()
    {
        _clipNameEdit.GrabFocus();
        _clipNameEdit.SelectAll();
    }

    private void CommitClipName()
    {
        if (_disc is null || _currentClip is null)
            return;

        var name = _clipNameEdit.Text;
        if ((_disc.GetClipName(_currentClip) ?? "") == name.Trim())
            return;

        _disc.SetClipName(_currentClip, name);
        Save();
        _clipTree.RefreshNames();
        _playlistPanel.RefreshNames();
    }

    /// <summary>Saves the name, then moves to the next file in the list so a disc can be named quickly.</summary>
    private void NameClipAndMoveOn()
    {
        CommitClipName();
        if (_currentClip is not null && _clipTree.SelectNextAfter(_currentClip))
            FocusClipName();
    }

    // ---- Track names ----

    /// <summary>Shows the names of the audio and subtitle tracks picked in the player, to name them.</summary>
    private void ShowTrackNames()
    {
        _audioNamed = ShowTrackName(_audioNameEdit, ClipTrackKind.Audio, "No audio track");
        _subtitleNamed = ShowTrackName(_subtitleNameEdit, ClipTrackKind.Subtitle, "Pick subtitles to name them");
    }

    private ClipTrack? ShowTrackName(LineEdit edit, ClipTrackKind kind, string none)
    {
        var track = _disc is not null && _previewClip is not null ? _player.SelectedTrack(kind) : null;
        var original = track is not null ? OriginalTrack(track) : null;
        edit.Editable = original is not null;
        edit.Text = original is not null ? _disc!.GetTrackName(_previewClip!, original.StreamId) ?? "" : "";
        // What the disc says it is, to name it after
        edit.PlaceholderText = original?.Label ?? none;
        return original;
    }

    private void CommitTrackNames()
    {
        CommitTrackName(_audioNameEdit, _audioNamed);
        CommitTrackName(_subtitleNameEdit, _subtitleNamed);
    }

    private void CommitTrackName(LineEdit edit, ClipTrack? track)
    {
        var disc = _disc;
        var clip = _previewClip;
        if (disc is null || clip is null || track is null)
            return;

        var name = edit.Text.Trim();
        if ((disc.GetTrackName(clip, track.StreamId) ?? "") == name)
            return;

        disc.SetTrackName(clip, track.StreamId, name);
        Save();
        _player.RenameTrack(track, NamedTrack(disc, clip, track).Label);
    }

    /// <summary>One of the previewed file's tracks as the disc lists it, without the name it was given.</summary>
    private ClipTrack? OriginalTrack(ClipTrack track) =>
        _previewClip is not null ? _contents?.FindClip(_previewClip)?.Tracks?.FirstOrDefault(t => t.Kind == track.Kind && t.StreamId == track.StreamId) : null;

    /// <summary>A track with the name it was given, e.g. "Commentary (English · AC3 · 2 ch)", as the player lists it.</summary>
    private static ClipTrack NamedTrack(ProjectDisc disc, string clip, ClipTrack track)
    {
        var name = disc.GetTrackName(clip, track.StreamId);
        return name is not null ? track with { Label = $"{name} ({track.Label})" } : track;
    }

    private void UpdateClipDetails()
    {
        ClipSummary? clip = null;
        if (_currentClip is not null)
            clip = _contents?.FindClip(_currentClip);

        ShowInterlacing(clip?.Picture);
        if (_contents is null || clip is null)
        {
            _clipDetails.Text = _contents is null ? "" : "Select a video file to preview and name it.";
            _clipDetails.TooltipText = "";
            return;
        }

        var usage = _contents.PlaylistsByClip().GetValueOrDefault(clip.File);
        var playlists = usage is { Count: > 0 } ? $"In {string.Join(", ", usage)}" : "Not in any of the disc's playlists";
        var length = ClipText.FormatTime(clip.Duration.ToPreciseTime());
        _clipDetails.Text = $"{clip.File}  ·  {length}  ·  {clip.Description}  ·  {playlists}";
        _clipDetails.TooltipText = $"{clip.File} ({length})\n{clip.Description}\n{playlists}";
    }

    /// <summary>
    /// Says what the clip's picture is, and where its frames looked interlaced, if every frame was checked: each stretch
    /// is listed, to go to in the preview.
    /// </summary>
    private void ShowInterlacing(PictureAnalysis? picture)
    {
        _interlacedList.Clear();
        _interlacingPanel.Visible = picture is not null;
        if (picture is null)
            return;

        var ranges = picture.InterlacedRanges ?? [];
        _interlacingLabel.Text = $"Picture: {picture.Summary}" + (ranges.Count switch
        {
            0 when picture.Type == PictureType.Unknown => "",
            0 => picture.InterlacedRanges is null ? "" : "  ·  No frames looked interlaced",
            1 => "  ·  Interlaced frames at:",
            _ => $"  ·  Interlaced frames in {ranges.Count} places:",
        });
        _interlacingLabel.TooltipText = picture.Explanation;
        _interlacedList.Visible = ranges.Count > 0;
        foreach (var range in ranges)
        {
            var start = ClipText.FormatTime(range.Start);
            var end = ClipText.FormatTime(range.End);
            var index = _interlacedList.AddItem(start == end ? start : $"{start} – {end}");
            _interlacedList.SetItemMetadata(index, range.Start.Nanoseconds);
            _interlacedList.SetItemTooltip(index, $"{range.Frames} {(range.Frames == 1 ? "frame" : "frames")} looked interlaced. Click to go there.");
        }
    }

    private void On_InterlacedList_ItemClicked(long index, Vector2 atPosition, long mouseButtonIndex)
    {
        if (mouseButtonIndex == (long)MouseButton.Left)
            _player.Seek(new PreciseTime(_interlacedList.GetItemMetadata((int)index).AsInt64()).AsTimeSpan());
    }

    private void UpdateClipButtons()
    {
        var hasClip = _currentClip is not null;
        var playlist = _playlistPanel.Playlist;
        _addToPlaylistButton.Disabled = !hasClip || playlist is null;
        _addToPlaylistButton.TooltipText = playlist is null
            ? "Create or select a playlist first"
            : $"Add the selected video file to \"{playlist.Name}\"";
        var file = _currentClip is not null ? _source?.GetClipFilePath(_currentClip) : null;
        _openExternalButton.Disabled = file is null;
        _openExternalButton.TooltipText = file is not null || !hasClip
            ? "Open the selected video file in your default video player"
            : "Only clips that are ordinary files (in Blu-ray folders) can be opened in another app";
        var hasPicture = _currentClip is not null && _contents?.FindClip(_currentClip)?.Tracks?.Any(t => t.Kind == ClipTrackKind.Video) == true;
        _saveFrameButton.Disabled = !hasPicture || _source is null;
        _saveFrameButton.TooltipText = hasPicture && _source is null
            ? "The disc isn't on this computer or the render server"
            : "Save the picture being shown as an image file, at its full size";
    }

    // ---- Saving a frame ----

    /// <summary>Pauses on the picture being shown, and asks where to save it.</summary>
    private void ChooseWhereToSaveFrame()
    {
        var time = _player.Time;
        if (_disc is null || _source is null || _currentClip is null || time is null)
            return;

        _player.Pause();
        var at = PreciseTime.FromTimespan(time.Value);
        _frameToSave = (_disc, _source, _currentClip, at);
        // e.g. "Disc 1 - Gag Reel - 0-12-34.png"
        var name = _disc.GetClipName(_currentClip) ?? Path.GetFileNameWithoutExtension(_currentClip);
        var stamp = $"{(int)time.Value.TotalHours}-{time.Value.Minutes:00}-{time.Value.Seconds:00}";
        _saveFrameDialog.CurrentFile = ProjectFolder.ToSafeFileName($"{_disc.Label} - {name} - {stamp}") + ".png";
        _saveFrameDialog.PopupCentered();
    }

    /// <summary>Saves the frame picked with ffmpeg, from the disc itself rather than what the player shows.</summary>
    private async void SaveFrame(string path)
    {
        var frame = _frameToSave;
        var folder = _folder;
        _frameToSave = null;
        if (frame is null || folder is null)
            return;
        var (disc, source, clip, time) = frame.Value;
        if (Path.GetExtension(path).Length == 0)
            path += ".png";

        _saveFrameButton.Disabled = true;
        SetStatus($"Saving the frame at {time} of {ClipText.Describe(disc, clip)}...");
        var sources = new ProjectTranscodeSources(folder, new Dictionary<ProjectDisc, DiscSource?> { [disc] = source });
        string? error;
        try
        {
            error = await FrameGrabber.SaveAsync(sources, disc, clip, time, path, default);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = ex.Message;
        }

        if (!IsInstanceValid(this))
            return;
        UpdateClipButtons();
        if (error is not null)
        {
            SetStatus("Couldn't save the frame.");
            ShowError($"Couldn't save the frame: {error}");
            return;
        }

        SetStatus($"Saved the frame at {time} as \"{path}\".");
    }

    private void OpenSelectedClipExternally()
    {
        if (_currentClip is null)
            return;

        var path = _source?.GetClipFilePath(_currentClip);
        if (path is null)
            return;

        _player.Pause();
        var error = OS.ShellOpen(path);
        if (error != Error.Ok)
            ShowError($"Couldn't open \"{_currentClip}\" in another app ({error}).");
    }

    // ---- Preview ----

    private void QueuePreview(string clip)
    {
        _pendingPreview = clip;
        _previewTimer.Start();
    }

    /// <summary>Plays a clip straight from the disc, with its tracks in the disc's order.</summary>
    private async void Preview(string clip)
    {
        _pendingPreview = null;
        var request = ++_previewRequest;
        ClipPreview? preview;
        try
        {
            var tracks = _contents?.FindClip(clip)?.Tracks;
            preview = _source is not null ? await _source.OpenPreviewAsync(clip, tracks) : null;
        }
        catch (Exception ex)
        {
            if (request != _previewRequest)
                return;
            _player.Close();
            SetStatus($"Can't preview {ClipText.Describe(_disc, clip)}: {ex.Message}");
            return;
        }

        if (request != _previewRequest)
        {
            preview?.Stream?.Dispose();
            return;
        }

        if (preview is null)
        {
            _player.Close();
            if (_contents is not null)
                SetStatus($"Can't preview {ClipText.Describe(_disc, clip)}: the disc isn't on this computer.");
            return;
        }

        var summary = _contents?.FindClip(clip);
        var disc = _disc;
        var named = disc is not null ? summary?.Tracks?.Select(t => NamedTrack(disc, clip, t)).ToList() : summary?.Tracks;
        // The names being edited are of the file playing until now
        CommitTrackNames();
        _audioNamed = _subtitleNamed = null;
        _previewClip = clip;
        _player.Open(preview, named, summary?.Duration.Value);
    }

    // ---- Helpers ----

    private void Save()
    {
        try
        {
            _folder?.Save();
        }
        catch (Exception ex)
        {
            SetStatus($"Couldn't save the project: {ex.Message}");
        }
    }

    private void SetStatus(string status) => EmitSignal(SignalName.StatusChanged, status);

    private void ShowError(string message)
    {
        _errorDialog.DialogText = message;
        _errorDialog.PopupCentered();
    }

    // ---- As a step page ----

    public ProjectStep Step => ProjectStep.ClipRenamer;

    void IStepPage.ShowProject(ProjectFolder folder, AppServices services) => ShowProject(folder, services.Discs, services.Server);

    void IStepPage.Leave() => StopPreview();

    void IStepPage.OnDiscLibraryChanged()
    {
    }

    void IStepPage.OnSettingsChanged()
    {
        ShortClipSeconds = AppSettings.Current.ShortClipSeconds;
        // The render server may be another one now, or none, or there may be one now
        if (_lookedOn != _server?.Client && _source is not { IsHere: true } && _disc is not null)
            ShowDisc(_disc);
    }
}
