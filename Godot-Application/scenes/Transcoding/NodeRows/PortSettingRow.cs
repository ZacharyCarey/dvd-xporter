using Godot;

namespace DvdXporter.UI;

/// <summary>A setting beside an output port, e.g. an output file's container next to its "File" port.</summary>
public partial class PortSettingRow : HBoxContainer
{
    /// <summary>In a node's own scene: the setting its control shows (see <see cref="SettingsNodeView"/>).</summary>
    [Export]
    public string SettingKey { get; set; } = "";

    /// <summary>In a node's own scene: the output port beside it.</summary>
    [Export]
    public string OutputKey { get; set; } = "";

    // The port's name goes last, beside its port, after a control the scene adds
    public override void _Ready() => MoveChild(GetNode("%PortLabel"), -1);

    public void Setup(Control control, string port)
    {
        AddChild(control);
        MoveChild(control, 0);
        GetNode<Label>("%PortLabel").Text = port;
    }
}
