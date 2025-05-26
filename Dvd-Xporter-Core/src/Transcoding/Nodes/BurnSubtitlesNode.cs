namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Burn In Subtitles node: draws subtitles (by default only the forced ones) into the picture itself.</summary>
public sealed class BurnSubtitlesNode(CanvasNode node)
{
    public const string OnlyForcedKey = "only-forced";

    public const string SubtitlesPort = "subs";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.BurnSubtitles,
        Title = "Burn In Subtitles",
        Category = NodeCategory.Subtitles,
        Description = "Draws subtitles into the picture itself, so they always show and players never have to add them "
            + "while streaming. Put it before any Crop or Scale, so the captions land where they belong.\n\n" + SubtitleHelp.Forced,
        Settings =
        [
            new(OnlyForcedKey, "Only forced captions", SettingKind.Toggle, "true",
                Tooltip: "Burn in just the captions the disc marks forced (e.g. foreign-language lines), not the whole track."),
        ],
        Ports = (_, _) => new NodePorts(
            [new NodePort("in", "Video", PortKind.Video), new NodePort(SubtitlesPort, "Subtitles", PortKind.Subtitle)],
            [new NodePort("out", "Video", PortKind.Video)]),
    };

    /// <summary>Whether only the captions the disc marks forced are burned in.</summary>
    public bool OnlyForced => node.GetBool(OnlyForcedKey, true);

    /// <summary>The filter that lays the subtitles (bitmaps, a picture stream in the filters) over the video while they show.</summary>
    public const string Filter = "overlay=eof_action=pass";
}
