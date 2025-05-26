using System.Globalization;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Crop node: cuts the picture down to a size, placed where it's set or else centred.</summary>
public sealed class CropNode(CanvasNode node)
{
    public const string WidthKey = "width";
    public const string HeightKey = "height";
    public const string LeftKey = "x";
    public const string TopKey = "y";

    /// <summary>The detect button's action key (see <see cref="SettingKind.Action"/>).</summary>
    public const string DetectAction = "detect";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Crop,
        Title = "Crop",
        Category = NodeCategory.VideoFilters,
        Description = "Cuts the picture down, e.g. to remove black bars.",
        PassKind = PortKind.Video,
        Settings =
        [
            new(DetectAction, "Detect Black Bars", SettingKind.Action,
                Tooltip: "Measures the black bars in samples across the video connected here, and sets the crop to remove them."),
            new(WidthKey, "Width", SettingKind.Integer, Min: 1, Max: 7680, Placeholder: "Full width"),
            new(HeightKey, "Height", SettingKind.Integer, Min: 1, Max: 4320, Placeholder: "Full height"),
            new(LeftKey, "Left", SettingKind.Integer, Max: 7680, Placeholder: "Centred"),
            new(TopKey, "Top", SettingKind.Integer, Max: 4320, Placeholder: "Centred"),
        ],
    };

    /// <summary>How wide the picture is cropped to; null = its full width.</summary>
    public int? Width
    {
        get => node.GetInt(WidthKey) is > 0 and var width ? width : null;
        set => Set(WidthKey, value);
    }

    /// <summary>How high the picture is cropped to; null = its full height.</summary>
    public int? Height
    {
        get => node.GetInt(HeightKey) is > 0 and var height ? height : null;
        set => Set(HeightKey, value);
    }

    /// <summary>Where the crop starts from the picture's left edge; null = centred.</summary>
    public int? Left
    {
        get => node.Get(LeftKey).Length > 0 ? node.GetInt(LeftKey) : null;
        set => Set(LeftKey, value);
    }

    /// <summary>Where the crop starts from the picture's top edge; null = centred.</summary>
    public int? Top
    {
        get => node.Get(TopKey).Length > 0 ? node.GetInt(TopKey) : null;
        set => Set(TopKey, value);
    }

    /// <summary>The ffmpeg filter that crops the picture.</summary>
    public string Filter
    {
        get
        {
            var filter = $"crop=w={Format(Width) ?? "iw"}:h={Format(Height) ?? "ih"}";
            var left = Left;
            if (left is not null)
                filter += $":x={Format(left.Value)}";
            var top = Top;
            if (top is not null)
                filter += $":y={Format(top.Value)}";
            return filter;
        }
    }

    private static string? Format(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    private void Set(string key, int? value) => node.Set(key, Format(value));
}
