namespace DvdXporter.Core.Clips;

/// <summary>How a clip's tracks are identified, for matching a player's tracks to <see cref="ClipSummary.Tracks"/>.</summary>
public enum PreviewTrackIds
{
    /// <summary>Don't match: the file's own tracks are already in order and labelled.</summary>
    None,

    /// <summary>
    /// DVD cell stream that announces its tracks up front (see <see cref="DiscDecoders.Disc.OpenForPlayback"/>): the
    /// player's audio and subtitle tracks come in <see cref="ClipSummary.Tracks"/>' order, so the n-th of each kind is
    /// the n-th listed.
    /// </summary>
    PrimedOrder,

    /// <summary>Blu-ray clip: <see cref="ClipTrack.StreamId"/> is the PID.</summary>
    TransportStreamPid,
}

/// <summary>
/// How to play a clip: <see cref="FilePath"/> if it's an ordinary file, otherwise <see cref="Stream"/>, which the
/// player owns and disposes. <see cref="Demux"/> names the stream's container ("ps" or "ts") for players that
/// can't tell from a stream alone. <see cref="SeekOffset"/>, if set, gives the stream's byte offset for a time
/// from the clip's start (a Blu-ray clip's entry point map), for exact seeking.
/// </summary>
public sealed record ClipPreview(string? FilePath, Stream? Stream, PreviewTrackIds TrackIds, string? Demux = null,
    Func<TimeSpan, long>? SeekOffset = null);
