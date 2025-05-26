namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Template node: makes a template's outputs for each video listed, each going to its own library item.</summary>
public sealed class TemplateNode(CanvasNode node)
{
    public const string TemplateKey = "template";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Template,
        Title = "Template",
        Category = NodeCategory.Custom,
        Description = "Makes a template's outputs for each video you list, each going to its own library item.",
        Settings =
        [
            new(TemplateKey, "Template", SettingKind.Choice),
            new(TemplateRows.Setting, "Videos", SettingKind.TemplateRows),
        ],
        Ports = (_, _) => new NodePorts([], []),
        AllowedIn = kind => kind == CanvasKind.Canvas,
    };

    /// <summary>The template's canvas id.</summary>
    public string Template
    {
        get => node.Get(TemplateKey);
        set => node.Set(TemplateKey, value);
    }

    /// <summary>The videos it makes the template's outputs for (a copy: set it to change them).</summary>
    public List<TemplateRow> Rows
    {
        get => TemplateRows.Parse(node.Get(TemplateRows.Setting));
        set => node.Set(TemplateRows.Setting, TemplateRows.Serialize(value));
    }
}
