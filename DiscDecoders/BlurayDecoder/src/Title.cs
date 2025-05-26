using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>One clip a title plays: a play item at the title's angle.</summary>
public class TitleClip
{
    public readonly PlayItem PlayItem;

    /// <summary>The clip the title's angle plays (the play item's only clip, unless it's multi-angle).</summary>
    public readonly PlayItemClip Clip;

    /// <summary>The clip's info file, or null if it's missing or couldn't be read.</summary>
    public readonly ClipInformation? Info;

    internal readonly uint StartTime45k;

    /// <summary>The clip's number, e.g. "00012" for 00012.m2ts.</summary>
    public string ClipId => Clip.ClipId;

    /// <summary>Where in the title it starts.</summary>
    public DiscTime StartTime => NavigationFile.ToDiscTime(StartTime45k);

    /// <summary>Where in the clip it starts playing, on the clip's timeline (see <see cref="PresentationStart"/>).</summary>
    public DiscTime InTime => PlayItem.InTime;

    /// <summary>Where in the clip it stops playing.</summary>
    public DiscTime OutTime => PlayItem.OutTime;

    /// <summary>
    /// The length of the whole .m2ts file: the presentation time its clip info covers, or zero without one. The title
    /// may play only part of it (see <see cref="InTime"/> and <see cref="OutTime"/>).
    /// </summary>
    public DiscTime ClipDuration => Info?.Duration ?? DiscTime.Zero;

    /// <summary>The timestamp the .m2ts file's presentation starts at (often 10:00), or null without its clip info.</summary>
    public DiscTime? PresentationStart => Info?.PresentationStart;

    public StreamTable Streams => PlayItem.Streams;

    internal TitleClip(PlayItem playItem, int angle, uint startTime45k, IReadOnlyDictionary<string, ClipInformation> clips)
    {
        PlayItem = playItem;
        Clip = playItem.Clips[angle < playItem.AngleCount ? angle : 0];
        Info = clips.GetValueOrDefault(Clip.ClipId);
        StartTime45k = startTime45k;
    }

    /// <summary>A video stream's aspect ratio, from the clip info (playlists don't record it); null if it doesn't list the stream.</summary>
    public VideoAspect? GetAspect(PlaylistStream stream) => Info?.FindStream(stream.Pid)?.Aspect;
}

/// <summary>A chapter of a title.</summary>
public class TitleChapter
{
    public readonly PlaylistMark Mark;

    /// <summary>Its index among the title's chapters (its number - 1).</summary>
    public readonly int Index;

    internal readonly uint Start45k;
    internal uint Duration45k;

    /// <summary>Where in the title it starts.</summary>
    public DiscTime Start => NavigationFile.ToDiscTime(Start45k);

    /// <summary>How long it lasts: as the disc says, or until the next chapter (or the end of the title).</summary>
    public DiscTime Duration => NavigationFile.ToDiscTime(Duration45k);

    /// <summary>The play item it's in (an index into <see cref="Title.Clips"/>).</summary>
    public int ClipIndex => Mark.PlayItemIndex;

    internal TitleChapter(PlaylistMark mark, int index, uint start45k)
    {
        Mark = mark;
        Index = index;
        Start45k = start45k;
        Duration45k = mark.Duration45k;
    }
}

/// <summary>
/// A playlist played at one angle: the clips it plays in turn, where each starts in the title, and its chapters. See
/// <see cref="Bluray.GetTitle"/>.
/// </summary>
public class Title
{
    public readonly Playlist Playlist;

    /// <summary>The angle it's played at, from 0.</summary>
    public readonly int Angle;

    public readonly ReadOnlyCollection<TitleClip> Clips;

    public readonly ReadOnlyCollection<TitleChapter> Chapters;

    public DiscTime Duration => Playlist.Duration;

    /// <summary>The playlist's number of angles (see <see cref="Playlist.AngleCount"/>).</summary>
    public int AngleCount => Playlist.AngleCount;

    internal Title(Playlist playlist, int angle, IReadOnlyDictionary<string, ClipInformation> clipInfo)
    {
        Playlist = playlist;
        Angle = angle;

        var clips = new List<TitleClip>();
        uint time = 0;
        foreach (var item in playlist.PlayItems)
        {
            clips.Add(new TitleClip(item, angle, time, clipInfo));
            time += item.OutTime45k - item.InTime45k;
        }
        Clips = clips.AsReadOnly();

        // A chapter without a length lasts until the next one; libbluray's rule, kept as it is, only fills one in
        // when the next chapter has no length of its own either
        var chapters = new List<TitleChapter>();
        foreach (var mark in playlist.Marks.Where(m => m.Type == MarkType.Entry))
        {
            var chapter = new TitleChapter(mark, chapters.Count, TitleTime(mark));
            if (chapter.Duration45k == 0 && chapters.LastOrDefault() is { Duration45k: 0 } previous)
                previous.Duration45k = chapter.Start45k - previous.Start45k;
            chapters.Add(chapter);
        }
        if (chapters.LastOrDefault() is { Duration45k: 0 } last)
            last.Duration45k = playlist.Duration45k - last.Start45k;
        Chapters = chapters.AsReadOnly();

        // Where a mark is in the title: its clip's start in the title, plus how far into the clip it is
        uint TitleTime(PlaylistMark mark) => mark.PlayItemIndex < clips.Count
            ? clips[mark.PlayItemIndex].StartTime45k + mark.Time45k - playlist.PlayItems[mark.PlayItemIndex].InTime45k
            : 0;
    }
}
