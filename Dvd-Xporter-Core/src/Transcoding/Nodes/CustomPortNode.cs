namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Custom Input or Custom Output node, inside a custom node's canvas: one of the custom node's inputs (what's connected
/// to it comes out here) or outputs (what's connected here comes out of it).
/// </summary>
public sealed class CustomPortNode(CanvasNode node)
{
    public const string NameKey = "name";
    public const string KindKey = "kind";

    public static readonly IReadOnlyList<SettingChoice> Kinds =
    [
        new("video", "Video"),
        new("audio", "Audio"),
        new("subtitle", "Subtitles"),
        new("chapters", "Chapters"),
    ];

    public static readonly NodeType InputType = new()
    {
        Id = NodeTypes.CustomInput,
        Title = "Custom Input",
        Category = NodeCategory.Custom,
        Description = "One of a custom node's inputs: what's connected to it on the custom node comes out here.",
        Settings =
        [
            new(NameKey, "Name", SettingKind.Text, "Input"),
            new(KindKey, "Kind", SettingKind.Choice, "video", Kinds),
        ],
        Ports = (n, _) => new NodePorts([], [new NodePort("out", new CustomPortNode(n).Name, new CustomPortNode(n).Kind)]),
        AllowedIn = kind => kind == CanvasKind.CustomNode,
    };

    public static readonly NodeType OutputType = new()
    {
        Id = NodeTypes.CustomOutput,
        Title = "Custom Output",
        Category = NodeCategory.Custom,
        Description = "One of a custom node's outputs: what's connected here comes out of it on the custom node.",
        Settings =
        [
            new(NameKey, "Name", SettingKind.Text, "Output"),
            new(KindKey, "Kind", SettingKind.Choice, "video", Kinds),
        ],
        Ports = (n, _) => new NodePorts([new NodePort("in", new CustomPortNode(n).Name, new CustomPortNode(n).Kind)], []),
        AllowedIn = kind => kind == CanvasKind.CustomNode,
    };

    /// <summary>The port's name on the custom node.</summary>
    public string Name => node.Get(NameKey, node.Type == NodeTypes.CustomOutput ? "Output" : "Input");

    /// <summary>What the port carries.</summary>
    public PortKind Kind => node.Get(KindKey, "video") switch
    {
        "audio" => PortKind.Audio,
        "subtitle" => PortKind.Subtitle,
        "chapters" => PortKind.Chapters,
        _ => PortKind.Video,
    };
}
