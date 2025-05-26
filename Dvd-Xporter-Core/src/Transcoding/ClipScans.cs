using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// The clips' scans (see <see cref="ClipSummary.Scan"/>), made when their disc was read, and what they tell about the
/// clips a source plays.
/// </summary>
public sealed class ClipScans(ProjectDiscs discs)
{
    /// <summary>Each entry's clip scan, or null where its clip couldn't be read through.</summary>
    public List<ClipScan?> Get(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries)
    {
        var cache = discs.Cache(disc);
        return entries.Select(e => cache.GetScan(e.Clip)).ToList();
    }

    /// <summary>
    /// The IDs of the tracks that really have data in these clips, or null if that isn't known: a disc whose navigation
    /// files list streams its clips may not have (a DVD's) has the streams found in each clip scanned.
    /// </summary>
    public IReadOnlyCollection<int>? PresentTracks(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries)
    {
        if (entries.Count == 0)
            return null;
        var scans = Get(disc, entries);
        if (scans.Any(s => s?.StreamIds is null))
            return null;
        return scans.SelectMany(s => s!.StreamIds!).ToHashSet();
    }

    /// <summary>
    /// How many captions each subtitle stream shows across the clips, and how many are forced, by stream ID; null if a
    /// clip couldn't be read through.
    /// </summary>
    public IReadOnlyDictionary<int, SubtitleCount>? SubtitleCounts(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries)
    {
        var scans = Get(disc, entries);
        if (scans.Any(s => s is null))
            return null;

        var counts = new Dictionary<int, SubtitleCount>();
        foreach (var subtitles in scans.SelectMany(s => s!.Subtitles))
        {
            var known = counts.GetValueOrDefault(subtitles.StreamId) ?? new SubtitleCount(0, 0);
            counts[subtitles.StreamId] = new SubtitleCount(known.Total + subtitles.Total, known.Forced + subtitles.Forced);
        }

        return counts;
    }
}
