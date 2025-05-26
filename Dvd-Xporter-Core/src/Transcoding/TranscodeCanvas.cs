using System.Globalization;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// One transcoding graph: sources, filters and codecs wired into output files. A project can have any number
/// of canvases, and each can hold any number of outputs.
/// </summary>
/// <remarks>
/// Nodes keep their settings as plain strings keyed by name and are wired port to port by port key rather than
/// position, so a node's ports can change (a different clip, more segments) without breaking the rest, and
/// new settings or node types never break older projects. What each node type means is in <see cref="NodeTypes"/>.
/// </remarks>
public sealed class TranscodeCanvas
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = "";

    public CanvasKind Kind { get; set; } = CanvasKind.Canvas;

    /// <summary>For a custom node: settings of nodes inside it that show on the custom node, to set per use.</summary>
    public List<ExposedSetting> Exposed { get; set; } = [];

    public List<CanvasNode> Nodes { get; set; } = [];

    public List<CanvasLink> Links { get; set; } = [];

    /// <summary>Frames grouping nodes on the canvas. Like comments, they don't change what's made.</summary>
    public List<CanvasFrame> Frames { get; set; } = [];

    /// <summary>Notes on the canvas.</summary>
    public List<CanvasComment> Comments { get; set; } = [];

    /// <summary>What a canvas of <paramref name="kind"/> is called, e.g. "Custom Node".</summary>
    public static string KindName(CanvasKind kind) => kind switch
    {
        CanvasKind.Template => "Template",
        CanvasKind.CustomNode => "Custom Node",
        _ => "Canvas",
    };

    public CanvasNode? FindNode(int id) => Nodes.Find(n => n.Id == id);

    public int NextNodeId() => Nodes.Count == 0 ? 1 : Nodes.Max(n => n.Id) + 1;

    /// <summary>The link into an input port, if any (an input takes one link; an output can feed many).</summary>
    public CanvasLink? LinkInto(int node, string port) => Links.Find(l => l.ToNode == node && l.ToPort == port);

    public void RemoveNode(int id)
    {
        Nodes.RemoveAll(n => n.Id == id);
        Links.RemoveAll(l => l.FromNode == id || l.ToNode == id);
        foreach (var frame in Frames)
            frame.Nodes.Remove(id);
    }

    public CanvasFrame? FindFrame(int id) => Frames.Find(f => f.Id == id);

    public int NextFrameId() => Frames.Count == 0 ? 1 : Frames.Max(f => f.Id) + 1;

    /// <summary>The frame a node is in, if any (a node is in at most one).</summary>
    public CanvasFrame? FrameOf(int node) => Frames.Find(f => f.Nodes.Contains(node));

    /// <summary>The frame a comment is in, if any.</summary>
    public CanvasFrame? FrameOfComment(int comment) => Frames.Find(f => f.Comments.Contains(comment));

    public CanvasComment? FindComment(int id) => Comments.Find(c => c.Id == id);

    public int NextCommentId() => Comments.Count == 0 ? 1 : Comments.Max(c => c.Id) + 1;

    public void RemoveComment(int id)
    {
        Comments.RemoveAll(c => c.Id == id);
        foreach (var frame in Frames)
            frame.Comments.Remove(id);
    }

    /// <summary>The canvas's contents (not its name or id), to put back with <see cref="Restore"/>, e.g. to undo.</summary>
    public string Snapshot() => System.Text.Json.JsonSerializer.Serialize(new CanvasContents(Nodes, Links, Frames, Comments, Exposed));

    public void Restore(string snapshot)
    {
        var contents = System.Text.Json.JsonSerializer.Deserialize<CanvasContents>(snapshot);
        if (contents is null)
            return;
        Nodes = contents.Nodes;
        Links = contents.Links;
        Frames = contents.Frames;
        Comments = contents.Comments;
        Exposed = contents.Exposed;
    }

    private sealed record CanvasContents(List<CanvasNode> Nodes, List<CanvasLink> Links, List<CanvasFrame> Frames,
        List<CanvasComment> Comments, List<ExposedSetting> Exposed);
}

/// <summary>
/// A frame on a canvas: a titled, tinted box around a group of nodes (and comments). With some in it, it fits
/// around them and moves with them; empty, it keeps its own size.
/// </summary>
public sealed class CanvasFrame
{
    public int Id { get; set; }

    public string Title { get; set; } = "";

    /// <summary>Its tint, as "#rrggbb"; empty = the default.</summary>
    public string Color { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 320;

    public double Height { get; set; } = 160;

    /// <summary>The ids of the nodes in it.</summary>
    public List<int> Nodes { get; set; } = [];

    /// <summary>The ids of the comments in it.</summary>
    public List<int> Comments { get; set; } = [];

    public bool IsEmpty => Nodes.Count == 0 && Comments.Count == 0;
}

/// <summary>A note on a canvas, typed straight into it.</summary>
public sealed class CanvasComment
{
    public int Id { get; set; }

    public string Text { get; set; } = "";

    /// <summary>Its tint, as "#rrggbb"; empty = the default.</summary>
    public string Color { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public double Width { get; set; } = 260;

    public double Height { get; set; } = 140;
}

public enum CanvasKind
{
    /// <summary>An ordinary canvas, whose outputs are made.</summary>
    Canvas,

    /// <summary>
    /// A graph made once and used for many videos: its Template Input stands for each video, and its Export
    /// nodes for each library item, as listed on a Template node.
    /// </summary>
    Template,

    /// <summary>A reusable chain of nodes, used as one node; its Custom Input and Output nodes are its ports.</summary>
    CustomNode,
}

/// <param name="Node">The node inside the custom node.</param>
/// <param name="Key">Its setting.</param>
public sealed record ExposedSetting(int Node, string Key);

public sealed class CanvasNode
{
    public int Id { get; set; }

    /// <summary>The node type's id in <see cref="NodeTypes"/>, e.g. "video-codec".</summary>
    public string Type { get; set; } = "";

    public double X { get; set; }

    public double Y { get; set; }

    public Dictionary<string, string> Settings { get; set; } = [];

    public string Get(string key, string fallback = "") =>
        Settings.TryGetValue(key, out var value) ? value : fallback;

    public int GetInt(string key, int fallback = 0) =>
        int.TryParse(Get(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public double GetDouble(string key, double fallback = 0) =>
        double.TryParse(Get(key), NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : fallback;

    public bool GetBool(string key, bool fallback = false) =>
        Settings.TryGetValue(key, out var value) ? value == "true" : fallback;

    /// <summary>A time typed in (see <see cref="PreciseTime.TryParse(string?, out PreciseTime)"/>); null if it's empty or isn't one.</summary>
    public PreciseTime? GetTime(string key) =>
        Get(key).Trim() is { Length: > 0 } text && PreciseTime.TryParse(text, out var time) ? time : null;

    /// <summary>Sets a setting; an empty value removes it, so it falls back to its default.</summary>
    public void Set(string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
            Settings.Remove(key);
        else
            Settings[key] = value;
    }

    public void Set(string key, bool value) => Settings[key] = value ? "true" : "false";

    public void Set(string key, double value) => Settings[key] = value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A copy with its own settings, for expanding templates and custom nodes.</summary>
    public CanvasNode Clone(int id) => new() { Id = id, Type = Type, X = X, Y = Y, Settings = new Dictionary<string, string>(Settings) };
}

/// <summary>A wire from one node's output port to another's input port.</summary>
public sealed record CanvasLink(int FromNode, string FromPort, int ToNode, string ToPort);
