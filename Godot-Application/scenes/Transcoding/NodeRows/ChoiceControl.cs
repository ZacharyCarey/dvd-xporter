using System;
using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using Godot;

namespace DvdXporter.UI;

/// <summary>A setting picked from a list, e.g. a Source's playlist or an encoder's preset.</summary>
public partial class ChoiceControl : OptionButton, ISettingControl
{
    private CanvasNode _model = null!;
    private NodeSetting _setting = null!;
    // Each item's value, in order
    private readonly List<string> _values = [];
    private bool _bound;

    public event Action? SettingChanged;

    public void Bind(CanvasNode model, NodeSetting setting, IReadOnlyList<SettingChoice>? choices)
    {
        (_model, _setting) = (model, setting);
        choices ??= [];
        Clear();
        _values.Clear();
        var current = model.Get(setting.Key, setting.Default);
        if (choices.All(c => c.Value != current))
        {
            // Nothing picked yet, or the pick is gone (e.g. a deleted playlist)
            AddItem(current.Length == 0 ? "Choose..." : $"(missing: {current})");
            _values.Add(current);
        }

        foreach (var choice in choices)
        {
            AddItem(choice.Label);
            _values.Add(choice.Value);
        }

        Select(_values.IndexOf(current));
        ShowPick();
        if (!_bound)
        {
            _bound = true;
            ItemSelected += index =>
            {
                ShowPick();
                _model.Set(_setting.Key, _values[(int)index]);
                SettingChanged?.Invoke();
            };
        }
    }

    // Without a tooltip of its own, it shows the whole of its pick, in case that's cut off
    private void ShowPick() => TooltipText = _setting.Tooltip ?? (Selected >= 0 ? GetItemText(Selected) : "");
}
