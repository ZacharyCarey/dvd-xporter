using DvdXporter.Core.Projects;
using DvdXporter.Core.Storage;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// The user's own templates and custom nodes, kept outside any project so they can be added to others. A
/// project always holds its own copy of each one it uses, so a shared project works without the library.
/// </summary>
public sealed class TranscodeLibrary
{
    public List<TranscodeCanvas> Items { get; set; } = [];

    public static TranscodeLibrary Load(string path) => JsonFile.TryRead<TranscodeLibrary>(path) ?? new TranscodeLibrary();

    public void Save(string path) => JsonFile.Write(path, this);

    /// <summary>
    /// Saves a project's template or custom node into the library, with the custom nodes it uses, replacing the
    /// library's earlier copies of them (the same canvas, by id).
    /// </summary>
    public void Add(TranscodeCanvas canvas, Project project)
    {
        foreach (var item in WithDependencies(canvas, project.Canvases))
        {
            Items.RemoveAll(i => i.Id == item.Id);
            Items.Add(JsonFile.Clone(item));
        }
    }

    /// <summary>
    /// Copies a library item into a project, with the custom nodes it uses, replacing the project's copies of
    /// them. Sample videos and library items that aren't in this project are cleared, to be picked again.
    /// </summary>
    /// <returns>The project's copy of the item.</returns>
    public TranscodeCanvas AddToProject(TranscodeCanvas item, Project project)
    {
        TranscodeCanvas? added = null;
        foreach (var dependency in WithDependencies(item, Items))
        {
            var copy = JsonFile.Clone(dependency);
            foreach (var node in copy.Nodes)
            {
                if (NodeTypes.IsSource(node.Type) && new SourceNode(node) is var source && source.FindDisc(project) is null)
                {
                    source.Disc = "";
                    source.Item = "";
                }
                else if (node.Type == NodeTypes.Export && new ExportNode(node) is { Target.Length: > 0 } export
                    && NodeTypes.ExportTargetChoices(project).All(c => c.Value != export.Target))
                {
                    export.Target = "";
                }
            }

            var index = project.Canvases.FindIndex(c => c.Id == copy.Id);
            if (index >= 0)
                project.Canvases[index] = copy;
            else
                project.Canvases.Add(copy);
            if (dependency.Id == item.Id)
                added = copy;
        }

        return added!;
    }

    public void Remove(string id) => Items.RemoveAll(i => i.Id == id);

    /// <summary>A canvas and every custom node it uses, directly or through other custom nodes.</summary>
    private static List<TranscodeCanvas> WithDependencies(TranscodeCanvas canvas, List<TranscodeCanvas> available)
    {
        var found = new List<TranscodeCanvas>();
        var queue = new Queue<TranscodeCanvas>([canvas]);
        while (queue.TryDequeue(out var next))
        {
            if (found.Any(f => f.Id == next.Id))
                continue;
            found.Add(next);
            foreach (var node in next.Nodes.Where(n => n.Type.StartsWith(NodeTypes.CustomPrefix, StringComparison.Ordinal)))
            {
                var id = node.Type[NodeTypes.CustomPrefix.Length..];
                var used = available.Find(c => c.Id == id);
                if (used is not null)
                    queue.Enqueue(used);
            }
        }

        return found;
    }
}
