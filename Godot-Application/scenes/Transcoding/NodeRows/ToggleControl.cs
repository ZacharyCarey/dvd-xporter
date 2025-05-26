using System;
using System.Collections.Generic;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>A setting that's on or off.</summary>
public partial class ToggleControl : CheckBox, ISettingControl
{
    private CanvasNode _model = null!;
    private NodeSetting _setting = null!;
    private bool _bound;

    public event Action? SettingChanged;

    public void Bind(CanvasNode model, NodeSetting setting, IReadOnlyList<SettingChoice>? choices)
    {
        (_model, _setting) = (model, setting);
        SetPressedNoSignal(model.GetBool(setting.Key, setting.Default == "true"));
        var tooltip = setting.Tooltip;
        if (tooltip is not null)
            TooltipText = tooltip;
        if (!_bound)
        {
            _bound = true;
            Toggled += pressed =>
            {
                _model.Set(_setting.Key, pressed);
                SettingChanged?.Invoke();
            };
        }
    }
}
