using FFmpeg;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Draw Text node: writes text over the picture, throughout or for part of it.</summary>
public sealed class DrawTextNode(CanvasNode node)
{
    public const string TextKey = "text";
    public const string SizeKey = "size";
    public const string ColorKey = "color";
    public const string XKey = "x";
    public const string YKey = "y";
    public const string StartKey = "start";
    public const string EndKey = "end";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.DrawText,
        Title = "Draw Text",
        Category = NodeCategory.VideoFilters,
        Description = "Writes text over the picture.",
        PassKind = PortKind.Video,
        Settings =
        [
            new(TextKey, "Text", SettingKind.Text),
            new(SizeKey, "Size", SettingKind.Integer, "32", Min: 4, Max: 1000),
            new(ColorKey, "Color", SettingKind.Color, "#ffffff"),
            new(XKey, "X", SettingKind.Text, "(w-text_w)/2", Tooltip: "Pixels, or an ffmpeg expression (w, h, text_w, text_h)."),
            new(YKey, "Y", SettingKind.Text, "(h-text_h)/2"),
            new(StartKey, "From", SettingKind.Time, Placeholder: "Beginning"),
            new(EndKey, "Until", SettingKind.Time, Placeholder: "End"),
        ],
    };

    public string Text => node.Get(TextKey);

    /// <summary>The font size, at least 4.</summary>
    public int Size => Math.Max(4, node.GetInt(SizeKey, 32));

    /// <summary>The colour, "#rrggbb".</summary>
    public string Color => node.Get(ColorKey, "#ffffff");

    /// <summary>Where the text goes from the left: pixels, or an ffmpeg expression.</summary>
    public string X => node.Get(XKey, "(w-text_w)/2");

    /// <summary>Where the text goes from the top: pixels, or an ffmpeg expression.</summary>
    public string Y => node.Get(YKey, "(h-text_h)/2");

    /// <summary>When it starts showing; null = from the beginning.</summary>
    public PreciseTime? Start => node.GetTime(StartKey);

    /// <summary>When it stops showing; null = until the end.</summary>
    public PreciseTime? End => node.GetTime(EndKey);

    public string Filter =>
        $"drawtext=expansion=none:text={FFmpegFilters.EscapeValue(Text)}:fontsize={Size}"
        + $":fontcolor=0x{Color.TrimStart('#')}:x={FFmpegFilters.EscapeValue(X)}:y={FFmpegFilters.EscapeValue(Y)}"
        + FilterTiming.EnableOption(Start, End);
}
