using DvdXporter.Core.Clips;
using Godot;
using LibVLCSharp.Shared;
using LibVLCSharp.Shared.Structures;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

/// <summary>
/// Video preview built on LibVLCSharp. VLC decodes each frame into a memory buffer, which is copied into a
/// texture every Godot frame; VLC plays the audio itself. Call <see cref="Open"/> or <see cref="OpenVideo"/>,
/// <see cref="Close"/> and <see cref="Pause"/>.
/// </summary>
public partial class VlcPlayer : Control
{
    // VLC's "no subtitles" track is -1, which OptionButton reads as "use the index", so it gets this ID instead
    private const int SubtitlesOffId = 1_000_000;

    private static bool _coreInitialized;
    private static bool _resolverSet;
    private static string? _coreError;

    private LibVLC? _libVlc;
    private MediaPlayer? _mediaPlayer;
    private Media? _media;
    // Set when playing a stream rather than a file; disposed with the media
    private MediaInput? _input;
    private Stream? _stream;

    // The clip's tracks in the order to offer them, and how to find each among VLC's (null = show VLC's own)
    private IReadOnlyList<ClipTrack>? _tracks;
    private PreviewTrackIds _trackIds;

    // VLC can't seek streams by time (or does so far off), so they're seeked by position: to the byte offset
    // the clip's index gives when there is one (Blu-ray), otherwise by the fraction of the clip's known length
    private bool _seekByPosition;
    private Func<TimeSpan, long>? _seekOffset;
    private long _knownLength;

    // Kept in fields so the garbage collector doesn't free them while VLC still calls them
    private MediaPlayer.LibVLCVideoFormatCb? _formatCallback;
    private MediaPlayer.LibVLCVideoCleanupCb? _cleanupCallback;
    private MediaPlayer.LibVLCVideoLockCb? _lockCallback;
    private MediaPlayer.LibVLCVideoDisplayCb? _displayCallback;

    // VLC draws into _buffer on its own thread; each finished frame is copied to _frameBytes for the main thread
    private readonly object _frameLock = new();
    private IntPtr _buffer;
    private byte[] _frameBytes = [];
    private int _frameWidth;
    private int _frameHeight;
    // The picture's real size; VLC pads frames (e.g. 1080 lines become 1090), so the rest is cropped off
    private int _visibleWidth;
    private int _visibleHeight;
    private bool _frameReady;
    private readonly ImageTexture _texture = new();

    // One image, refilled every frame. A new Godot Image per frame isn't freed until the garbage collector
    // happens to run, and its pixel memory is invisible to the collector, so memory climbs by hundreds of MB a second.
    private readonly Image _image = new();
    private byte[] _imageBytes = [];

    private TextureRect _frame = null!;
    private AspectRatioContainer _aspect = null!;
    private Label _emptyLabel = null!;
    private HSlider _timeline = null!;
    private Button _playPauseButton = null!;
    private Label _timeLabel = null!;
    private OptionButton _audioOption = null!;
    private OptionButton _subtitleOption = null!;
    private CheckButton _autoPlay = null!;
    private CheckButton _deinterlace = null!;
    private HSlider _volumeSlider = null!;
    private Control _volumeBubble = null!;
    private Label _volumeBubbleLabel = null!;
    private bool _draggingVolume;
    // Seconds the volume bubble stays up after the last change, when not dragging
    private double _volumeBubbleTime;
    private const double VolumeBubbleLinger = 1.0;

    // Kept between clips (and players) for the rest of the session. 100% is the clip's own level; up to 125%
    // VLC amplifies it, which can clip on loud passages.
    private static int _volume = 75;
    private Label _debugLabel = null!;

    private bool _updatingTimeline;

    // After a seek, VLC's time is briefly stale or wild (worked out from its own length guess) until it decodes
    // the new position, so the timeline shows the requested time until VLC's comes close to it
    private const long SeekSettleTolerance = 3000;
    private const ulong SeekSettleTimeout = 2000;
    private long? _seekTarget;
    private ulong _seekDeadline;
    private bool _isDragging;
    private bool _wasPlaying;
    private bool _pauseOnFirstFrame;
    private bool _tracksListed;
    private int _listedTrackCount;
    private double _debugTimer;
    private double _framesPerSecond;

    /// <summary>The file being shown, or null if none.</summary>
    public string? VideoPath { get; private set; }

    public override void _Ready()
    {
        _frame = GetNode<TextureRect>("%Frame");
        _aspect = GetNode<AspectRatioContainer>("%Aspect");
        _emptyLabel = GetNode<Label>("%EmptyLabel");
        _timeline = GetNode<HSlider>("%Timeline");
        _playPauseButton = GetNode<Button>("%PlayPauseButton");
        _timeLabel = GetNode<Label>("%TimeLabel");
        _audioOption = GetNode<OptionButton>("%AudioOption");
        _subtitleOption = GetNode<OptionButton>("%SubtitleOption");
        _autoPlay = GetNode<CheckButton>("%AutoPlay");
        _deinterlace = GetNode<CheckButton>("%Deinterlace");
        _volumeSlider = GetNode<HSlider>("%VolumeSlider");
        _volumeBubble = GetNode<Control>("%VolumeBubble");
        _volumeBubbleLabel = GetNode<Label>("%VolumeBubbleLabel");
        _debugLabel = GetNode<Label>("%DebugLabel");
        _frame.Texture = _texture;
        // Without the signal: changing it says so, which is for the user's changes
        _volumeSlider.SetValueNoSignal(_volume);
        _volumeSlider.TooltipText = VolumeTooltip();

        if (StartVlc())
            UpdateEmptyState();
    }

    /// <summary>
    /// Makes this player's VLC, if it hasn't been made: at start, or if VLC couldn't be loaded then, when there's
    /// something to play (it may have been installed since).
    /// </summary>
    private bool StartVlc()
    {
        if (_mediaPlayer is not null)
            return true;
        if (!InitializeVlc())
        {
            _emptyLabel.Text = $"VLC couldn't be loaded: {_coreError}";
            return false;
        }

        try
        {
            // Deinterlacing starts off so interlaced sources show their combing; the Deinterlace switch turns it on
            _libVlc = new LibVLC("--no-video-title-show", "--no-osd", "--stats", "--deinterlace=0");
        }
        catch (VLCException ex)
        {
            _emptyLabel.Text = $"VLC couldn't start: {ex.Message}";
            return false;
        }

        _mediaPlayer = new MediaPlayer(_libVlc);
        _formatCallback = OnVideoFormat;
        _cleanupCallback = OnVideoCleanup;
        _lockCallback = OnVideoLock;
        _displayCallback = OnVideoDisplay;
        _mediaPlayer.SetVideoFormatCallbacks(_formatCallback, _cleanupCallback);
        _mediaPlayer.SetVideoCallbacks(_lockCallback, null, _displayCallback);
        return true;
    }

    // ---- Signals connected in the scene ----

    private void On_PlayPauseButton_Pressed() => TogglePlayback();

    private void On_PrevFrameButton_Pressed() => PreviousFrame();

    private void On_NextFrameButton_Pressed() => NextFrame();

    private void On_Deinterlace_Toggled(bool on) => ApplyDeinterlace();

    private void On_VolumeSlider_ValueChanged(double value)
    {
        _volume = (int)value;
        _volumeSlider.TooltipText = VolumeTooltip();
        ApplyVolume();
        ShowVolumeBubble();
    }

    private void On_VolumeSlider_DragStarted()
    {
        _draggingVolume = true;
        ShowVolumeBubble();
    }

    private void On_VolumeSlider_DragEnded(bool valueChanged)
    {
        _draggingVolume = false;
        _volumeBubbleTime = VolumeBubbleLinger;
    }

    private void On_AudioOption_ItemSelected(long index) => _mediaPlayer?.SetAudioTrack(_audioOption.GetItemId((int)index));

    private void On_SubtitleOption_ItemSelected(long index)
    {
        var id = _subtitleOption.GetItemId((int)index);
        _mediaPlayer?.SetSpu(id == SubtitlesOffId ? -1 : id);
    }

    public override void _ExitTree()
    {
        CloseMedia();
        _mediaPlayer?.Dispose();
        _libVlc?.Dispose();
        _mediaPlayer = null;
        _media = null;
        _libVlc = null;
        lock (_frameLock)
            FreeBuffer();
    }

    /// <summary>Loads a file, replacing whatever is shown; an empty path unloads. Plays if Auto-play is on.</summary>
    public void OpenVideo(string path)
    {
        if (string.IsNullOrEmpty(path))
            Close();
        else
            Open(new ClipPreview(path, null, PreviewTrackIds.None), null, null);
    }

    /// <summary>
    /// Loads a clip from a file or a stream (which this player then owns and disposes), replacing whatever is
    /// shown. With <paramref name="tracks"/>, the audio and subtitle lists offer just those tracks, in that order,
    /// with those names. <paramref name="length"/> is used when VLC can't tell the length itself. Plays if
    /// Auto-play is on.
    /// </summary>
    public void Open(ClipPreview preview, IReadOnlyList<ClipTrack>? tracks, TimeSpan? length)
    {
        if (!StartVlc() || _mediaPlayer is null || _libVlc is null)
        {
            preview.Stream?.Dispose();
            return;
        }

        CloseMedia();
        var stream = preview.Stream;
        if (stream is not null)
        {
            _stream = stream;
            _input = new StreamMediaInput(stream);
            _media = new Media(_libVlc, _input);
            var demux = preview.Demux;
            // A stream has no file name for VLC to guess the format from
            if (demux is not null)
                _media.AddOption($":demux={demux}");
            VideoPath = "(stream)";
        }
        else
        {
            _media = new Media(_libVlc, preview.FilePath!, FromType.FromPath);
            VideoPath = preview.FilePath;
        }

        _tracks = preview.TrackIds == PreviewTrackIds.None ? null : tracks;
        _trackIds = preview.TrackIds;
        _seekByPosition = preview.Stream is not null;
        _seekOffset = preview.SeekOffset;
        _knownLength = (long)(length?.TotalMilliseconds ?? 0);
        _mediaPlayer.Media = _media;
        ApplyDeinterlace();
        // VLC only shows a picture once it plays, so without Auto-play it plays until the first frame and pauses
        _pauseOnFirstFrame = !_autoPlay.ButtonPressed;
        _mediaPlayer.Play();
        UpdateEmptyState();
    }

    /// <summary>Stops playback and unloads the video.</summary>
    public void Close()
    {
        if (_mediaPlayer is null)
            return;
        CloseMedia();
        VideoPath = null;
        _texture.SetImage(Image.CreateEmpty(2, 2, false, Image.Format.Rgba8));
        UpdateEmptyState();
    }

    private void CloseMedia()
    {
        _mediaPlayer?.Stop();
        _media?.Dispose();
        _input?.Dispose();
        _stream?.Dispose();
        _media = null;
        _input = null;
        _stream = null;
        _tracks = null;
        _tracksListed = false;
        _audioOption.Clear();
        _subtitleOption.Clear();
        _framesPerSecond = 0;
        _visibleWidth = 0;
        _visibleHeight = 0;
        _timeline.Value = 0;
        _seekTarget = null;
    }

    /// <summary>Goes to <paramref name="time"/> in what's open, from its start, paused there to look at.</summary>
    public void Seek(TimeSpan time)
    {
        if (_mediaPlayer is null)
            return;
        Pause();
        SeekTo((long)time.TotalMilliseconds);
    }

    /// <summary>Where it is in what's open, from its start (where it's going, while a seek settles); null if nothing's open.</summary>
    public TimeSpan? Time => _mediaPlayer is null ? null : TimeSpan.FromMilliseconds(Math.Max(0, _seekTarget ?? _mediaPlayer.Time));

    /// <summary>Pauses playback, keeping the video loaded.</summary>
    public void Pause()
    {
        if (_mediaPlayer?.IsPlaying == true)
            _mediaPlayer.SetPause(true);
    }

    public override void _Process(double delta)
    {
        UpdateVolumeBubble(delta);
        if (_mediaPlayer is null)
            return;

        if (TakeFrame() && _pauseOnFirstFrame)
        {
            _pauseOnFirstFrame = false;
            _mediaPlayer.SetPause(true);
        }

        var length = Length;
        var time = _mediaPlayer.Time;
        var target = _seekTarget;
        if (target is not null)
        {
            if (Math.Abs(time - target.Value) <= SeekSettleTolerance || Godot.Time.GetTicksMsec() > _seekDeadline)
                _seekTarget = null;
            else
                time = target.Value;
        }

        if (!_isDragging && length > 0)
        {
            _updatingTimeline = true;
            _timeline.MaxValue = length;
            _timeline.Value = Math.Max(0, time);
            _updatingTimeline = false;
        }

        _timeLabel.Text = $"{FormatTime(time)} / {FormatTime(length)}";

        // VLC only takes a volume once its audio output exists, and each new clip makes a new one
        if (_mediaPlayer.State is VLCState.Playing or VLCState.Paused && _mediaPlayer.Volume >= 0 && _mediaPlayer.Volume != _volume)
            ApplyVolume();
        _playPauseButton.Text = _mediaPlayer.IsPlaying ? "❚❚" : "▶";

        // VLC adds tracks as it comes across them, so list them again whenever it finds more
        if (_mediaPlayer.State is VLCState.Playing or VLCState.Paused
            && (!_tracksListed || _mediaPlayer.AudioTrackCount + _mediaPlayer.SpuCount != _listedTrackCount)
            && _mediaPlayer.AudioTrackCount > 0)
            ListTracks();

        _debugTimer -= delta;
        if (_debugTimer <= 0)
        {
            _debugTimer = 0.25;
            UpdateDebug();
        }
    }

    // ---- Frames from VLC ----

    // Called by VLC (its thread) when the video's size is known: ask for 32-bit BGRX frames at that size
    private uint OnVideoFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        Marshal.Copy(Encoding.ASCII.GetBytes("RV32"), 0, chroma, 4);
        lock (_frameLock)
        {
            FreeBuffer();
            _frameWidth = (int)width;
            _frameHeight = (int)height;
            _buffer = Marshal.AllocHGlobal(_frameWidth * _frameHeight * 4);
            _frameBytes = new byte[_frameWidth * _frameHeight * 4];
            _frameReady = false;
        }

        pitches = width * 4;
        lines = height;
        return 1;
    }

    private void OnVideoCleanup(ref IntPtr opaque)
    {
        lock (_frameLock)
            FreeBuffer();
    }

    private IntPtr OnVideoLock(IntPtr opaque, IntPtr planes)
    {
        Marshal.WriteIntPtr(planes, _buffer);
        return IntPtr.Zero;
    }

    private void OnVideoDisplay(IntPtr opaque, IntPtr picture)
    {
        lock (_frameLock)
        {
            if (_buffer == IntPtr.Zero)
                return;
            Marshal.Copy(_buffer, _frameBytes, 0, _frameBytes.Length);
            _frameReady = true;
        }
    }

    /// <summary>Copies the newest frame into the texture, cropping VLC's padding. Returns whether there was one.</summary>
    private bool TakeFrame()
    {
        int width, height;
        lock (_frameLock)
        {
            if (!_frameReady)
                return false;
            _frameReady = false;

            width = _visibleWidth > 0 ? Math.Min(_visibleWidth, _frameWidth) : _frameWidth;
            height = _visibleHeight > 0 ? Math.Min(_visibleHeight, _frameHeight) : _frameHeight;
            if (_imageBytes.Length != width * height * 4)
                _imageBytes = new byte[width * height * 4];

            if (width == _frameWidth)
                Buffer.BlockCopy(_frameBytes, 0, _imageBytes, 0, _imageBytes.Length);
            else
                for (int row = 0; row < height; row++)
                    Buffer.BlockCopy(_frameBytes, row * _frameWidth * 4, _imageBytes, row * width * 4, width * 4);
        }

        _image.SetData(width, height, false, Image.Format.Rgba8, _imageBytes);
        if (_texture.GetWidth() == width && _texture.GetHeight() == height)
            _texture.Update(_image);
        else
            _texture.SetImage(_image);
        return true;
    }

    private void FreeBuffer()
    {
        if (_buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_buffer);
        _buffer = IntPtr.Zero;
        _frameReady = false;
    }

    // ---- Controls ----

    private void TogglePlayback()
    {
        if (_mediaPlayer?.Media is null)
            return;
        _pauseOnFirstFrame = false;
        if (_mediaPlayer.State is VLCState.Ended or VLCState.Stopped or VLCState.Error)
            Restart();
        else
            _mediaPlayer.SetPause(_mediaPlayer.IsPlaying);
        _playPauseButton.ReleaseFocus();
    }

    /// <summary>
    /// Plays the clip again from the start. VLC won't replay a finished media by Play alone: it has to be stopped
    /// first, and a stream rewound for VLC to read it again.
    /// </summary>
    private void Restart()
    {
        if (_mediaPlayer is null)
            return;
        _mediaPlayer.Stop();
        if (_stream is { CanSeek: true } stream)
            stream.Position = 0;
        _tracksListed = false;
        _mediaPlayer.Play();
    }

    /// <summary>Shows the volume above the slider's handle, as the tooltip can't stay up while dragging.</summary>
    private void ShowVolumeBubble()
    {
        _volumeBubbleLabel.Text = $"{_volume}%";
        _volumeBubbleTime = VolumeBubbleLinger;
        _volumeBubble.Visible = true;
        _volumeBubble.ResetSize();
        PlaceVolumeBubble();
    }

    private void UpdateVolumeBubble(double delta)
    {
        if (!_volumeBubble.Visible)
            return;
        if (!_draggingVolume)
        {
            _volumeBubbleTime -= delta;
            if (_volumeBubbleTime <= 0)
            {
                _volumeBubble.Visible = false;
                return;
            }
        }
        PlaceVolumeBubble();
    }

    private void PlaceVolumeBubble()
    {
        // The handle moves along the slider's width, less the handle itself
        var grabber = _volumeSlider.GetThemeIcon("grabber");
        float travel = _volumeSlider.Size.X - grabber.GetWidth();
        float fraction = (float)((_volumeSlider.Value - _volumeSlider.MinValue) / (_volumeSlider.MaxValue - _volumeSlider.MinValue));
        var handle = _volumeSlider.GlobalPosition + new Vector2(grabber.GetWidth() / 2f + travel * fraction, 0);
        _volumeBubble.GlobalPosition = new Vector2(handle.X - _volumeBubble.Size.X / 2f, handle.Y - _volumeBubble.Size.Y - 4);
    }

    private static string VolumeTooltip() =>
        _volume > 100 ? $"Volume: {_volume}% (amplified above the clip's own level; loud parts may distort)" : $"Volume: {_volume}%";

    private void ApplyVolume()
    {
        if (_mediaPlayer is not null)
            _mediaPlayer.Volume = _volume;
    }

    /// <summary>Applies the Deinterlace switch. Takes effect immediately, including on the video already playing.</summary>
    private void ApplyDeinterlace()
    {
        _mediaPlayer?.SetDeinterlace(_deinterlace.ButtonPressed ? "yadif" : "");
    }

    private void NextFrame()
    {
        _mediaPlayer?.NextFrame();
    }

    // VLC can only step forwards; step back by seeking one frame's time
    private void PreviousFrame()
    {
        if (_mediaPlayer is null || _framesPerSecond <= 0)
            return;
        _mediaPlayer.SetPause(true);
        SeekTo(Math.Max(0, _mediaPlayer.Time - (long)Math.Round(1000 / _framesPerSecond)));
    }

    /// <summary>
    /// The clip's known length, or VLC's when there isn't one. VLC guesses a DVD program stream's length from its
    /// bitrate, which on some cells is many times too long.
    /// </summary>
    private long Length => _knownLength > 0 ? _knownLength : Math.Max(0, _mediaPlayer?.Length ?? 0);

    private void SeekTo(long milliseconds)
    {
        if (_mediaPlayer is null)
            return;
        _seekTarget = milliseconds;
        _seekDeadline = Godot.Time.GetTicksMsec() + SeekSettleTimeout;
        if (_seekByPosition && _seekOffset is not null && _stream is { Length: > 0 } stream)
            _mediaPlayer.Position = (float)Math.Clamp((double)_seekOffset(TimeSpan.FromMilliseconds(milliseconds)) / stream.Length, 0, 1);
        else if (_seekByPosition && Length > 0)
            _mediaPlayer.Position = (float)Math.Clamp((double)milliseconds / Length, 0, 1);
        else
            _mediaPlayer.Time = milliseconds;
    }

    private void OnTimelineDragStarted()
    {
        _isDragging = true;
        _wasPlaying = _mediaPlayer?.IsPlaying == true;
        Pause();
    }

    private void OnTimelineDragEnded(bool valueChanged)
    {
        _isDragging = false;
        if (_mediaPlayer is null)
            return;
        SeekTo((long)_timeline.Value);
        if (_wasPlaying)
            _mediaPlayer.SetPause(false);
    }

    private void OnTimelineValueChanged(double value)
    {
        // Clicks seek straight away; drags seek when released
        if (!_updatingTimeline && !_isDragging && _mediaPlayer is not null)
            SeekTo((long)value);
    }

    private void ListTracks()
    {
        if (_mediaPlayer is null)
            return;
        _tracksListed = true;
        _listedTrackCount = _mediaPlayer.AudioTrackCount + _mediaPlayer.SpuCount;

        var vlcAudio = _mediaPlayer.AudioTrackDescription.Where(t => t.Id >= 0).ToList();
        var vlcSubtitles = _mediaPlayer.SpuDescription.Where(t => t.Id >= 0).ToList();

        _audioOption.Clear();
        _subtitleOption.Clear();
        _subtitleOption.AddItem("Off", SubtitlesOffId);
        if (_tracks is null)
        {
            foreach (var track in vlcAudio)
                _audioOption.AddItem(track.Name ?? $"Track {track.Id}", track.Id);
            foreach (var track in vlcSubtitles)
                _subtitleOption.AddItem(track.Name ?? $"Track {track.Id}", track.Id);
        }
        else
        {
            // The clip's own list: only its selected streams, in order, with their names
            AddClipTracks(_audioOption, _tracks.Where(t => t.Kind == ClipTrackKind.Audio).ToList(), vlcAudio);
            AddClipTracks(_subtitleOption, _tracks.Where(t => t.Kind == ClipTrackKind.Subtitle).ToList(), vlcSubtitles);

            // Start on the first listed audio track, as a player following the disc would
            if (_audioOption.ItemCount > 0 && _audioOption.GetItemIndex(_mediaPlayer.AudioTrack) < 0)
                _mediaPlayer.SetAudioTrack(_audioOption.GetItemId(0));
        }

        SelectId(_audioOption, _mediaPlayer.AudioTrack);
        _audioOption.Disabled = _audioOption.ItemCount < 2;
        SelectId(_subtitleOption, _mediaPlayer.Spu < 0 ? SubtitlesOffId : _mediaPlayer.Spu);
        _subtitleOption.Disabled = _subtitleOption.ItemCount < 2;
    }

    /// <summary>
    /// Adds the clip's tracks of one kind that VLC has, with the clip's names. A Blu-ray clip's are matched by PID;
    /// a primed DVD cell's by position, as VLC created its tracks in the clip's order.
    /// </summary>
    private void AddClipTracks(OptionButton option, List<ClipTrack> clipTracks, List<TrackDescription> vlcTracks)
    {
        for (int i = 0; i < clipTracks.Count; i++)
        {
            int? vlcId = _trackIds == PreviewTrackIds.PrimedOrder
                ? (i < vlcTracks.Count ? vlcTracks[i].Id : null)
                : (vlcTracks.Any(t => t.Id == clipTracks[i].StreamId) ? clipTracks[i].StreamId : null);
            if (vlcId is not null)
                option.AddItem(clipTracks[i].Label, vlcId.Value);
        }
    }

    private static void SelectId(OptionButton option, int id)
    {
        var index = option.GetItemIndex(id);
        if (index >= 0)
            option.Select(index);
    }

    // ---- Info ----

    private void UpdateDebug()
    {
        if (_mediaPlayer is null || _media is null)
        {
            _debugLabel.Text = "Player: VLC (LibVLCSharp)\nNo video";
            return;
        }

        var text = new StringBuilder("Player: VLC (LibVLCSharp)\n");
        var video = _media.Tracks.FirstOrDefault(t => t.TrackType == TrackType.Video);
        if (video.TrackType == TrackType.Video)
        {
            var v = video.Data.Video;
            var sar = v.SarNum > 0 && v.SarDen > 0 ? (double)v.SarNum / v.SarDen : 1.0;
            _framesPerSecond = v.FrameRateDen > 0 ? (double)v.FrameRateNum / v.FrameRateDen : 0;
            if (v.Width > 0 && v.Height > 0)
            {
                _aspect.Ratio = (float)(v.Width * sar / v.Height);
                _visibleWidth = (int)v.Width;
                _visibleHeight = (int)v.Height;
            }

            text.AppendLine($"Codec: {FourCC(video.Codec)}");
            text.AppendLine($"Resolution: {v.Width}x{v.Height}  (SAR {v.SarNum}:{v.SarDen})");
            text.AppendLine($"Display aspect: {_aspect.Ratio:0.000}");
            text.AppendLine($"Frame rate: {_framesPerSecond:0.###}");
        }

        text.AppendLine($"Output: {_frameWidth}x{_frameHeight}");
        text.AppendLine($"Time: {FormatTime(_mediaPlayer.Time)}");
        if (_framesPerSecond > 0)
            text.AppendLine($"Frame (approx.): {Math.Round(_mediaPlayer.Time / 1000.0 * _framesPerSecond)}");
        text.Append($"Audio tracks: {_mediaPlayer.AudioTrackCount}  Subtitles: {Math.Max(0, _mediaPlayer.SpuCount - 1)}");
        _debugLabel.Text = text.ToString();
    }

    private void UpdateEmptyState()
    {
        _emptyLabel.Visible = VideoPath is null;
        _timeline.Editable = VideoPath is not null;
        if (VideoPath is null && _libVlc is not null)
            _emptyLabel.Text = "No video selected";
    }

    private static string FourCC(uint codec) =>
        Encoding.ASCII.GetString(BitConverter.GetBytes(codec)).TrimEnd('\0', ' ');

    private static string FormatTime(long milliseconds)
    {
        var time = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}.{time.Milliseconds:000}";
    }

    /// <summary>
    /// Why VLC can't play anything here ("libvlc couldn't be loaded"), or null if it can: libvlc loads and starts with
    /// its plugins. Checked once, at start-up, as previews need it. The full error goes to the log.
    /// </summary>
    public static string? FindProblem()
    {
        if (!InitializeVlc())
            return "libvlc couldn't be loaded";
        try
        {
            using var vlc = new LibVLC("--no-video-title-show");
            return null;
        }
        catch (Exception ex)
        {
            GD.PrintErr($"VLC couldn't start: {ex}");
            return $"libvlc couldn't start ({ex.Message})";
        }
    }

    /// <summary>
    /// Loads the native libvlc. On Linux that is the system's (libvlc.so.5, from the vlc packages), which the
    /// default loader doesn't find without the development symlink. Tried again after failing, so VLC installed while
    /// the app's open is found.
    /// </summary>
    private static bool InitializeVlc()
    {
        if (_coreInitialized)
            return true;

        try
        {
            // An assembly's resolver can only be set once
            if (OperatingSystem.IsLinux() && !_resolverSet)
            {
                NativeLibrary.SetDllImportResolver(typeof(LibVLC).Assembly, (name, assembly, searchPath) =>
                    name == "libvlc" && NativeLibrary.TryLoad("libvlc.so.5", out var handle) ? handle : IntPtr.Zero);
                _resolverSet = true;
            }

            Core.Initialize();
            _coreInitialized = true;
            _coreError = null;
            return true;
        }
        catch (Exception ex)
        {
            _coreError = ex.Message;
            GD.PrintErr($"VLC couldn't be loaded: {ex}");
            return false;
        }
    }
}
