namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Flip node: mirrors the picture side to side, top to bottom, or both.</summary>
public sealed class FlipNode(CanvasNode node)
{
    public const string HorizontalKey = "horizontal";
    public const string VerticalKey = "vertical";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Flip,
        Title = "Flip",
        Category = NodeCategory.VideoFilters,
        PassKind = PortKind.Video,
        Settings =
        [
            new(HorizontalKey, "Horizontal", SettingKind.Toggle, "true"),
            new(VerticalKey, "Vertical", SettingKind.Toggle, "false"),
        ],
    };

    public bool Horizontal => node.GetBool(HorizontalKey, true);

    public bool Vertical => node.GetBool(VerticalKey);

    public string Filter
    {
        get
        {
            var parts = new List<string>();
            if (Horizontal)
                parts.Add("hflip");
            if (Vertical)
                parts.Add("vflip");
            return parts.Count > 0 ? string.Join(",", parts) : "null";
        }
    }
}
