using DiscDecoders;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// The pictures of a disc's clips (see <see cref="PictureDetector"/>): checked when the disc is read and saved with what
/// was read (see <see cref="ClipSummary.Picture"/>), and what they say about the clips a Disc Video plays.
/// </summary>
public sealed class PictureAnalyses(ProjectTranscodeSources sources)
{
    /// <summary>What the pictures of a Disc Video's clips are, together; null if it isn't set or none could be checked.</summary>
    public PictureAnalysis? Get(CanvasNode source)
    {
        if (new SourceNode(source).Resolve(sources) is not { Entries.Count: > 0 } selection)
            return null;
        var contents = sources.GetContents(selection.Disc);
        return PictureAnalysis.Combine(selection.Entries.Select(e => contents?.FindClip(e.Clip)?.Picture).OfType<PictureAnalysis>().ToList());
    }

    /// <summary>
    /// Checks the pictures of every clip with video, as part of reading the disc (see <see cref="DiscSource.ReadInfoAsync"/>),
    /// from what's been read so far (held in <paramref name="cache"/>): every frame, if the disc was added to have them checked (see
    /// <see cref="ProjectDisc.CheckEveryFrame"/>), which takes as long as decoding the whole disc; otherwise only their
    /// colour, a few seconds a clip, and their type is <see cref="PictureType.Unknown"/>.
    /// </summary>
    /// <param name="progress">Told as each clip is checked, with how far through checking them all it is.</param>
    /// <returns>What each clip's check found, by clip; null where it couldn't be told.</returns>
    internal static async Task<Dictionary<string, PictureAnalysis?>> CheckClipsAsync(DiscCache cache, DiscSource source,
        IProgress<DiscContentsProgress>? progress, Action<string>? onWarning, CancellationToken cancellation)
    {
        var disc = cache.Disc;
        // The cache holds what's been read so far, and is the one these sources use for the disc
        var sources = new ProjectTranscodeSources(cache.Folder, new Dictionary<ProjectDisc, DiscSource?> { [disc] = source });
        var clips = sources.GetContents(disc)?.Clips.Where(c => c.Tracks?.Any(t => t.Kind == ClipTrackKind.Video) == true).ToList() ?? [];
        var workFolder = Path.Combine(cache.FolderPath, "work", "picture");

        // Checking every frame takes as long as the clip, so how far it's got goes by their lengths
        double total = Math.Max(1, clips.Sum(c => c.Duration.ToPreciseTime().Nanoseconds)), done = 0;
        var pictures = new Dictionary<string, PictureAnalysis?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < clips.Count; i++)
        {
            var index = i;
            var length = clips[i].Duration.ToPreciseTime().Nanoseconds;
            var before = done;
            void report(double fraction) => progress?.Report(new DiscContentsProgress(true, index, clips.Count, (before + fraction * length) / total));
            report(0);
            try
            {
                var (analysis, problem) = await CheckClipAsync(sources, disc, clips[i].File, workFolder, disc.CheckEveryFrame ? report : null, cancellation);
                pictures[clips[i].File] = analysis;
                if (problem is not null)
                    onWarning?.Invoke($"{clips[i].File}: {problem}");
            }
            catch (Exception ex) when (ex is IOException or DiscException)
            {
                onWarning?.Invoke($"{clips[i].File}: {ex.Message}");
            }

            done += length;
        }

        progress?.Report(new DiscContentsProgress(true, clips.Count, clips.Count, 1));
        return pictures;
    }

    /// <summary>
    /// Checks one clip's first video track: what was found, or null and why (no reason if it's too short to tell).
    /// With <paramref name="everyFrame"/>, each frame is judged, reporting how far it's got.
    /// </summary>
    private static async Task<(PictureAnalysis? Analysis, string? Problem)> CheckClipAsync(ProjectTranscodeSources sources, ProjectDisc disc,
        string clip, string workFolder, Action<double>? everyFrame, CancellationToken cancellation)
    {
        // A canvas of its own: just the clip
        var canvas = new TranscodeCanvas();
        var source = SourceNode.Create(disc.Fingerprint, NodeTypes.ClipItemPrefix + clip, id: 1);
        canvas.Nodes.Add(source);
        var context = sources.ForCanvas(canvas);

        var selection = new SourceNode(source).Resolve(context);
        if (selection is null)
            return (null, null);
        var video = selection.Tracks.FirstOrDefault(t => t.Kind == ClipTrackKind.Video);
        if (video is null)
            return (null, null);

        TranscodeJob? Probe(string filter, out string? problem) =>
            TranscodeCompiler.CompileProbe(canvas, context, sources, source.Id, source.Id, SourceNode.TrackPortKey(video), filter, out problem);
        var colourProbe = Probe(PictureDetector.ColourFilter, out var problem);
        if (colourProbe is null)
            return (null, problem);
        TranscodeJob? framesProbe = null;
        if (everyFrame is not null && (framesProbe = Probe(PictureDetector.FramesFilter, out problem)) is null)
            return (null, problem);

        var isFilm = sources.GetMedia(disc, selection.Entries)?.IsFilm == true;
        var dolbyVision = selection.Tracks.Any(t => t.Kind == ClipTrackKind.Video && t.Label.Contains("enhancement layer", StringComparison.Ordinal));
        // Too little to tell (a menu's still, a short logo) is left unknown rather than reported
        return (await PictureDetector.DetectAsync(colourProbe, framesProbe, isFilm, dolbyVision, workFolder, everyFrame, cancellation), null);
    }
}
