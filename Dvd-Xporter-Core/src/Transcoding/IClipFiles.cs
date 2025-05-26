using DvdXporter.Core.Projects;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>Where the clips to transcode are on this computer, and how ffmpeg reads them.</summary>
public interface IClipFiles
{
    /// <summary>How to read a source's clips, or null if its disc isn't on this computer.</summary>
    SourceMedia? GetMedia(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries);

    /// <summary>
    /// A file holding just a subtitle track's forced captions, timed from the source's start (before its own start
    /// trim), or null if it has none (or a clip couldn't be read through, so it isn't known).
    /// </summary>
    string? GetForcedSubtitles(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, Clips.ClipTrack track);
}

/// <summary>How ffmpeg reads one Disc Video source.</summary>
/// <param name="Parts">Files or local addresses (see <see cref="ClipStreamServer"/>) played in order.</param>
/// <param name="IsFilm">Whether the video is film (23.976 fps) stored as soft-telecined NTSC, so its frames are
/// timed at the film rate rather than on a 29.97 grid.</param>
/// <param name="SubtitleExtradata">For DVD subtitles: their colours, as the codec's extradata.</param>
public sealed record SourceMedia(IReadOnlyList<ConcatPart> Parts, bool IsFilm = false, string? SubtitleExtradata = null);
