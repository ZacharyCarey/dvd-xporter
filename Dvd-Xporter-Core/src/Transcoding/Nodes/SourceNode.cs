using System.Globalization;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using FFmpeg;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>What a source node plays: its disc, the clips in order, and the first clip's tracks.</summary>
public sealed record SourceSelection(ProjectDisc Disc, string Name, IReadOnlyList<PlaylistEntry> Entries, IReadOnlyList<ClipTrack> Tracks,
    PreciseTime? Duration);

/// <summary>
/// A Disc Video, or a template's Template Input that stands in for one: a video file or playlist from one of the
/// project's discs, played from a start to an end (each a time, or a chapter). It gives the video's tracks and
/// chapters.
/// </summary>
public sealed class SourceNode(CanvasNode node)
{
    public const string DiscKey = "disc";
    public const string ItemKey = "item";
    public const string StartByKey = "start-by";
    public const string StartKey = "start";
    public const string StartChapterKey = "start-chapter";
    public const string EndByKey = "end-by";
    public const string EndKey = "end";
    public const string EndChapterKey = "end-chapter";

    // Start-by and end-by values
    private const string ByTime = "time";
    private const string ByChapter = "chapter";

    private static readonly SettingChoice[] RangeModes = [new(ByTime, "Time"), new(ByChapter, "Chapter")];

    // Before the types, which use them
    private static readonly NodeSetting[] Settings =
    [
        new(DiscKey, "Disc", SettingKind.Choice),
        new(ItemKey, "Video", SettingKind.Choice),
        new(StartByKey, "Start by", SettingKind.Choice, ByTime, RangeModes,
            Tooltip: "Start at a time, or at the beginning of one of the video's chapters."),
        new(StartKey, "Start", SettingKind.Time, Placeholder: "Beginning", Visible: n => !new SourceNode(n).StartsByChapter),
        new(StartChapterKey, "Start", SettingKind.Choice, "1", Visible: n => new SourceNode(n).StartsByChapter,
            Tooltip: "Starts at the beginning of this chapter."),
        new(EndByKey, "End by", SettingKind.Choice, ByTime, RangeModes,
            Tooltip: "End at a time, or at the end of one of the video's chapters."),
        new(EndKey, "End", SettingKind.Time, Placeholder: "End", Visible: n => !new SourceNode(n).EndsByChapter),
        new(EndChapterKey, "End", SettingKind.Choice, Visible: n => new SourceNode(n).EndsByChapter,
            Tooltip: "Ends at the end of this chapter."),
    ];

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Source,
        Title = "Disc Video",
        Category = NodeCategory.Inputs,
        Description = "A video file or playlist from one of the project's discs, with its tracks.",
        Settings = Settings,
        Ports = (n, context) => new SourceNode(n).Ports(context),
    };

    public static readonly NodeType TemplateInputType = new()
    {
        Id = NodeTypes.TemplateInput,
        Title = "Template Input",
        Category = NodeCategory.Inputs,
        Description = "Stands for each video a template is used with. Pick a sample video to build and test the template with; "
            + "its tracks are matched by ID in the others.",
        Settings = Settings,
        Ports = (n, context) => new SourceNode(n).Ports(context),
        AllowedIn = kind => kind == CanvasKind.Template,
    };

    /// <summary>A new Disc Video, e.g. to ask about one of a disc's videos.</summary>
    public static CanvasNode Create(string disc, string item = "", int id = 0)
    {
        var created = new CanvasNode { Id = id, Type = NodeTypes.Source };
        _ = new SourceNode(created) { Disc = disc, Item = item };
        return created;
    }

    public CanvasNode Node => node;

    /// <summary>The disc's fingerprint (see <see cref="ProjectDisc.Fingerprint"/>).</summary>
    public string Disc
    {
        get => node.Get(DiscKey);
        set => node.Set(DiscKey, value);
    }

    /// <summary>What it plays: one of the disc's playlists or video files (see <see cref="NodeTypes.PlaylistItemPrefix"/>).</summary>
    public string Item
    {
        get => node.Get(ItemKey);
        set => node.Set(ItemKey, value);
    }

    /// <summary>Whether it starts at one of its chapters, rather than at a time.</summary>
    public bool StartsByChapter
    {
        get => node.Get(StartByKey, ByTime) == ByChapter;
        set => node.Set(StartByKey, value ? ByChapter : null);
    }

    /// <summary>Where it starts, when it starts at a time; null = the beginning.</summary>
    public PreciseTime? Start => node.GetTime(StartKey);

    /// <summary>The chapter it starts at, counting from 1, when it starts at a chapter.</summary>
    public int StartChapter
    {
        get => node.GetInt(StartChapterKey, 1);
        set => node.Set(StartChapterKey, value.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Whether it ends at the end of one of its chapters, rather than at a time.</summary>
    public bool EndsByChapter
    {
        get => node.Get(EndByKey, ByTime) == ByChapter;
        set => node.Set(EndByKey, value ? ByChapter : null);
    }

    /// <summary>Where it ends, when it ends at a time; null = the end.</summary>
    public PreciseTime? End => node.GetTime(EndKey);

    /// <summary>The chapter it ends with, counting from 1, when it ends at a chapter; null = the last.</summary>
    public int? EndChapter
    {
        get => int.TryParse(node.Get(EndChapterKey), NumberStyles.Integer, CultureInfo.InvariantCulture, out var chapter) ? chapter : null;
        set => node.Set(EndChapterKey, value?.ToString(CultureInfo.InvariantCulture));
    }

    public ProjectDisc? FindDisc(Project project) => NodeTypes.FindDisc(project, Disc);

    /// <summary>
    /// What it's set to play, or null if it isn't set or the disc hasn't been read yet. A playlist's tracks are its
    /// first clip's: a playlist's clips almost always share their layout.
    /// </summary>
    public SourceSelection? Resolve(ITranscodeContext context)
    {
        var disc = FindDisc(context.Project);
        if (disc is null)
            return null;
        var contents = context.GetContents(disc);
        if (contents is null)
            return null;

        var item = Item;
        List<PlaylistEntry> entries;
        string name;
        if (item.StartsWith(NodeTypes.PlaylistItemPrefix, StringComparison.Ordinal))
        {
            var id = item[NodeTypes.PlaylistItemPrefix.Length..];
            var playlist = disc.Playlists.Find(p => p.Id == id);
            if (playlist is null)
                return null;
            entries = [.. playlist.Entries];
            name = playlist.Name;
        }
        else if (item.StartsWith(NodeTypes.ClipItemPrefix, StringComparison.Ordinal))
        {
            var file = item[NodeTypes.ClipItemPrefix.Length..];
            entries = [new PlaylistEntry(file)];
            name = disc.GetClipName(file) ?? file;
        }
        else
        {
            return null;
        }

        var first = entries.Count > 0 ? contents.FindClip(entries[0].Clip) : null;
        if (first is null)
            return new SourceSelection(disc, name, entries, [], null);

        var duration = DiscTime.Zero;
        foreach (var entry in entries)
        {
            var trimmed = entry.TrimmedDuration;
            if (trimmed is not null)
            {
                duration += trimmed.Value;
            }
            else
            {
                var clip = contents.FindClip(entry.Clip);
                if (clip is not null)
                    duration += clip.Duration;
            }
        }

        var tracks = first.Tracks ?? [];
        var present = context.GetPresentTracks(disc, entries);
        if (present is not null)
            tracks = tracks.Where(t => present.Contains(t.StreamId)).ToList();
        return new SourceSelection(disc, name, entries, tracks, duration.ToPreciseTime());
    }

    /// <summary>
    /// Where it starts and ends on its own timeline. Each end is a time, or a chapter: the start of the start
    /// chapter, the end of the end chapter. Null = the beginning or the end.
    /// </summary>
    public (PreciseTime? Start, PreciseTime? End) Range(ITranscodeContext context)
    {
        var byChapter = StartsByChapter || EndsByChapter;
        var chapters = byChapter && ChapterLists.AllOfSource(node, context) is var (all, _, _) ? all : [];

        var start = Start;
        if (StartsByChapter)
            start = chapters.Count > 0 && chapters[ClampedStartChapter(chapters.Count) - 1].Start is var s && s > PreciseTime.Zero ? s : null;

        var end = End;
        if (EndsByChapter)
        {
            // An end chapter before the start chapter plays just the start chapter
            var last = Math.Clamp(EndChapter ?? chapters.Count, 1, Math.Max(1, chapters.Count));
            if (StartsByChapter)
                last = Math.Max(last, ClampedStartChapter(chapters.Count));
            end = last < chapters.Count ? chapters[last].Start : null;
        }

        return (start, end);
    }

    private int ClampedStartChapter(int count) => Math.Clamp(StartChapter, 1, Math.Max(1, count));

    /// <summary>Its tracks, and its chapters once it's set to play something.</summary>
    public NodePorts Ports(ITranscodeContext context)
    {
        var selection = Resolve(context);
        if (selection is null)
            return new NodePorts([], []);
        var outputs = selection.Tracks.Select(t => new NodePort(TrackPortKey(t), t.Label, TrackPortKind(t.Kind))).ToList();
        outputs.Add(new NodePort(NodeTypes.ChaptersPort, "Chapters", PortKind.Chapters));
        return new NodePorts([], outputs);
    }

    /// <summary>A track's port key, e.g. "a:80" for DVD AC-3 stream 0x80 or "v:1011" for a Blu-ray video PID.</summary>
    public static string TrackPortKey(ClipTrack track) => track.Kind switch
    {
        ClipTrackKind.Video => "v:",
        ClipTrackKind.Audio => "a:",
        _ => "s:",
    } + track.StreamId.ToString("x");

    public static PortKind TrackPortKind(ClipTrackKind kind) => kind switch
    {
        ClipTrackKind.Video => PortKind.Video,
        ClipTrackKind.Audio => PortKind.Audio,
        _ => PortKind.Subtitle,
    };

    /// <summary>
    /// The ffmpeg input that reads what's played, from <paramref name="start"/> to <paramref name="end"/>, and how
    /// each of its tracks is picked out once it's input number <paramref name="index"/>.
    /// </summary>
    public static (FFmpegInput Input, IReadOnlyList<string> TrackSpecifiers) BuildInput(SourceSelection selection, SourceMedia media,
        PreciseTime? start, PreciseTime? end, int index)
    {
        var options = new List<string>();
        // Video in a program stream (DVD) has packets without a presentation time (B-frames carry only a decode
        // time), which MKV won't take when it's copied: ffmpeg works them out
        var programStream = selection.Disc.Format == DiscFormat.Dvd;
        if (programStream)
            options.AddRange(["-fflags", "+genpts"]);
        if (media.IsFilm)
            options.AddRange(["-r", "24000/1001"]);

        // A single whole clip in a container that lists its tracks (a Blu-ray transport stream) is read as it is,
        // and its tracks found by PID. Anything else goes through the concat demuxer, which declares the tracks up
        // front (see ConcatInput).
        var whole = media.Parts.Count == 1 && media.Parts[0] is { InPoint: null, OutPoint: null };
        if (!programStream && whole)
        {
            return (new FileInput(media.Parts[0].Source, start.ToFFmpegTime(), end.ToFFmpegTime(), options),
                selection.Tracks.Select(t => $"{index}:i:0x{t.StreamId:x}").ToList());
        }

        // The disc's subtitle codec names are ffmpeg's (dvd_subtitle, hdmv_pgs_subtitle, hdmv_text_subtitle)
        var streams = selection.Tracks.Select(t => new ConcatStream(
            FfmpegStreamId(programStream, t),
            t.Kind == ClipTrackKind.Subtitle ? t.Codec : null,
            t.Kind == ClipTrackKind.Subtitle ? media.SubtitleExtradata : null)).ToList();
        return (new ConcatInput(media.Parts, streams, start.ToFFmpegTime(), end.ToFFmpegTime(), options),
            selection.Tracks.Select((_, t) => $"{index}:{t}").ToList());
    }

    /// <summary>
    /// ffmpeg's ID for a track: in a transport stream its PID; in a program stream the full stream ID (0x1E0, 0x1C0)
    /// for MPEG video and audio, or the substream ID (0x80, 0x20, ...) for tracks in private stream 1.
    /// </summary>
    private static int FfmpegStreamId(bool programStream, ClipTrack track) =>
        programStream && track.StreamId >= 0xC0 ? 0x100 | track.StreamId : track.StreamId;
}
