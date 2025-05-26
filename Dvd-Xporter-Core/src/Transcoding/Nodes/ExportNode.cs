namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>An Export node: where a finished file goes in the Emby library: the movie, an episode, or an extra.</summary>
public sealed class ExportNode(CanvasNode node)
{
    public const string TargetKey = "target";
    public const string VersionKey = "version";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Export,
        Title = "Export",
        Category = NodeCategory.Outputs,
        Description = "Where a finished file goes in the Emby library: the movie, an episode, or an extra. Extras are "
            + "added in the Information step; an Image Gallery's pictures go to a Gallery extra.",
        Settings =
        [
            // A template's library items are its videos', picked on the Template node
            new(TargetKey, "Library item", SettingKind.Choice, ShownIn: kind => kind != CanvasKind.Template),
            new(VersionKey, "Version", SettingKind.Text, Placeholder: "Optional, e.g. Director's Cut",
                Visible: n => !new ExportNode(n).IsExtra,
                Tooltip: "Emby shows files with different versions as alternatives of the same movie or episode."),
        ],
        Ports = (_, _) => new NodePorts([new NodePort(NodeTypes.FilePort, "File", PortKind.File)], []),
    };

    /// <summary>A new Export, e.g. to describe a library item.</summary>
    public static CanvasNode Create(string target)
    {
        var created = new CanvasNode { Type = NodeTypes.Export };
        _ = new ExportNode(created) { Target = target };
        return created;
    }

    /// <summary>
    /// The library item: <see cref="NodeTypes.MovieTarget"/>, an episode (<see cref="NodeTypes.EpisodeTargetPrefix"/>) or
    /// an extra (<see cref="NodeTypes.ExtraTargetPrefix"/>); empty = none picked.
    /// </summary>
    public string Target
    {
        get => node.Get(TargetKey);
        set => node.Set(TargetKey, value);
    }

    public bool IsExtra => NodeTypes.IsExtraTarget(Target);

    /// <summary>The version, e.g. "Director's Cut"; empty = none.</summary>
    public string Version
    {
        get => node.Get(VersionKey).Trim();
        set => node.Set(VersionKey, value);
    }
}
