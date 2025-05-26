using System;
using System.Collections.Generic;
using System.Globalization;
using DvdXporter.Core;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A setting typed in: text, or a number or time, which is put back as it was if it isn't one (or is out of range).
/// Changes are kept when Enter is pressed or the field is left.
/// </summary>
public partial class TextControl : LineEdit, ISettingControl
{
    private CanvasNode _model = null!;
    private NodeSetting _setting = null!;
    private bool _bound;

    public event Action? SettingChanged;

    public void Bind(CanvasNode model, NodeSetting setting, IReadOnlyList<SettingChoice>? choices)
    {
        (_model, _setting) = (model, setting);
        Text = model.Get(setting.Key);
        PlaceholderText = setting.Placeholder ?? setting.Default;
        SelectAllOnFocus = setting.Kind != SettingKind.Text;
        var tooltip = setting.Tooltip;
        if (tooltip is not null)
            TooltipText = tooltip;
        if (!_bound)
        {
            _bound = true;
            TextSubmitted += _ => Commit();
            FocusExited += Commit;
        }
    }

    private void Commit()
    {
        var text = Text.Trim();
        if (text == _model.Get(_setting.Key))
            return;
        if (text.Length > 0 && !IsValid(_setting, text))
        {
            Text = _model.Get(_setting.Key);
            return;
        }

        _model.Set(_setting.Key, text);
        SettingChanged?.Invoke();
    }

    private static bool IsValid(NodeSetting setting, string text) => setting.Kind switch
    {
        SettingKind.Integer => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)
            && i >= setting.Min && i <= setting.Max,
        SettingKind.Number => double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            && d >= setting.Min && d <= setting.Max,
        SettingKind.Time => PreciseTime.TryParse(text, out _),
        _ => true,
    };
}
