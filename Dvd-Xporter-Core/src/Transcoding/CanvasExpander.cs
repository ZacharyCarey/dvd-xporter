using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>Where a node of an expanded canvas came from: a node on the canvas, and what part of it.</summary>
/// <param name="NodeId">The node on the canvas (itself, a Template node, or a custom node).</param>
/// <param name="Prefix">For a copy: what it's part of, e.g. "S01E03 › " or "My Encode › ".</param>
public sealed record NodeOrigin(int NodeId, string? Prefix);

/// <summary>A canvas with its templates and custom nodes replaced by the nodes they stand for.</summary>
public sealed class ExpandedCanvas
{
    public required TranscodeCanvas Canvas { get; init; }

    /// <summary>Where each copied node came from; nodes not listed are the canvas's own.</summary>
    public Dictionary<int, NodeOrigin> Origins { get; } = [];

    /// <summary>The Output File copies made for each row of each Template node.</summary>
    public Dictionary<(int Node, int Row), List<int>> RowOutputs { get; } = [];

    /// <summary>Problems found while expanding, on the canvas's own nodes.</summary>
    public List<GraphIssue> Issues { get; } = [];

    public NodeOrigin OriginOf(int id) => Origins.GetValueOrDefault(id) ?? new NodeOrigin(id, null);
}

/// <summary>
/// Expands a canvas for compiling: each Template node becomes a copy of its template per row (the Template
/// Input playing the row's video, the Exports going to its library item), and each custom node becomes the
/// nodes inside it, wired to what's connected to it.
/// </summary>
public static class CanvasExpander
{
    // Copies get ids from here up, clear of the canvas's own
    private const int FirstCopyId = 1_000_000;
    // Custom nodes inside custom nodes, this deep at most (and a custom node can't contain itself)
    private const int MaxDepth = 8;

    public static ExpandedCanvas Expand(TranscodeCanvas canvas, Project project)
    {
        var result = new TranscodeCanvas { Id = canvas.Id, Name = canvas.Name, Kind = canvas.Kind, Nodes = [.. canvas.Nodes], Links = [.. canvas.Links] };
        var expanded = new ExpandedCanvas { Canvas = result };
        int nextId = Math.Max(FirstCopyId, canvas.NextNodeId());

        foreach (var template in result.Nodes.Where(n => n.Type == NodeTypes.Template).ToList())
            ExpandTemplate(expanded, template, project, ref nextId);

        for (int depth = 0; depth <= MaxDepth; depth++)
        {
            var customs = result.Nodes.Where(n => n.Type.StartsWith(NodeTypes.CustomPrefix, StringComparison.Ordinal)).ToList();
            if (customs.Count == 0)
                break;

            foreach (var custom in customs)
            {
                var origin = expanded.OriginOf(custom.Id);
                var definition = CustomNodes.Definition(custom.Type, project);
                if (depth == MaxDepth)
                {
                    expanded.Issues.Add(new GraphIssue(IssueLevel.Error, origin.NodeId, "Custom nodes are nested too deep (does one contain itself?)."));
                    Remove(result, custom);
                }
                else if (definition is not null)
                {
                    ExpandCustom(expanded, custom, definition, project, ref nextId);
                }
                else
                {
                    expanded.Issues.Add(new GraphIssue(IssueLevel.Error, origin.NodeId, "This custom node's definition is missing from the project."));
                    Remove(result, custom);
                }
            }
        }

        return expanded;
    }

    private static void ExpandTemplate(ExpandedCanvas expanded, CanvasNode node, Project project, ref int nextId)
    {
        var result = expanded.Canvas;
        Remove(result, node);
        var templateNode = new TemplateNode(node);
        var templateId = templateNode.Template;
        var template = project.Canvases.Find(c => c.Kind == CanvasKind.Template && c.Id == templateId);
        if (template is null)
        {
            expanded.Issues.Add(new GraphIssue(IssueLevel.Error, node.Id,
                templateId.Length == 0 ? "Pick a template." : "Its template is missing from the project."));
            return;
        }

        var rows = templateNode.Rows;
        if (rows.Count == 0)
            expanded.Issues.Add(new GraphIssue(IssueLevel.Warning, node.Id, "Add the videos to make with this template."));

        for (int r = 0; r < rows.Count; r++)
        {
            var row = rows[r];
            // Templates can't use Template nodes, so a template can't contain itself
            var map = CopyInto(result, template.Nodes.Where(n => n.Type != NodeTypes.Template), template.Links, ref nextId);
            var prefix = $"{TemplateRows.DescribeTarget(row, project)} › ";
            var outputs = new List<int>();
            foreach (var copyId in map.Values)
            {
                var copy = result.FindNode(copyId)!;
                expanded.Origins[copyId] = new NodeOrigin(node.Id, prefix);
                if (copy.Type == NodeTypes.TemplateInput)
                {
                    row.ApplyTo(copy);
                }
                else if (copy.Type == NodeTypes.Export)
                {
                    row.ApplyToExport(copy);
                }
                else if (NodeTypes.IsOutput(copy.Type))
                {
                    outputs.Add(copyId);
                }
            }

            expanded.RowOutputs[(node.Id, r)] = outputs;
        }
    }

    private static void ExpandCustom(ExpandedCanvas expanded, CanvasNode node, TranscodeCanvas definition, Project project, ref int nextId)
    {
        var result = expanded.Canvas;
        var origin = expanded.OriginOf(node.Id);
        var inner = definition.Nodes.Where(n => n.Type is not (NodeTypes.CustomInput or NodeTypes.CustomOutput)).ToList();
        var map = CopyInto(result, inner, definition.Links, ref nextId);

        // The values set on this use of the custom node
        foreach (var (innerId, copyId) in map)
        {
            var copy = result.FindNode(copyId)!;
            var overridden = CustomNodes.WithOverrides(copy, node, innerId);
            copy.Settings = overridden.Settings;
            expanded.Origins[copyId] = new NodeOrigin(origin.NodeId, $"{origin.Prefix}{definition.Name} › ");
        }

        // Wire what's connected to the custom node's ports to the nodes inside that use them
        var incoming = result.Links.Where(l => l.ToNode == node.Id).ToList();
        var outgoing = result.Links.Where(l => l.FromNode == node.Id).ToList();
        foreach (var link in definition.Links)
        {
            var from = definition.FindNode(link.FromNode);
            var to = definition.FindNode(link.ToNode);
            if (from is null || to is null || (from.Type != NodeTypes.CustomInput && to.Type != NodeTypes.CustomOutput))
                continue;

            var sources = from.Type == NodeTypes.CustomInput
                ? incoming.Where(l => l.ToPort == CustomNodes.InputPort(from)).Select(l => (l.FromNode, l.FromPort)).ToList()
                : map.TryGetValue(from.Id, out var fromCopy) ? [(fromCopy, link.FromPort)] : [];
            var targets = to.Type == NodeTypes.CustomOutput
                ? outgoing.Where(l => l.FromPort == CustomNodes.OutputPort(to)).Select(l => (l.ToNode, l.ToPort)).ToList()
                : map.TryGetValue(to.Id, out var toCopy) ? [(toCopy, link.ToPort)] : [];
            foreach (var (sourceNode, sourcePort) in sources)
            {
                foreach (var (targetNode, targetPort) in targets)
                    result.Links.Add(new CanvasLink(sourceNode, sourcePort, targetNode, targetPort));
            }
        }

        Remove(result, node);
    }

    /// <summary>Copies nodes, and the links between them, into a canvas under new ids. Returns old id → new id.</summary>
    private static Dictionary<int, int> CopyInto(TranscodeCanvas canvas, IEnumerable<CanvasNode> nodes, IEnumerable<CanvasLink> links, ref int nextId)
    {
        var map = new Dictionary<int, int>();
        foreach (var node in nodes)
        {
            var copy = node.Clone(nextId++);
            map[node.Id] = copy.Id;
            canvas.Nodes.Add(copy);
        }

        foreach (var link in links)
        {
            if (map.TryGetValue(link.FromNode, out var from) && map.TryGetValue(link.ToNode, out var to))
                canvas.Links.Add(link with { FromNode = from, ToNode = to });
        }

        return map;
    }

    private static void Remove(TranscodeCanvas canvas, CanvasNode node)
    {
        canvas.Nodes.Remove(node);
        canvas.Links.RemoveAll(l => l.FromNode == node.Id || l.ToNode == node.Id);
    }

    /// <summary>
    /// One row of a Template node as ordinary nodes, for detaching it: a copy of the template playing the row's
    /// video into its library item, with ids from <paramref name="firstId"/> and placed around (x, y).
    /// </summary>
    public static (List<CanvasNode> Nodes, List<CanvasLink> Links) DetachRow(TranscodeCanvas template, TemplateRow row, int firstId, double x, double y)
    {
        var scratch = new TranscodeCanvas();
        int nextId = firstId;
        var map = CopyInto(scratch, template.Nodes.Where(n => n.Type != NodeTypes.Template), template.Links, ref nextId);
        double left = template.Nodes.Count > 0 ? template.Nodes.Min(n => n.X) : 0, top = template.Nodes.Count > 0 ? template.Nodes.Min(n => n.Y) : 0;
        foreach (var node in scratch.Nodes)
        {
            node.X += x - left;
            node.Y += y - top;
            // A template's stand-in becomes an ordinary Disc Video
            if (node.Type == NodeTypes.TemplateInput)
            {
                node.Type = NodeTypes.Source;
                row.ApplyTo(node);
            }
            else if (node.Type == NodeTypes.Export)
            {
                row.ApplyToExport(node);
            }
        }

        return (scratch.Nodes, scratch.Links);
    }
}
