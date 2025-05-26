using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>Custom nodes: types built from custom node canvases.</summary>
public static class CustomNodes
{
    public static string TypeId(TranscodeCanvas canvas) => NodeTypes.CustomPrefix + canvas.Id;

    /// <summary>The canvas that defines a custom node type, if the project has it.</summary>
    public static TranscodeCanvas? Definition(string type, Project project) =>
        type.StartsWith(NodeTypes.CustomPrefix, StringComparison.Ordinal)
            ? project.Canvases.Find(c => c.Kind == CanvasKind.CustomNode && c.Id == type[NodeTypes.CustomPrefix.Length..])
            : null;

    public static string InputPort(CanvasNode input) => $"i{input.Id}";

    public static string OutputPort(CanvasNode output) => $"o{output.Id}";

    /// <summary>The key an exposed setting has on the custom node, e.g. "x4.crf" for setting "crf" of inner node 4.</summary>
    public static string ExposedKey(ExposedSetting setting) => $"x{setting.Node}.{setting.Key}";

    /// <summary>Settings of nodes of <paramref name="type"/> that can be shown on a custom node (not ones naming project items).</summary>
    public static bool CanExpose(NodeSetting setting, string type) =>
        setting.Kind is not (SettingKind.TrackOptions or SettingKind.ChapterList or SettingKind.TemplateRows or SettingKind.Action)
        && !(NodeTypes.IsSource(type) && setting.Key is SourceNode.DiscKey or SourceNode.ItemKey or SourceNode.StartChapterKey or SourceNode.EndChapterKey)
        && !(type == NodeTypes.Export && setting.Key == ExportNode.TargetKey);

    /// <summary>
    /// A custom node's type: an input port for each Custom Input inside it, an output for each Custom Output (both
    /// in top-to-bottom order), and the settings exposed from nodes inside.
    /// </summary>
    public static NodeType? Type(string id, Project project)
    {
        var definition = Definition(id, project);
        if (definition is null)
            return null;

        static List<CanvasNode> Ports(TranscodeCanvas canvas, string type) =>
            canvas.Nodes.Where(n => n.Type == type).OrderBy(n => n.Y).ThenBy(n => n.Id).ToList();
        var inputs = Ports(definition, NodeTypes.CustomInput);
        var outputs = Ports(definition, NodeTypes.CustomOutput);

        var settings = new List<NodeSetting>();
        foreach (var exposed in definition.Exposed)
        {
            var inner = definition.FindNode(exposed.Node);
            if (inner is null)
                continue;
            var innerType = NodeTypes.Resolve(inner.Type, project);
            var setting = innerType?.FindSetting(exposed.Key);
            if (innerType is null || setting is null)
                continue;

            // Two exposed settings with one name (e.g. two codecs' quality) are told apart by their node
            var label = definition.Exposed.Count(e =>
            {
                var n = definition.FindNode(e.Node);
                return n is not null && NodeTypes.Resolve(n.Type, project)?.FindSetting(e.Key)?.Label == setting.Label;
            }) > 1
                ? $"{innerType.Title}: {setting.Label}"
                : setting.Label;
            var key = ExposedKey(exposed);
            var visible = setting.Visible;
            // The value set inside is the custom node's default, shown in its empty field over the setting's own placeholder
            var value = inner.Get(exposed.Key);
            settings.Add(setting with
            {
                Key = key,
                Label = label,
                Default = inner.Get(exposed.Key, setting.Default),
                Placeholder = value.Length > 0 ? value : setting.Placeholder,
                Visible = visible is not null ? node => visible(WithOverrides(inner, node, exposed.Node)) : null,
            });
        }

        var description = $"Custom node: {inputs.Count} {(inputs.Count == 1 ? "input" : "inputs")}, "
            + $"{outputs.Count} {(outputs.Count == 1 ? "output" : "outputs")}.";
        return new NodeType
        {
            Id = id,
            Title = definition.Name,
            Category = NodeCategory.Custom,
            Description = description,
            Settings = settings,
            Ports = (_, _) => new NodePorts(
                inputs.Select(i => new NodePort(InputPort(i), new CustomPortNode(i).Name, new CustomPortNode(i).Kind)).ToList(),
                outputs.Select(o => new NodePort(OutputPort(o), new CustomPortNode(o).Name, new CustomPortNode(o).Kind)).ToList()),
        };
    }

    /// <summary>An inner node with the values set on a custom node instance for it.</summary>
    public static CanvasNode WithOverrides(CanvasNode inner, CanvasNode instance, int innerId)
    {
        var copy = inner.Clone(inner.Id);
        var prefix = $"x{innerId}.";
        foreach (var (key, value) in instance.Settings)
        {
            if (key.StartsWith(prefix, StringComparison.Ordinal))
                copy.Settings[key[prefix.Length..]] = value;
        }

        return copy;
    }
}
