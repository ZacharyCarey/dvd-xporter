using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>
/// Works out each clip's chapter marks (<see cref="ClipInfo.Chapters"/>) from the playlists: a chapter mark is a point
/// in a playlist, and this finds the clip it lands in. The same for every kind of disc.
/// </summary>
internal static class ClipChapters
{
    // Marks this close together are the same chapter
    private static readonly DiscTime SameChapterTolerance = new(DiscTime.TicksPerSecond / 2); // 0.5s

    public static void Fill(DiscInfo info)
    {
        var clips = new Dictionary<string, ClipInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var clip in info.Clips)
            clips.TryAdd(clip.Filename, clip);

        foreach (var playlist in info.Playlists)
        {
            foreach (var chapter in playlist.Chapters)
            {
                var position = DiscTime.Zero;
                foreach (var item in playlist.Clips)
                {
                    var length = item.OutTime - item.InTime;
                    if (chapter.StartTime < position + length)
                    {
                        var clip = clips.GetValueOrDefault(item.Filename);
                        if (chapter.StartTime >= position && clip is not null)
                        {
                            // From the clip's start: where the item starts in it, then how far into the item the mark is
                            var offset = item.InTime - (clip.Start ?? item.InTime) + (chapter.StartTime - position);
                            if (!clip.Chapters.Any(m => m - offset < SameChapterTolerance && offset - m < SameChapterTolerance))
                                clip.Chapters.Add(offset);
                        }

                        break;
                    }

                    position += length;
                }
            }
        }

        foreach (var clip in info.Clips)
            clip.Chapters.Sort();
    }
}
