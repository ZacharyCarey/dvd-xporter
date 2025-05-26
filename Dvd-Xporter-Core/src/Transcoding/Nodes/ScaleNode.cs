using System.Globalization;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Scale node: resizes the picture, keeping its shape when one side is left empty.</summary>
public sealed class ScaleNode(CanvasNode node)
{
    public const string WidthKey = "width";
    public const string HeightKey = "height";
    public const string AlgorithmKey = "algorithm";
    public const string SquareKey = "square";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Scale,
        Title = "Scale",
        Category = NodeCategory.VideoFilters,
        Description = "Resizes the picture. Leave one side empty to keep the shape.",
        PassKind = PortKind.Video,
        Settings =
        [
            new(WidthKey, "Width", SettingKind.Integer, Min: 16, Max: 7680, Placeholder: "Keep aspect"),
            new(HeightKey, "Height", SettingKind.Integer, Min: 16, Max: 4320, Placeholder: "Keep aspect"),
            new(AlgorithmKey, "Algorithm", SettingKind.Choice, "lanczos",
                [new("bicubic", "Bicubic"), new("lanczos", "Lanczos"), new("spline", "Spline"), new("bilinear", "Bilinear")]),
            new(SquareKey, "Square pixels", SettingKind.Toggle, "false",
                Tooltip: "Stretch anamorphic video (e.g. a 16:9 DVD) to its display shape instead of keeping the pixel aspect."),
        ],
    };

    /// <summary>The width to scale to; null = kept, or worked out from the height.</summary>
    public int? Width => node.GetInt(WidthKey) is > 0 and var width ? width : null;

    /// <summary>The height to scale to; null = kept, or worked out from the width.</summary>
    public int? Height => node.GetInt(HeightKey) is > 0 and var height ? height : null;

    /// <summary>ffmpeg's scaling algorithm, e.g. "lanczos".</summary>
    public string Algorithm => node.Get(AlgorithmKey, "lanczos");

    /// <summary>Whether anamorphic video is stretched to its display shape (square pixels).</summary>
    public bool SquarePixels => node.GetBool(SquareKey);

    public string Filter
    {
        get
        {
            // Square pixels: stretch anamorphic video out to its display width first
            var square = SquarePixels ? "scale=w='trunc(iw*sar/2)*2':h=ih:flags=" + Algorithm + ",setsar=1," : "";
            var w = Width is not null ? Width.Value.ToString(CultureInfo.InvariantCulture) : Height is not null ? "-2" : "iw";
            var h = Height is not null ? Height.Value.ToString(CultureInfo.InvariantCulture) : Width is not null ? "-2" : "ih";
            var scale = $"{square}scale=w={w}:h={h}:flags={Algorithm}";
            return SquarePixels ? scale + ",setsar=1" : scale;
        }
    }
}
