namespace DvdXporter.Core.Transcoding;

/// <summary>What flows along a wire.</summary>
public enum PortKind
{
    Video,
    Audio,
    Subtitle,
    /// <summary>A finished file (an output file, or later a remux of one).</summary>
    File,
    /// <summary>An input that takes any video, audio or subtitle stream (an output file's tracks).</summary>
    Stream,
    /// <summary>A list of chapters.</summary>
    Chapters,
}

public enum NodeCategory
{
    Inputs,
    VideoFilters,
    AudioFilters,
    Combine,
    Subtitles,
    Codecs,
    Outputs,
    /// <summary>Templates, custom nodes, and the nodes that build them.</summary>
    Custom,
}

public enum SettingKind
{
    Text,
    Integer,
    Number,
    Choice,
    Toggle,
    /// <summary>A time as "h:mm:ss.fff", "90" or "1m 30s" (see <see cref="PreciseTime.TryParse(string?, out PreciseTime)"/>).</summary>
    Time,
    /// <summary>An HTML colour, "#rrggbb".</summary>
    Color,
    /// <summary>A track's title, language and flags in an output file, edited as a group under the setting's key.</summary>
    TrackOptions,
    /// <summary>A Chapters node's list, edited in a dialog.</summary>
    ChapterList,
    /// <summary>A Template node's videos and library items (see <see cref="TemplateRows"/>).</summary>
    TemplateRows,
    /// <summary>A button that does something for the node, e.g. detecting a crop; holds no value.</summary>
    Action,
}

public sealed record SettingChoice(string Value, string Label);

/// <param name="Default">The value used when the setting is empty.</param>
/// <param name="Placeholder">Shown in an empty text field instead of the default, e.g. "Auto".</param>
/// <param name="Visible">Whether to show it, given the node's other settings (null = always).</param>
/// <param name="Stepper">For an <see cref="SettingKind.Integer"/> that always has a value: shown with up/down arrows.</param>
/// <param name="ShownIn">Which kinds of canvas show it (null = any).</param>
public sealed record NodeSetting(
    string Key,
    string Label,
    SettingKind Kind,
    string Default = "",
    IReadOnlyList<SettingChoice>? Choices = null,
    string? Placeholder = null,
    double Min = 0,
    double Max = 100_000,
    Func<CanvasNode, bool>? Visible = null,
    string? Tooltip = null,
    bool Stepper = false,
    Func<CanvasKind, bool>? ShownIn = null)
{
    /// <summary>Whether a node on a canvas of <paramref name="canvas"/>'s kind shows it.</summary>
    public bool IsShown(CanvasNode node, CanvasKind canvas) => (ShownIn?.Invoke(canvas) ?? true) && (Visible?.Invoke(node) ?? true);
}

public sealed record NodePort(string Key, string Label, PortKind Kind);

/// <summary>A node's inputs and outputs, each in the order it shows them.</summary>
public sealed record NodePorts(IReadOnlyList<NodePort> Inputs, IReadOnlyList<NodePort> Outputs);

public sealed class NodeType
{
    public required string Id { get; init; }

    public required string Title { get; init; }

    public required NodeCategory Category { get; init; }

    public string Description { get; init; } = "";

    public IReadOnlyList<NodeSetting> Settings { get; init; } = [];

    /// <summary>The node's ports, given its settings and what it plays; when null, an "in" and an "out" of <see cref="PassKind"/>.</summary>
    public Func<CanvasNode, ITranscodeContext, NodePorts>? Ports { get; init; }

    /// <summary>For a one-in, one-out filter or codec: what it takes and gives.</summary>
    public PortKind? PassKind { get; init; }

    /// <summary>Which kinds of canvas the node can go on (null = any).</summary>
    public Func<CanvasKind, bool>? AllowedIn { get; init; }

    public bool IsAllowedIn(CanvasKind kind) => AllowedIn?.Invoke(kind) ?? true;

    /// <summary>What the node takes and gives, given its settings and what it plays.</summary>
    public NodePorts GetPorts(CanvasNode node, ITranscodeContext context)
    {
        if (Ports is not null)
            return Ports(node, context);
        if (PassKind is not null)
        {
            var kind = PassKind.Value;
            return new NodePorts([new NodePort("in", KindName(kind), kind)], [new NodePort("out", KindName(kind), kind)]);
        }

        return new NodePorts([], []);
    }

    public NodeSetting? FindSetting(string key) => Settings.FirstOrDefault(s => s.Key == key);

    internal static string KindName(PortKind kind) => kind switch
    {
        PortKind.Video => "Video",
        PortKind.Audio => "Audio",
        PortKind.Subtitle => "Subtitles",
        PortKind.File => "File",
        PortKind.Chapters => "Chapters",
        _ => "Track",
    };
}
