namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Solid Color node: a plain coloured picture, e.g. black frames to pad between segments.</summary>
public sealed class SolidColorNode(CanvasNode node)
{
    public const string ColorKey = "color";
    public const string WidthKey = "width";
    public const string HeightKey = "height";
    public const string AspectKey = "aspect";
    public const string FrameRateKey = "fps";
    public const string DurationKey = "duration";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.SolidColor,
        Title = "Solid Color",
        Category = NodeCategory.Inputs,
        Description = "A plain coloured picture, e.g. black frames to pad between segments.",
        Settings =
        [
            new(ColorKey, "Color", SettingKind.Color, "#000000"),
            new(WidthKey, "Width", SettingKind.Integer, "720", Min: 16, Max: 7680),
            new(HeightKey, "Height", SettingKind.Integer, "480", Min: 16, Max: 4320),
            new(AspectKey, "Display aspect", SettingKind.Choice, "", [new("", "Square pixels"), new("4:3", "4:3"), new("16:9", "16:9")],
                Tooltip: "Match the video it's joined to: a 720×480 16:9 DVD needs 16:9 here."),
            new(FrameRateKey, "Frame rate", SettingKind.Choice, "30000/1001", NodeTypes.FrameRates),
            new(DurationKey, "Duration", SettingKind.Time, "5"),
        ],
        Ports = (_, _) => new NodePorts([], [new NodePort("out", "Video", PortKind.Video)]),
    };

    /// <summary>The colour, "#rrggbb".</summary>
    public string Color => node.Get(ColorKey, "#000000");

    /// <summary>The picture's width, kept within what encoders take and even.</summary>
    public int Width => Math.Clamp(node.GetInt(WidthKey, 720), 16, 7680) / 2 * 2;

    /// <summary>The picture's height, kept within what encoders take and even.</summary>
    public int Height => Math.Clamp(node.GetInt(HeightKey, 480), 16, 4320) / 2 * 2;

    /// <summary>The display aspect, e.g. "16:9"; empty = square pixels.</summary>
    public string Aspect => node.Get(AspectKey);

    public string FrameRate => node.Get(FrameRateKey, "30000/1001");

    public PreciseTime Duration => node.GetTime(DurationKey) ?? new PreciseTime(0, 0, 5, 0);

    /// <summary>The ffmpeg source filter that makes the picture.</summary>
    public string Graph
    {
        get
        {
            var graph = $"color=c=0x{Color.TrimStart('#')}:s={Width}x{Height}:r={FrameRate}:d={Duration.ToFFmpegTime()}";
            if (Aspect is { Length: > 0 } aspect)
                graph += $",setdar={aspect.Replace(':', '/')}";
            return graph + ",format=yuv420p";
        }
    }
}
