namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Custom Video Filter or Custom Audio Filter node: any ffmpeg filter chain with one input and one output.</summary>
public sealed class CustomFilterNode(CanvasNode node)
{
    public const string FilterKey = "filter";

    public static readonly NodeType VideoType = new()
    {
        Id = NodeTypes.CustomVideo,
        Title = "Custom Video Filter",
        Category = NodeCategory.VideoFilters,
        Description = "Any ffmpeg video filter chain with one input and one output.",
        PassKind = PortKind.Video,
        Settings = [new(FilterKey, "Filter", SettingKind.Text, Placeholder: "e.g. hqdn3d=2:1:2:3")],
    };

    public static readonly NodeType AudioType = new()
    {
        Id = NodeTypes.CustomAudio,
        Title = "Custom Audio Filter",
        Category = NodeCategory.AudioFilters,
        Description = "Any ffmpeg audio filter chain with one input and one output.",
        PassKind = PortKind.Audio,
        Settings = [new(FilterKey, "Filter", SettingKind.Text, Placeholder: "e.g. loudnorm")],
    };

    /// <summary>The filter chain as typed; null if there's none.</summary>
    public string? Filter => node.Get(FilterKey).Trim() is { Length: > 0 } filter ? filter : null;
}
