using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

/// <summary>What node types need to know about the project, e.g. a disc's clips for a source's tracks.</summary>
public interface ITranscodeContext
{
    Project Project { get; }

    /// <summary>The disc's clips and playlists, or null if it hasn't been read yet.</summary>
    DiscContents? GetContents(ProjectDisc disc);

    /// <summary>
    /// The IDs of the tracks (<see cref="ClipTrack.StreamId"/>) that really have data in these clips, or null if
    /// that isn't known. A DVD's IFO lists tracks a cell may not have; its scan says which it has.
    /// </summary>
    IReadOnlyCollection<int>? GetPresentTracks(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries);

    /// <summary>
    /// Whether a Disc Video's picture is progressive, interlaced or telecined, as checked when its disc was read; null if
    /// it couldn't be told.
    /// </summary>
    PictureAnalysis? GetPicture(CanvasNode source);

    /// <summary>
    /// How many captions each of a Disc Video's subtitle tracks shows, and how many are forced, by stream ID; null if it
    /// isn't set, or a clip it plays couldn't be read.
    /// </summary>
    IReadOnlyDictionary<int, SubtitleCount>? GetSubtitleCounts(CanvasNode source);
}

/// <summary>A context that also knows the canvas, for node rows that depend on the node's links.</summary>
public interface ICanvasContext : ITranscodeContext
{
    TranscodeCanvas Canvas { get; }
}
