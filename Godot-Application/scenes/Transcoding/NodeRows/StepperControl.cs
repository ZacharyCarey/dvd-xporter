using System;
using System.Collections.Generic;
using System.Globalization;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>A whole number with up/down arrows, kept within the setting's range.</summary>
public partial class StepperControl : SpinBox, ISettingControl
{
    private CanvasNode _model = null!;
    private NodeSetting _setting = null!;
    private bool _bound;

    public event Action? SettingChanged;

    public void Bind(CanvasNode model, NodeSetting setting, IReadOnlyList<SettingChoice>? choices)
    {
        (_model, _setting) = (model, setting);
        var current = int.TryParse(model.Get(setting.Key, setting.Default), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n)
            ? n
            : (int)setting.Min;
        MinValue = setting.Min;
        MaxValue = setting.Max;
        SetValueNoSignal(Math.Clamp(current, setting.Min, setting.Max));
        var tooltip = setting.Tooltip;
        if (tooltip is not null)
            TooltipText = tooltip;
        if (!_bound)
        {
            _bound = true;
            ValueChanged += value =>
            {
                var text = ((int)value).ToString(CultureInfo.InvariantCulture);
                if (text == _model.Get(_setting.Key, _setting.Default))
                    return;
                _model.Set(_setting.Key, text);
                SettingChanged?.Invoke();
            };
        }
    }
}
