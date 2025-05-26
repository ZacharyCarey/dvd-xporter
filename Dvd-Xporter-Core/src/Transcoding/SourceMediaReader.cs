using DiscDecoders.Common;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// How ffmpeg reads a source's clips on this computer (see <see cref="SourceMedia"/>). Blu-ray folders are read as files;
/// clips inside images and DVD cells are streamed straight off the disc through <see cref="ClipStreamServer"/>, so
/// nothing is copied first. A disc on the render server is streamed from there, through the same local addresses.
/// </summary>
public sealed class SourceMediaReader(ProjectDiscs discs, ClipScans scans)
{
    // A cell is film if its pictures come at the film rate (it's soft-telecined to 29.97 on the disc)
    private const double FilmFrameRate = 24000 / 1001.0;

    /// <summary>How to read the entries' clips, or null if their disc isn't on this computer (or the render server).</summary>
    public SourceMedia? Get(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries)
    {
        var source = discs.GetSource(disc);
        if (source is null)
            return null;

        var cache = discs.Cache(disc);
        // Addresses are kept for the app's run, so one streamed from the server is told apart from one off the disc here
        var key = source.StreamKey;
        // A DVD's cells follow on from each other in its VOBs, so they're read in runs, as a player reads them
        if (disc.Format == DiscFormat.Dvd)
            return GetJoined(disc, entries, key, clips => source.OpenClips(clips, cache.LeadInOf(clips)));

        return GetClipByClip(disc, entries, clip =>
        {
            var file = source.GetClipFilePath(clip);
            if (file is not null)
                return FFmpegSource.FromFile(file);
            return FFmpegSource.FromUrl(ClipStreamServer.Shared.Register($"{key}/{clip}", clip, () => source.OpenClip(clip)));
        });
    }

    /// <summary>A source read clip by clip: each from its file if there is one, or streamed.</summary>
    /// <param name="sourceOf">Where ffmpeg reads a clip.</param>
    private SourceMedia GetClipByClip(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, Func<string, FFmpegSource> sourceOf)
    {
        var contents = discs.GetContents(disc);
        return new SourceMedia(entries
            .Select(e => new ConcatPart(sourceOf(e.Clip), e.InTime.ToFFmpegTime(), e.OutTime.ToFFmpegTime(), (e.TrimmedDuration ?? contents?.FindClip(e.Clip)?.Duration).ToFFmpegTime(),
                (entries.Count > 1 || e.IsTrimmed ? contents?.FindClip(e.Clip)?.Start : null).ToFFmpegTime()))
            .ToList(), IsFilm(scans.Get(disc, entries)), discs.SubtitlePalette(disc, entries[0].Clip));
    }

    /// <summary>
    /// A source from a disc whose clips join seamlessly (a DVD's cells), as runs of clips: clips that play on (one ends
    /// where the next starts) are served as one stream, as a player reads them, with the run's lead-in blanked out.
    /// </summary>
    /// <param name="open">Opens a run of clips as one stream.</param>
    private SourceMedia GetJoined(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, string key, Func<IReadOnlyList<string>, Stream> open)
    {
        var cache = discs.Cache(disc);
        var contents = discs.GetContents(disc);
        var clipScans = scans.Get(disc, entries);
        var runs = cache.GroupSeamless(entries.Select(e => e.Clip).ToList());
        var extension = disc.Format == DiscFormat.Dvd ? ".vob" : ".ts";

        var parts = runs.Select(run =>
        {
            var clips = run.Select(i => entries[i].Clip).ToList();
            var name = Path.GetFileNameWithoutExtension(clips[0]) + (clips.Count > 1 ? $"+{clips.Count - 1}" : "") + extension;
            var url = ClipStreamServer.Shared.Register($"{key}/{string.Join("+", clips)}", name, () => open(clips));

            // A run plays from its first clip's start to its last's end
            var first = clipScans[run[0]]?.Start;
            var last = clipScans[run[^1]]?.End;
            FFmpegTime? duration = first is not null && last is not null && last.Value > first.Value
                ? (last.Value - first.Value).ToFFmpegTime()
                : run.Aggregate((DiscTime?)DiscTime.Zero, (total, i) =>
                {
                    var length = contents?.FindClip(entries[i].Clip)?.Duration;
                    return total is not null && length is not null ? total.Value + length.Value : null;
                }).ToFFmpegTime();
            return new ConcatPart(FFmpegSource.FromUrl(url), null, null, duration);
        }).ToList();

        return new SourceMedia(parts, IsFilm(clipScans), discs.SubtitlePalette(disc, entries[0].Clip));
    }

    /// <summary>
    /// Whether every clip is film: its pictures come at the film rate though it plays at 29.97 (soft-telecined), which
    /// only a scan can tell.
    /// </summary>
    private static bool IsFilm(List<ClipScan?> scans) =>
        scans.All(s => s?.Duration is { TotalSeconds: > 1 } d && s.Pictures is not null
            && Math.Abs(s.Pictures.Value / d.TotalSeconds - FilmFrameRate) < FilmFrameRate * 0.01);
}
