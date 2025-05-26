using Godot;

namespace DvdXporter.UI;

/// <summary>A row that's just a port, on one side: its name.</summary>
public partial class PortLabel : Label
{
    /// <summary>In a node's own scene: the input port it shows (see <see cref="SettingsNodeView"/>).</summary>
    [Export]
    public string InputKey { get; set; } = "";

    /// <summary>In a node's own scene: the output port it shows.</summary>
    [Export]
    public string OutputKey { get; set; } = "";

    /// <summary>Whether it's named as the port is, e.g. a Custom Input's, which is named in its settings.</summary>
    [Export]
    public bool NamedByPort { get; set; }
}
