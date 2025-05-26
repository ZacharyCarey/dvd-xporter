using System.Collections.Generic;
using System.Linq;
using System.Text;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A node laid out by its scene, whose rows each say what they show: a <see cref="SettingRow"/> or
/// <see cref="PortSettingRow"/> its setting, a <see cref="PortLabel"/> or <see cref="PortPairRow"/> its ports, an
/// <see cref="ActionButton"/> its action. Each is bound to the node here; a setting's row shows while the setting does,
/// and a port's while the node has the port. Types whose rows change with what they play or hold extend it.
/// </summary>
public partial class SettingsNodeView : TranscodeNodeView
{
    private readonly HashSet<ActionButton> _actions = [];

    public override bool Rebuild(ICanvasContext context, bool force = false)
    {
        var ports = Type.GetPorts(Model, context);
        var signature = new StringBuilder(PortSignature(ports));
        foreach (var key in SceneSettings())
        {
            var setting = Type.FindSetting(key);
            if (setting is not null)
                signature.Append('\n').Append(key).Append(':').Append(setting.IsShown(Model, context.Canvas.Kind)).Append(':').Append(ChoiceSignature(setting, context));
        }

        if (!StartRebuild(signature.Append('\n').Append(ExtraSignature(context)).ToString(), force))
            return false;

        var rowPorts = new Dictionary<Control, (NodePort?, NodePort?)>();
        foreach (var child in GetChildren())
        {
            switch (child)
            {
                case SettingRow { SettingKey.Length: > 0 } row:
                {
                    var setting = Type.FindSetting(row.SettingKey);
                    if (setting is null)
                        break;
                    row.Visible = setting.IsShown(Model, context.Canvas.Kind);
                    row.Setup(null, setting.Tooltip, ExposedKeys.Contains(setting.Key));
                    if (row.Visible)
                        BindControlsOf(row, setting.Key, context);
                    break;
                }
                case PortSettingRow { SettingKey.Length: > 0 } row:
                    BindControlsOf(row, row.SettingKey, context);
                    ShowPorts(row, null, row.OutputKey);
                    break;
                case PortPairRow row:
                    ShowPorts(row, row.InputKey, row.OutputKey);
                    break;
                case PortLabel label:
                    ShowPorts(label, label.InputKey, label.OutputKey);
                    break;
                case ActionButton { ActionKey.Length: > 0 } button when _actions.Add(button):
                    // A Chapters node's list is edited in a dialog; any other action is the page's to do
                    if (Type.FindSetting(button.ActionKey)?.Kind == SettingKind.ChapterList)
                        button.Pressed += RequestChaptersEdit;
                    else
                        BindAction(button, button.ActionKey);
                    break;
            }
        }

        LayOut(context, rowPorts);
        FinishRebuild(rowPorts);
        return true;

        // A port row shows while the node has its ports
        void ShowPorts(Control row, string? inputKey, string? outputKey)
        {
            var input = string.IsNullOrEmpty(inputKey) ? null : ports.Inputs.FirstOrDefault(p => p.Key == inputKey);
            var output = string.IsNullOrEmpty(outputKey) ? null : ports.Outputs.FirstOrDefault(p => p.Key == outputKey);
            var port = input ?? output;
            if (row is PortLabel { NamedByPort: true } named && port is not null)
                named.Text = port.Label;
            if (row is not PortSettingRow)
                row.Visible = input is not null || output is not null;
            if (input is not null || output is not null)
                rowPorts[row] = (input, output);
        }
    }

    /// <summary>
    /// Lays out what changes with the node's settings or the project, before its ports are given out: rows added here
    /// with ports go in <paramref name="ports"/>. Nothing, unless a type's view adds it.
    /// </summary>
    protected virtual void LayOut(ICanvasContext context, Dictionary<Control, (NodePort?, NodePort?)> ports)
    {
    }

    /// <summary>What else <see cref="LayOut"/> shows, for telling when the node needs laying out again.</summary>
    protected virtual string ExtraSignature(ICanvasContext context) => "";

    // The settings the scene's rows show
    private IEnumerable<string> SceneSettings() => GetChildren().Select(c => c switch
    {
        SettingRow row => row.SettingKey,
        PortSettingRow row => row.SettingKey,
        _ => "",
    }).Where(k => k.Length > 0);

    private void BindControlsOf(Node row, string key, ICanvasContext context)
    {
        foreach (var control in row.GetChildren().OfType<ISettingControl>())
            Bind(control, key, context);
    }
}
