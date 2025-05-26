using System;
using System.Collections.Generic;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.UI;

/// <summary>A control that edits one of a node's settings: it shows the setting's value and writes changes into the node.</summary>
public interface ISettingControl
{
    /// <summary>Raised after the setting changes, once the node's settings hold the new value.</summary>
    event Action? SettingChanged;

    /// <summary>Shows the setting's value (and its choices, for a choice) and starts writing changes into the node.</summary>
    void Bind(CanvasNode model, NodeSetting setting, IReadOnlyList<SettingChoice>? choices);
}
