using DiscDecoders.Common;
using DvdXporter.Core.Transcoding.Nodes;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <param name="Name">What players show; null until numbered (see <see cref="ChapterLists.DefaultName"/>).</param>
public sealed record Chapter(PreciseTime Start, string? Name);

/// <summary>Chapters flowing along a wire: the chapters, where each clip starts, and how long the whole is.</summary>
public sealed record ChapterSet(IReadOnlyList<Chapter> Chapters, IReadOnlyList<PreciseTime> ClipStarts, PreciseTime? Length);

/// <summary>
/// Works out chapters: a Disc Video's from the disc's chapter marks, and a Chapters node's from its input and
/// settings or the list the user edited. Shared by the canvas (to show them) and the compiler.
/// </summary>
public static class ChapterLists
{
    // Chapters closer together than this are one chapter
    internal static readonly PreciseTime MinimumGap = new(0, 0, 0, 500_000_000);

    /// <summary>A Chapters node's edited list, as lines of "h:mm:ss.fff&lt;tab&gt;name". Empty = follow its input.</summary>
    public const string ListSetting = "list";

    public static string DefaultName(int index) => $"Chapter {index + 1:00}";

    /// <summary>The chapters coming out of a node's output port, or null if it has none (or isn't set up).</summary>
    public static ChapterSet? Resolve(CanvasNode node, string port, ICanvasContext context, int depth = 0)
    {
        if (depth > 16)
            return null;

        if (NodeTypes.IsSource(node.Type) && port == NodeTypes.ChaptersPort)
            return FromSource(node, context);
        if (node.Type != NodeTypes.Chapters || port != "out")
            return null;

        ChapterSet? input = null;
        var link = context.Canvas.LinkInto(node.Id, "in");
        if (link is not null)
        {
            var from = context.Canvas.FindNode(link.FromNode);
            if (from is not null)
                input = Resolve(from, link.FromPort, context, depth + 1);
        }

        var settings = new ChaptersNode(node);
        if (Parse(settings.List) is { Count: > 0 } edited)
            return new ChapterSet(edited, input?.ClipStarts ?? [], input?.Length);

        var chapters = new List<Chapter>(input?.Chapters ?? []);
        if (settings.ClipStarts)
            chapters.AddRange((input?.ClipStarts ?? []).Select(s => new Chapter(s, null)));

        var shift = settings.Shift;
        var length = input?.Length is not null ? input.Length.Value + shift : (PreciseTime?)null;
        return new ChapterSet(Finish(chapters.Select(c => c with { Start = c.Start + shift }), length), input?.ClipStarts ?? [], length);
    }

    /// <summary>
    /// A Disc Video's chapters: the disc's chapter marks in each clip it plays, placed on its own timeline (after
    /// its start and end trims, or within its chapter range), numbered in order from the start of what it plays.
    /// </summary>
    public static ChapterSet? FromSource(CanvasNode node, ITranscodeContext context)
    {
        if (AllOfSource(node, context) is not var (numbered, clipStarts, position))
            return null;

        // The source's own trim: by times, or by chapters
        var (from, to) = new SourceNode(node).Range(context);
        var start = from ?? PreciseTime.Zero;
        var end = to is not null && to.Value < position ? to.Value : position;
        // Disc chapters have no names of their own, so a cut's are numbered from its start
        return new ChapterSet(
            Trim(numbered, start, end).Select((c, i) => c with { Name = DefaultName(i) }).ToList(),
            clipStarts.Where(s => s >= start && s < end).Select(s => s - start).ToList(),
            end - start);
    }

    /// <summary>All of a Disc Video's chapters (numbered, before any trim), where its clips start, and its length.</summary>
    public static (List<Chapter> Chapters, List<PreciseTime> ClipStarts, PreciseTime Length)? AllOfSource(CanvasNode node, ITranscodeContext context)
    {
        var selection = new SourceNode(node).Resolve(context);
        if (selection is null)
            return null;
        var contents = context.GetContents(selection.Disc);
        if (contents is null)
            return null;

        var chapters = new List<Chapter>();
        var clipStarts = new List<PreciseTime>();
        var position = DiscTime.Zero;
        foreach (var entry in selection.Entries)
        {
            var clip = contents.FindClip(entry.Clip);
            var clipStart = clip?.Start ?? DiscTime.Zero;
            // Where the entry starts and how long it plays, measured from the clip's start
            var from = entry.InTime is not null ? entry.InTime.Value - clipStart : DiscTime.Zero;
            var length = entry.TrimmedDuration ?? (clip is not null ? clip.Duration - from : DiscTime.Zero);

            clipStarts.Add(position.ToPreciseTime());
            foreach (var mark in clip?.Chapters ?? [])
            {
                if (mark >= from && mark - from < length)
                    chapters.Add(new Chapter((position + (mark - from)).ToPreciseTime(), null));
            }

            position += length;
        }

        return (Finish(chapters, position.ToPreciseTime()), clipStarts, position.ToPreciseTime());
    }

    /// <summary>
    /// Sorts chapters, merges ones too close together, makes sure one starts at 0, drops any past the end, and
    /// numbers the unnamed ones by their place.
    /// </summary>
    public static List<Chapter> Finish(IEnumerable<Chapter> chapters, PreciseTime? length)
    {
        var sorted = new List<Chapter>();
        foreach (var chapter in chapters.Where(c => c.Start >= PreciseTime.Zero && (length is null || c.Start < length.Value)).OrderBy(c => c.Start))
        {
            if (sorted.Count > 0 && chapter.Start - sorted[^1].Start < MinimumGap)
            {
                // Keep the named one of two that coincide
                if (sorted[^1].Name is null && chapter.Name is not null)
                    sorted[^1] = sorted[^1] with { Name = chapter.Name };
                continue;
            }

            sorted.Add(chapter);
        }

        if (sorted.Count > 0 && sorted[0].Start >= MinimumGap)
            sorted.Insert(0, new Chapter(PreciseTime.Zero, null));
        else if (sorted.Count > 0)
            sorted[0] = sorted[0] with { Start = PreciseTime.Zero };

        return sorted.Select((c, i) => c.Name is null ? c with { Name = DefaultName(i) } : c).ToList();
    }

    /// <summary>
    /// The chapters within [<paramref name="start"/>, <paramref name="end"/>), moved so <paramref name="start"/> is
    /// 0. The chapter playing at the start keeps going from 0 under its own name.
    /// </summary>
    public static List<Chapter> Trim(IReadOnlyList<Chapter> chapters, PreciseTime start, PreciseTime? end) =>
        FFmpegCommand.TrimChapters(ToFFmpeg(chapters), start.ToFFmpegTime(), end.ToFFmpegTime(), MinimumGap.ToFFmpegTime())
            .Select(c => new Chapter(c.Start.ToPreciseTime(), c.Title))
            .ToList();

    /// <summary>The chapters as ffmpeg takes them.</summary>
    public static List<FFmpegChapter> ToFFmpeg(IEnumerable<Chapter> chapters) =>
        chapters.Select(c => new FFmpegChapter(c.Start.ToFFmpegTime(), c.Name)).ToList();

    // ---- The edited list ----

    public static string Format(PreciseTime time)
    {
        var span = time.AsTimeSpan();
        return $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}.{span.Milliseconds:000}";
    }

    public static string Serialize(IEnumerable<Chapter> chapters) =>
        string.Join("\n", chapters.Select(c => $"{Format(c.Start)}\t{(c.Name ?? "").Replace('\t', ' ').Replace('\n', ' ')}"));

    public static List<Chapter> Parse(string text)
    {
        var chapters = new List<Chapter>();
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var tab = line.IndexOf('\t');
            var time = tab >= 0 ? line[..tab] : line;
            if (PreciseTime.TryParse(time.Trim(), out var start))
                chapters.Add(new Chapter(start, tab >= 0 ? line[(tab + 1)..] : null));
        }

        return chapters.OrderBy(c => c.Start).ToList();
    }
}
