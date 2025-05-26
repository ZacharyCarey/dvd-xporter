using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using DvdXporter.Core;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A Disc Video or Template Input (see <see cref="SourceNode"/>): its disc and video, what's known about the video
/// (its length and picture), a port for each of its tracks and its chapters, and where it starts and ends.
/// </summary>
public partial class SourceNodeView : TranscodeNodeView
{
    private SettingRow _discRow = null!;
    private SettingRow _itemRow = null!;
    private SettingRow _startRow = null!;
    private SettingRow _endRow = null!;
    private ChoiceControl _disc = null!;
    private ChoiceControl _item = null!;
    private ChoiceControl _startBy = null!;
    private TextControl _start = null!;
    private ChoiceControl _startChapter = null!;
    private ChoiceControl _endBy = null!;
    private TextControl _end = null!;
    private ChoiceControl _endChapter = null!;
    private Label _length = null!;
    private Label _picture = null!;
    private Label _range = null!;
    private Label _chapters = null!;
    // A port row per track, made for the tracks the video has, before the chapters
    private readonly List<Label> _trackRows = [];

    public override void _Ready()
    {
        base._Ready();
        _discRow = GetNode<SettingRow>("%DiscRow");
        _itemRow = GetNode<SettingRow>("%ItemRow");
        _startRow = GetNode<SettingRow>("%StartRow");
        _endRow = GetNode<SettingRow>("%EndRow");
        _disc = GetNode<ChoiceControl>("%Disc");
        _item = GetNode<ChoiceControl>("%Item");
        _startBy = GetNode<ChoiceControl>("%StartBy");
        _start = GetNode<TextControl>("%Start");
        _startChapter = GetNode<ChoiceControl>("%StartChapter");
        _endBy = GetNode<ChoiceControl>("%EndBy");
        _end = GetNode<TextControl>("%End");
        _endChapter = GetNode<ChoiceControl>("%EndChapter");
        _length = GetNode<Label>("%Length");
        _picture = GetNode<Label>("%Picture");
        _range = GetNode<Label>("%Range");
        _chapters = GetNode<Label>("%Chapters");
    }

    public override bool Rebuild(ICanvasContext context, bool force = false)
    {
        var source = new SourceNode(Model);
        var selection = source.Resolve(context);
        var length = LengthText(source, selection, context);

        (string? Text, string? Tooltip) picture = (null, null), range = (null, null);
        var analysis = selection is not null ? context.GetPicture(Model) : null;
        if (analysis is not null)
        {
            picture = ($"Picture: {analysis.Summary}" + (analysis.Advice is not null ? $"\n{analysis.Advice}" : ""), analysis.Explanation);
            var summary = analysis.RangeSummary;
            if (summary is not null)
                range = (summary, analysis.RangeExplanation);
        }

        // Each track's port, named for the track, with how many captions a subtitle track has
        var counts = selection is null ? null : context.GetSubtitleCounts(Model);
        var mostCaptions = counts?.Values.Select(c => c.Total).DefaultIfEmpty(0).Max() ?? 0;
        var outputs = source.Ports(context).Outputs;
        var tracks = outputs.Where(p => p.Key != NodeTypes.ChaptersPort).ToList();
        var trackLabels = (selection?.Tracks ?? []).Select(t =>
        {
            var count = t.Kind == ClipTrackKind.Subtitle ? counts?.GetValueOrDefault(t.StreamId) : null;
            return count is not null ? t.Label + SubtitleHelp.Note(count, mostCaptions) : t.Label;
        }).ToList();
        // Hovering a track's port shows all that's known about it, e.g. its sample rate to match a Silence node to
        var trackDetails = (selection?.Tracks ?? []).Select(t => t.Details).ToList();
        var chaptersPort = outputs.FirstOrDefault(p => p.Key == NodeTypes.ChaptersPort);
        var chapterCount = chaptersPort is null ? 0 : ChapterLists.FromSource(Model, context)?.Chapters.Count ?? 0;
        var chapters = chapterCount == 1 ? "1 chapter" : $"{chapterCount} chapters";

        var signature = new StringBuilder()
            .AppendJoin('|', length.Text, length.Tooltip, picture.Text, picture.Tooltip, range.Text, range.Tooltip, chapters, chaptersPort is not null, source.StartsByChapter, source.EndsByChapter)
            .AppendJoin(',', tracks.Select(t => $"{t.Key}/{t.Kind}"))
            .AppendJoin(',', trackLabels)
            .AppendJoin(',', trackDetails);
        foreach (var key in new[] { SourceNode.DiscKey, SourceNode.ItemKey, SourceNode.StartChapterKey, SourceNode.EndChapterKey })
            signature.Append('\n').AppendJoin(',', NodeTypes.GetChoices(Model, Type.FindSetting(key)!, context).Select(c => $"{c.Value}={c.Label}"));
        if (!StartRebuild(signature.ToString(), force))
            return false;

        ShowSetting(_discRow, SourceNode.DiscKey);
        Bind(_disc, SourceNode.DiscKey, context);
        ShowSetting(_itemRow, SourceNode.ItemKey);
        Bind(_item, SourceNode.ItemKey, context);
        ShowText(_length, length.Text, length.Tooltip);
        ShowText(_picture, picture.Text, picture.Tooltip);
        ShowText(_range, range.Text, range.Tooltip);

        var ports = new Dictionary<Control, (NodePort?, NodePort?)>();
        foreach (var row in _trackRows)
        {
            RemoveChild(row);
            row.QueueFree();
        }

        _trackRows.Clear();
        for (int i = 0; i < tracks.Count; i++)
        {
            var row = Row<Label>("OutputPortRow");
            row.Text = trackLabels.ElementAtOrDefault(i) ?? tracks[i].Label;
            row.TooltipText = trackDetails.ElementAtOrDefault(i) ?? "";
            row.MouseFilter = MouseFilterEnum.Pass;
            AddChild(row);
            MoveChild(row, _chapters.GetIndex());
            _trackRows.Add(row);
            ports[row] = (null, tracks[i]);
        }

        _chapters.Visible = chaptersPort is not null;
        _chapters.Text = chapters;
        if (chaptersPort is not null)
            ports[_chapters] = (null, chaptersPort);

        // Each end of what plays: a time or a chapter, picked on its row
        ShowEnd(_startRow, _startBy, _start, _startChapter, source.StartsByChapter, SourceNode.StartByKey, SourceNode.StartKey, SourceNode.StartChapterKey, context);
        ShowEnd(_endRow, _endBy, _end, _endChapter, source.EndsByChapter, SourceNode.EndByKey, SourceNode.EndKey, SourceNode.EndChapterKey, context);

        FinishRebuild(ports);
        return true;
    }

    /// <summary>How long what plays is ("Length 12:34 of 45:00 · 3 clips"), or why that isn't known.</summary>
    /// <summary>The length played, to the millisecond, with the exact length as its tooltip (e.g. to match a Silence node to).</summary>
    private static (string? Text, string? Tooltip) LengthText(SourceNode source, SourceSelection? selection, ICanvasContext context)
    {
        if (selection?.Duration is not null)
        {
            var duration = selection.Duration.Value;
            var clips = selection.Entries.Count == 1 ? "" : $" · {selection.Entries.Count} clips";
            // The part played, if it's only part
            var (from, to) = source.Range(context);
            var played = (to is not null && to.Value < duration ? to.Value : duration) - (from ?? PreciseTime.Zero);
            var length = played < duration ? $"{Milliseconds(played)} of {Milliseconds(duration)}" : Milliseconds(duration);
            return ($"Length {length}{clips}", $"Exactly {played}, as the disc gives it: type that into a Silence node's Duration to match it.");
        }

        var disc = source.FindDisc(context.Project);
        return (disc is not null && context.GetContents(disc) is null
            ? "The disc hasn't been read yet. It's read once it's found on this computer: use Locate on the project overview."
            : null, null);
    }

    // E.g. "22:07.040", or "1:02:07.040"
    private static string Milliseconds(PreciseTime time)
    {
        var span = time.AsTimeSpan();
        return span.ToString(span.TotalHours >= 1 ? @"h\:mm\:ss\.fff" : @"m\:ss\.fff", CultureInfo.InvariantCulture);
    }

    private void ShowSetting(SettingRow row, string key) => row.Setup(null, Type.FindSetting(key)?.Tooltip, ExposedKeys.Contains(key));

    /// <summary>A start or end's row: how it's picked, then the time or the chapter.</summary>
    private void ShowEnd(SettingRow row, ChoiceControl mode, TextControl time, ChoiceControl chapter, bool byChapter,
        string modeKey, string timeKey, string chapterKey, ICanvasContext context)
    {
        var key = byChapter ? chapterKey : timeKey;
        ShowSetting(row, key);
        Bind(mode, modeKey, context);
        time.Visible = !byChapter;
        chapter.Visible = byChapter;
        Bind(byChapter ? chapter : time, key, context);
    }
}
