using Godot;

namespace DvdXporter.UI;

/// <summary>A row with a port on each side, e.g. a filter's video in and out.</summary>
public partial class PortPairRow : HBoxContainer
{
    /// <summary>In a node's own scene: the input port it shows (see <see cref="SettingsNodeView"/>).</summary>
    [Export]
    public string InputKey { get; set; } = "";

    /// <summary>In a node's own scene: the output port it shows.</summary>
    [Export]
    public string OutputKey { get; set; } = "";

    public void Setup(string input, string output)
    {
        GetNode<Label>("%InputLabel").Text = input;
        GetNode<Label>("%OutputLabel").Text = output;
    }
}
