using DvdXporter.Core.Emby;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>One video a template is used with, and the library item its outputs go to.</summary>
/// <param name="Disc">The disc's fingerprint.</param>
/// <param name="Item">The video: "clip:..." or "playlist:...", as a Disc Video's item.</param>
/// <param name="Target">The library item: "movie" or "episode:season:number", as an Export's target.</param>
/// <param name="Chapters">The chapters of the video to play, "first-last" (1-based, inclusive); empty = as the template plays it.</param>
/// <param name="Version">The version its files are, e.g. "Extended", before any the template's Export nodes give; empty = none.</param>
public sealed record TemplateRow(string Disc, string Item, string Target, string Chapters = "", string Version = "")
{
    /// <summary>The row's chapter range, or null if it plays the video as the template does.</summary>
    public (int First, int Last)? ChapterRange =>
        Chapters.Split('-') is [var a, var b] && int.TryParse(a, out var first) && int.TryParse(b, out var last) && first >= 1 && last >= first
            ? (first, last) : null;

    /// <summary>Points a template's stand-in (or the Disc Video it becomes) at this row's video.</summary>
    public void ApplyTo(CanvasNode node)
    {
        var source = new SourceNode(node) { Disc = Disc, Item = Item };
        if (ChapterRange is var (first, last))
        {
            source.StartsByChapter = true;
            source.StartChapter = first;
            source.EndsByChapter = true;
            source.EndChapter = last;
        }
    }

    /// <summary>
    /// Points a template's Export node at this row's library item, with the row's version before the Export's own,
    /// e.g. "Extended 720p".
    /// </summary>
    public void ApplyToExport(CanvasNode node)
    {
        var export = new ExportNode(node) { Target = Target };
        export.Version = string.Join(' ', new[] { Version.Trim(), export.Version }.Where(v => v.Length > 0));
    }
}

/// <summary>
/// A Template node's rows, kept as its "rows" setting: one
/// "disc&lt;tab&gt;item&lt;tab&gt;target[&lt;tab&gt;chapters[&lt;tab&gt;version]]" per line.
/// </summary>
public static class TemplateRows
{
    public const string Setting = "rows";

    public static List<TemplateRow> Parse(string text) => text
        .Split('\n', StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Split('\t'))
        .Where(parts => parts.Length is >= 3 and <= 5)
        .Select(parts => new TemplateRow(parts[0], parts[1], parts[2], parts.ElementAtOrDefault(3) ?? "", parts.ElementAtOrDefault(4) ?? ""))
        .ToList();

    public static string Serialize(IEnumerable<TemplateRow> rows) => string.Join("\n", rows.Select(r =>
    {
        // A version on one line, with no tabs to split it
        var version = string.Join(' ', r.Version.Split(['\t', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        var chapters = r.ChapterRange is not null ? r.Chapters : "";
        return version.Length > 0 ? $"{r.Disc}\t{r.Item}\t{r.Target}\t{chapters}\t{version}"
            : chapters.Length > 0 ? $"{r.Disc}\t{r.Item}\t{r.Target}\t{chapters}"
            : $"{r.Disc}\t{r.Item}\t{r.Target}";
    }));

    /// <summary>A row as one line, e.g. "Disc 1 › Playlist: Episode 3 → S01E03 – Bushwhacked".</summary>
    public static string Describe(TemplateRow row, ITranscodeContext context) => $"{DescribeInput(row, context)}  →  {DescribeTarget(row, context.Project)}";

    public static string DescribeInput(TemplateRow row, ITranscodeContext context)
    {
        var disc = NodeTypes.FindDisc(context.Project, row.Disc);
        if (disc is null)
            return "(missing disc)";
        var probe = SourceNode.Create(row.Disc);
        var item = NodeTypes.SourceItemChoices(probe, context).Find(c => c.Value == row.Item)?.Label ?? "(no video picked)";
        var chapters = row.ChapterRange is var (first, last) ? first == last ? $" · chapter {first}" : $" · chapters {first}–{last}" : "";
        return $"{disc.Label} › {item}{chapters}";
    }

    /// <summary>The row's library item, with its version if it has one, e.g. "S01E03 – Bushwhacked (Extended)".</summary>
    public static string DescribeTarget(TemplateRow row, Project project)
    {
        if (string.IsNullOrEmpty(row.Target))
            return "(no library item)";
        var item = LibraryNaming.Describe(ExportNode.Create(row.Target), project);
        // Extras have no versions
        var version = row.Version.Trim();
        return version.Length > 0 && !NodeTypes.IsExtraTarget(row.Target) ? $"{item} ({version})" : item;
    }
}
