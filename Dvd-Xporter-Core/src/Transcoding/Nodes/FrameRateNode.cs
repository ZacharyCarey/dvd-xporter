namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Frame Rate node: drops or repeats frames to reach a frame rate.</summary>
public sealed class FrameRateNode(CanvasNode node)
{
    public const string FrameRateKey = "fps";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.FrameRate,
        Title = "Frame Rate",
        Category = NodeCategory.VideoFilters,
        Description = "Drops or repeats frames to reach a frame rate.",
        PassKind = PortKind.Video,
        Settings = [new(FrameRateKey, "Frame rate", SettingKind.Choice, "24000/1001", NodeTypes.FrameRates)],
    };

    /// <summary>The frame rate, e.g. "24000/1001".</summary>
    public string FrameRate => node.Get(FrameRateKey, "24000/1001");

    public string Filter => $"fps={FrameRate}";
}
