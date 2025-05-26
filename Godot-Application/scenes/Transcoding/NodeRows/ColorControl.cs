using System;
using System.Collections.Generic;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>A colour setting, saved as "#rrggbb".</summary>
public partial class ColorControl : ColorPickerButton, ISettingControl
{
    private CanvasNode _model = null!;
    private NodeSetting _setting = null!;
    private bool _bound;

    public event Action? SettingChanged;

    public void Bind(CanvasNode model, NodeSetting setting, IReadOnlyList<SettingChoice>? choices)
    {
        (_model, _setting) = (model, setting);
        Color = new Color(model.Get(setting.Key, setting.Default));
        var tooltip = setting.Tooltip;
        if (tooltip is not null)
            TooltipText = tooltip;
        if (!_bound)
        {
            _bound = true;
            PopupClosed += () =>
            {
                _model.Set(_setting.Key, "#" + Color.ToHtml(false));
                SettingChanged?.Invoke();
            };
        }
    }
}
