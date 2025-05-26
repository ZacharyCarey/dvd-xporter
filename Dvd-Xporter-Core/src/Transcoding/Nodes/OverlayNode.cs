using FFmpeg;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>An Overlay node: puts one picture on top of another, throughout or for part of it.</summary>
public sealed class OverlayNode(CanvasNode node)
{
    public const string XKey = "x";
    public const string YKey = "y";
    public const string StartKey = "start";
    public const string EndKey = "end";

    public const string MainPort = "main";
    public const string OverlayPort = "overlay";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Overlay,
        Title = "Overlay",
        Category = NodeCategory.VideoFilters,
        Description = "Puts one picture on top of another.",
        Settings =
        [
            new(XKey, "X", SettingKind.Text, "0", Tooltip: "Pixels, or an ffmpeg expression (W, H = main size; w, h = overlay size)."),
            new(YKey, "Y", SettingKind.Text, "0"),
            new(StartKey, "From", SettingKind.Time, Placeholder: "Beginning"),
            new(EndKey, "Until", SettingKind.Time, Placeholder: "End"),
        ],
        Ports = (_, _) => new NodePorts(
            [new NodePort(MainPort, "Main", PortKind.Video), new NodePort(OverlayPort, "Overlay", PortKind.Video)],
            [new NodePort("out", "Video", PortKind.Video)]),
    };

    /// <summary>Where the overlay goes from the left: pixels, or an ffmpeg expression.</summary>
    public string X => node.Get(XKey, "0");

    /// <summary>Where the overlay goes from the top: pixels, or an ffmpeg expression.</summary>
    public string Y => node.Get(YKey, "0");

    public PreciseTime? Start => node.GetTime(StartKey);

    public PreciseTime? End => node.GetTime(EndKey);

    public string Filter => $"overlay=x={FFmpegFilters.EscapeValue(X)}:y={FFmpegFilters.EscapeValue(Y)}" + FilterTiming.EnableOption(Start, End);
}
