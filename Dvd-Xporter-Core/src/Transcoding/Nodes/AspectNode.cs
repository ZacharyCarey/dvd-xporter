using FFmpeg;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Set Aspect node: changes how wide the picture displays, without resizing it.</summary>
public sealed class AspectNode(CanvasNode node)
{
    public const string ModeKey = "mode";
    public const string RatioKey = "value";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Aspect,
        Title = "Set Aspect",
        Category = NodeCategory.VideoFilters,
        Description = "Changes how wide the picture displays, without resizing it.",
        PassKind = PortKind.Video,
        Settings =
        [
            new(ModeKey, "Set", SettingKind.Choice, "dar", [new("dar", "Display aspect"), new("sar", "Pixel aspect")]),
            new(RatioKey, "Ratio", SettingKind.Text, "16:9", Placeholder: "e.g. 16:9 or 4:3"),
        ],
    };

    /// <summary>Whether it sets the pixel aspect, rather than the display aspect.</summary>
    public bool SetsPixelAspect => node.Get(ModeKey, "dar") == "sar";

    /// <summary>The ratio, e.g. "16:9".</summary>
    public string Ratio => node.Get(RatioKey, "16:9");

    public string Filter => $"{(SetsPixelAspect ? "setsar" : "setdar")}={FFmpegFilters.EscapeValue(Ratio.Replace(':', '/'))}";
}
