namespace DiscDecoders.Common;

/// <summary>
/// What reading a clip through once found (see <c>Disc.ScanClip</c>): what its navigation files don't say. Anything
/// the scan doesn't measure for a kind of disc is null or empty.
/// </summary>
/// <param name="StreamIds">The streams that have data, in the order they first appear (see <see cref="StreamInfo.StreamId"/>),
/// or null if not checked.</param>
/// <param name="Start">The timestamp the clip's data starts at, or null if not measured.</param>
/// <param name="End">The timestamp its data ends at, or null if not measured.</param>
/// <param name="Pictures">How many pictures its video has, or null if not counted.</param>
/// <param name="LeadIn">Packets before its first picture that belong to the clip before it (a disc muxes sound ahead
/// of pictures), so a player starting here would play them early: blank them when it's played on its own.</param>
/// <param name="Subtitles">Each subtitle stream's captions.</param>
/// <param name="Interlaced">Whether its video is stored interlaced (see <see cref="VideoStreamInfo.Interlaced"/>, which
/// it decides where the navigation files don't say), or null if not checked.</param>
/// <param name="Audio">The audio streams' bitrates, measured at the clip's start (see <see cref="BitrateMeter"/>), which
/// navigation files don't give; null if not measured.</param>
public sealed record ClipScan(IReadOnlyList<int>? StreamIds, DiscTime? Start, DiscTime? End, int? Pictures,
    IReadOnlyList<ByteRange> LeadIn, IReadOnlyList<SubtitleScan> Subtitles, bool? Interlaced = null,
    IReadOnlyList<AudioBitrate>? Audio = null)
{
    /// <summary>How long its data plays, if measured.</summary>
    public DiscTime? Duration => (Start is not null) && (End is not null) && End > Start ? End - Start : null;
}

/// <summary>An audio stream's bitrate in kbps, as measured (see <see cref="BitrateMeter"/>).</summary>
public sealed record AudioBitrate(int StreamId, int Kbps);

/// <summary>A run of bytes in a clip's stream.</summary>
public sealed record ByteRange(long Offset, int Length);

/// <summary>A subtitle stream's captions.</summary>
/// <param name="Total">Captions shown.</param>
/// <param name="Forced">Of those, how many are forced: meant to show even with subtitles off.</param>
/// <param name="ForcedCaptions">The forced captions in order, ready for a forced-only track.</param>
public sealed record SubtitleScan(int StreamId, int Total, int Forced, IReadOnlyList<ForcedCaption> ForcedCaptions);

/// <summary>
/// A forced caption as the stream carries it, or what takes it off screen again where the stream needs one (Blu-ray
/// subtitles clear a caption with the next display set).
/// </summary>
/// <param name="Time">When it shows, on the clip's own timeline (as <see cref="ClipScan.Start"/>).</param>
/// <param name="Data">The caption in the stream's own format: a DVD subtitle unit (dvd_subtitle), or a Blu-ray display
/// set as .sup records (hdmv_pgs_subtitle), each with its original timestamps.</param>
public sealed record ForcedCaption(DiscTime Time, byte[] Data);
