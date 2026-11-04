using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;
using DiscFile = DiscDecoders.DiscFile;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// A project's discs as transcoding sees them: what the canvas and the compiler need to know about them. Made of parts
/// with one job each:
/// <list type="bullet">
/// <item><see cref="Discs"/>: where each disc is, what's been learnt from it, and its contents.</item>
/// <item><see cref="Scans"/>: what the clips' scans (made when their disc was read) tell.</item>
/// <item><see cref="Pictures"/>: what the clips' pictures (checked when their disc was read) tell.</item>
/// <item><see cref="Media"/>: how ffmpeg reads a source's clips.</item>
/// <item><see cref="ForcedSubtitles"/>: forced-only subtitle files.</item>
/// <item><see cref="SubtitleText"/>: text subtitle files, from subtitles' text.</item>
/// </list>
/// </summary>
public sealed class ProjectTranscodeSources : ICanvasContext, IClipFiles
{
    /// <param name="sources">Where each disc is, as resolved for this user (missing = not found).</param>
    public ProjectTranscodeSources(ProjectFolder folder, IReadOnlyDictionary<ProjectDisc, DiscSource?> sources)
    {
        Folder = folder;
        Discs = new ProjectDiscs(folder, sources);
        Scans = new ClipScans(Discs);
        Pictures = new PictureAnalyses(this);
        Media = new SourceMediaReader(Discs, Scans);
        ForcedSubtitles = new ForcedSubtitleFiles(Discs, Scans);
        SubtitleText = new SubtitleTextFiles(folder, Discs, Scans);
    }

    public ProjectFolder Folder { get; }

    public ProjectDiscs Discs { get; }

    public ClipScans Scans { get; }

    public PictureAnalyses Pictures { get; }

    public SourceMediaReader Media { get; }

    public ForcedSubtitleFiles ForcedSubtitles { get; }

    public SubtitleTextFiles SubtitleText { get; }

    public Project Project => Folder.Project;

    /// <summary>The canvas being compiled or shown; set before using this as an <see cref="ICanvasContext"/>.</summary>
    public TranscodeCanvas Canvas { get; set; } = new();

    /// <summary>This as the context of another canvas, leaving <see cref="Canvas"/> as it is.</summary>
    public ICanvasContext ForCanvas(TranscodeCanvas canvas) => new CanvasView(this, canvas);

    public DiscSource? GetSource(ProjectDisc disc) => Discs.GetSource(disc);

    public DiscContents? GetContents(ProjectDisc disc) => Discs.GetContents(disc);

    /// <summary>Forgets the discs' other files, e.g. after the discs are found somewhere else.</summary>
    public void Refresh() => Discs.Refresh();

    public IReadOnlyList<DiscFile>? GetDiscFiles(ProjectDisc disc) => Discs.GetDiscFiles(disc);

    // ---- ICanvasContext and IClipFiles ----

    public IReadOnlyCollection<int>? GetPresentTracks(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries) => Scans.PresentTracks(disc, entries);

    public PictureAnalysis? GetPicture(CanvasNode source) => Pictures.Get(source);

    public IReadOnlyDictionary<int, SubtitleCount>? GetSubtitleCounts(CanvasNode source) =>
        new SourceNode(source).Resolve(this) is { Entries.Count: > 0 } selection
            ? Scans.SubtitleCounts(selection.Disc, selection.Entries)
            : null;

    public SourceMedia? GetMedia(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries) => Media.Get(disc, entries);

    public string? GetForcedSubtitles(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, ClipTrack track) =>
        ForcedSubtitles.Get(disc, entries, track);

    public SubtitleTextFile GetSubtitleText(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, ClipTrack track, bool forcedOnly) =>
        SubtitleText.Get(disc, entries, track, forcedOnly);

    /// <summary>The sources seen from another canvas: the same in all but <see cref="Canvas"/>.</summary>
    private sealed class CanvasView(ProjectTranscodeSources sources, TranscodeCanvas canvas) : ICanvasContext
    {
        public TranscodeCanvas Canvas => canvas;

        public Project Project => sources.Project;

        public DiscContents? GetContents(ProjectDisc disc) => sources.GetContents(disc);

        public IReadOnlyCollection<int>? GetPresentTracks(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries) =>
            sources.GetPresentTracks(disc, entries);

        public PictureAnalysis? GetPicture(CanvasNode source) => sources.GetPicture(source);

        public IReadOnlyDictionary<int, SubtitleCount>? GetSubtitleCounts(CanvasNode source) => sources.GetSubtitleCounts(source);
    }
}
