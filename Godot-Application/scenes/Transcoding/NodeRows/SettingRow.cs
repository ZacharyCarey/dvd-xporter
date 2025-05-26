using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A setting's row: its name, then its control (and before that, a start or end's mode, if it has one). In a node's
/// own scene the name is the scene's; made for a setting, it's the setting's.
/// </summary>
public partial class SettingRow : HBoxContainer
{
    /// <summary>In a node's own scene: the setting its control shows (see <see cref="SettingsNodeView"/>).</summary>
    [Export]
    public string SettingKey { get; set; } = "";

    // The name as the scene gives it, before any ◆
    private string? _sceneText;

    /// <summary>Shows the name (null = the scene's), its tooltip, and whether it's shown on the custom node (◆).</summary>
    public void Setup(string? label, string? tooltip, bool exposed)
    {
        var name = GetNode<Label>("%Label");
        var text = label ?? (_sceneText ??= name.Text);
        name.Text = exposed ? "◆ " + text : text;
        name.TooltipText = (exposed ? "Shown on the custom node, to set per use. " : "") + (tooltip ?? "");
    }

    /// <summary>Adds a control after the name and the controls already added.</summary>
    public void AddControl(Control control) => AddChild(control);
}
