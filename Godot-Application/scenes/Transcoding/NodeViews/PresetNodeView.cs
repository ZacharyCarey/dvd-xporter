using System.Collections.Generic;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A Preset node (see <see cref="PresetNode"/>): its video, then an audio pair for each linked audio track and one to add
/// another, then the preset with the container it's made for, and the values shown to adjust.
/// </summary>
public partial class PresetNodeView : SettingsNodeView
{
    private readonly List<Control> _audioRows = [];

    protected override void LayOut(ICanvasContext context, Dictionary<Control, (NodePort?, NodePort?)> ports)
    {
        RemoveRows(_audioRows);
        ShowText(GetNode<Label>("%ContainerHint"), new PresetNode(Model).ContainerHint);

        var nodePorts = Type.GetPorts(Model, context);
        var settings = GetNode<Control>("%PresetRow");
        for (int i = 0; i < nodePorts.Inputs.Count; i++)
        {
            var input = nodePorts.Inputs[i];
            if (PresetNode.AudioIndex(input.Key) is null)
                continue;
            var output = nodePorts.Outputs[i];
            var row = Row<PortPairRow>("PortPairRow");
            row.Setup(input.Label, output.Label);
            InsertRow(row, settings);
            _audioRows.Add(row);
            ports[row] = (input, output);
        }
    }

    protected override string ExtraSignature(ICanvasContext context) => new PresetNode(Model).Preset;
}
