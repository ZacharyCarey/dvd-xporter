using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// Files holding just a subtitle track's forced captions (see <see cref="ForcedSubtitleWriter"/>), made from the clips'
/// scans (the captions were found and saved when the disc was read, so the disc itself isn't needed) and kept in the
/// disc's generated folder.
/// </summary>
public sealed class ForcedSubtitleFiles(ProjectDiscs discs, ClipScans scans)
{
    /// <summary>
    /// A file holding just the track's forced captions, timed from the entries' start, or null if it has none (or a
    /// clip couldn't be read through, so it isn't known).
    /// </summary>
    public string? Get(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, ClipTrack track)
    {
        if (entries.Count == 0)
            return null;

        var clipScans = scans.Get(disc, entries);
        if (clipScans.Any(s => s is null))
            return null;

        // Each caption at its place on the source's timeline: the entries before it, then its time in its entry
        var contents = discs.GetContents(disc);
        var captions = new List<(DiscTime Time, byte[] Data)>();
        var offset = DiscTime.Zero;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var scan = clipScans[i]!;
            var clip = contents?.FindClip(entry.Clip);
            // Where the entry starts on the clip's timeline, and how long it plays
            var from = entry.InTime ?? scan.Start ?? clip?.Start ?? DiscTime.Zero;
            var length = entry.TrimmedDuration ?? scan.Duration ?? clip?.Duration ?? DiscTime.Zero;
            foreach (var caption in scan.Subtitles.FirstOrDefault(s => s.StreamId == track.StreamId)?.ForcedCaptions ?? [])
            {
                var time = caption.Time - from;
                if (time >= DiscTime.Zero && time < length)
                    captions.Add((offset + time, caption.Data));
            }

            offset += length;
        }

        if (captions.Count == 0)
            return null;

        var cache = discs.Cache(disc);
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"forced/{track.StreamId}/" + string.Join("+", entries.Select(e => $"{e.Clip}@{e.InTime}-{e.OutTime}")))))[..16].ToLowerInvariant();
        var basePath = Path.Combine(cache.GeneratedFolder, $"forced-{key}");
        switch (track.Codec)
        {
            case nameof(SubtitleCodec.dvd_subtitle):
                if (!File.Exists(basePath + ".idx"))
                {
                    Directory.CreateDirectory(cache.GeneratedFolder);
                    var height = contents?.FindClip(entries[0].Clip)?.Tracks?.FirstOrDefault(t => t.Kind == ClipTrackKind.Video)?.Label
                        .StartsWith("576", StringComparison.Ordinal) == true ? 576 : 480;
                    ForcedSubtitleWriter.WriteVobSub(basePath, 720, height, discs.SubtitlePalette(disc, entries[0].Clip), track.Language,
                        captions.Select(c => (c.Time.TotalSeconds, c.Data)));
                }

                return basePath + ".idx";
            case nameof(SubtitleCodec.hdmv_pgs_subtitle):
                if (!File.Exists(basePath + ".sup"))
                {
                    Directory.CreateDirectory(cache.GeneratedFolder);
                    ForcedSubtitleWriter.WriteSup(basePath + ".sup", captions);
                }

                return basePath + ".sup";
            default:
                return null;
        }
    }
}
