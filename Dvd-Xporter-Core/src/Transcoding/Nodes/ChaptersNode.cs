namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Chapters node: chapters for an output file, from its input (a Disc Video's) with clip starts added and shifted,
/// or the list the user edited (see <see cref="ChapterLists"/>).
/// </summary>
public sealed class ChaptersNode(CanvasNode node)
{
    public const string ClipStartsKey = "clip-starts";
    public const string ShiftKey = "shift";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Chapters,
        Title = "Chapters",
        Category = NodeCategory.Combine,
        Description = "Chapters for an output file: the disc's own, or your own list. Connect a Disc Video's chapters to start from the disc's.",
        Settings =
        [
            new(ClipStartsKey, "Chapter at each clip", SettingKind.Toggle, "false",
                Visible: n => !new ChaptersNode(n).IsEdited,
                Tooltip: "Also start a chapter where each video file of a playlist starts."),
            new(ShiftKey, "Shift (s)", SettingKind.Number, "0", Min: -36_000, Max: 36_000,
                Visible: n => !new ChaptersNode(n).IsEdited,
                Tooltip: "Moves every chapter later (or earlier, if negative), e.g. by the length of something joined before the video."),
            new(ChapterLists.ListSetting, "Chapters", SettingKind.ChapterList),
        ],
        Ports = (_, _) => new NodePorts(
            [new NodePort("in", "From video", PortKind.Chapters)],
            [new NodePort("out", "Chapters", PortKind.Chapters)]),
    };

    /// <summary>Whether a chapter also starts where each clip of a playlist starts.</summary>
    public bool ClipStarts => node.GetBool(ClipStartsKey);

    /// <summary>How much later every chapter goes (earlier, if negative).</summary>
    public PreciseTime Shift => PreciseTime.FromTimespan(TimeSpan.FromSeconds(node.GetDouble(ShiftKey)));

    /// <summary>The list as the user edited it, as lines of "h:mm:ss.fff&lt;tab&gt;name"; empty = follow the input.</summary>
    public string List
    {
        get => node.Get(ChapterLists.ListSetting);
        set => node.Set(ChapterLists.ListSetting, value);
    }

    /// <summary>Whether the user edited the list, which then stands instead of the input.</summary>
    public bool IsEdited => List.Length > 0;
}
