using System.Globalization;
using System.Text;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Subtitles;

namespace DvdXporter.Core.Transcoding;

/// <summary>A source's subtitle text as an SRT file (see <see cref="SubtitleTextFiles"/>).</summary>
/// <param name="Path">The file, or null if there's none: a clip's text hasn't been read, or there's no text.</param>
/// <param name="Unread">The source's clips whose text hasn't been read yet.</param>
public sealed record SubtitleTextFile(string? Path, IReadOnlyList<string> Unread);

/// <summary>
/// SRT files made from subtitle text read off a track's pictures and corrected (see <see cref="SubtitleTexts"/>): each
/// clip's lines moved to the source's timeline, as <see cref="ForcedSubtitleFiles"/> does with captions, and kept in
/// the disc's generated folder.
/// </summary>
public sealed class SubtitleTextFiles(ProjectFolder folder, ProjectDiscs discs, ClipScans scans)
{
    /// <summary>The track's text as an SRT timed from the entries' start; only its forced captions if <paramref name="forcedOnly"/>.</summary>
    public SubtitleTextFile Get(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries, ClipTrack track, bool forcedOnly)
    {
        var texts = entries.Select(e => e.Clip).Distinct().ToDictionary(c => c, c => SubtitleTexts.Load(folder, disc, c, track.StreamId));
        var unread = texts.Where(t => t.Value is null).Select(t => t.Key).ToList();
        if (unread.Count > 0 || entries.Count == 0)
            return new SubtitleTextFile(null, unread);

        // Each line at its place on the source's timeline: the entries before it, then its time in its entry
        var contents = discs.GetContents(disc);
        var clipScans = scans.Get(disc, entries);
        var srt = new StringBuilder();
        int number = 0;
        var offset = DiscTime.Zero;
        for (int i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var scan = clipScans[i];
            var clip = contents?.FindClip(entry.Clip);
            // Where the entry starts on the clip's timeline, and how long it plays
            var from = entry.InTime ?? scan?.Start ?? clip?.Start ?? DiscTime.Zero;
            var length = entry.TrimmedDuration ?? scan?.Duration ?? clip?.Duration ?? DiscTime.Zero;
            foreach (var line in texts[entry.Clip]!.Lines)
            {
                var start = line.Start - from;
                if (start < DiscTime.Zero || start >= length || string.IsNullOrWhiteSpace(line.Text) || (forcedOnly && !line.Forced))
                    continue;
                var end = line.End - from < length ? line.End - from : length;
                srt.Append(CultureInfo.InvariantCulture, $"{++number}\n{Time(offset + start)} --> {Time(offset + end)}\n{line.Text.Trim()}\n\n");
            }

            offset += length;
        }

        if (number == 0)
            return new SubtitleTextFile(null, []);

        // Named by what's in it, so an edit makes a new file and a file already made is used as it is
        var text = srt.ToString();
        var key = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();
        var cache = discs.Cache(disc);
        var path = Path.Combine(cache.GeneratedFolder, $"text-{key}.srt");
        if (!File.Exists(path))
        {
            Directory.CreateDirectory(cache.GeneratedFolder);
            File.WriteAllText(path, text);
        }

        return new SubtitleTextFile(path, []);
    }

    // SRT's "00:01:02,345"
    private static string Time(DiscTime time)
    {
        var span = time.ToPreciseTime().AsTimeSpan();
        return string.Create(CultureInfo.InvariantCulture, $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00},{span.Milliseconds:000}");
    }
}
