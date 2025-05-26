using Godot;

namespace DvdXporter.UI;

/// <summary>A button that does something for the node, e.g. a Crop's Detect Black Bars.</summary>
public partial class ActionButton : Button
{
    /// <summary>In a node's own scene: the action's key (see <see cref="SettingsNodeView"/>).</summary>
    [Export]
    public string ActionKey { get; set; } = "";
}
