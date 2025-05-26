using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// A Concat node (see <see cref="ConcatNode"/>): how many segments and streams, then each segment's inputs, with the
/// joined streams coming out beside the first segment's.
/// </summary>
public partial class ConcatNodeView : SettingsNodeView
{
    private readonly List<Control> _segmentRows = [];

    protected override void LayOut(ICanvasContext context, Dictionary<Control, (NodePort?, NodePort?)> ports)
    {
        RemoveRows(_segmentRows);
        var concat = new ConcatNode(Model);
        var nodePorts = Type.GetPorts(Model, context);
        int line = 0;
        for (int s = 0; s < concat.Segments; s++)
        {
            var heading = Row<Label>("HintRow");
            ShowText(heading, $"Segment {s + 1}");
            Add(heading);
            for (int j = 0; j < concat.Streams; j++)
            {
                var input = nodePorts.Inputs.First(p => p.Key == concat.InputPort(s, j));
                var output = nodePorts.Outputs.ElementAtOrDefault(line++);
                Control row;
                if (output is not null)
                {
                    var pair = Row<PortPairRow>("PortPairRow");
                    pair.Setup(input.Label, output.Label);
                    row = pair;
                }
                else
                {
                    var label = Row<Label>("InputPortRow");
                    label.Text = input.Label;
                    row = label;
                }

                Add(row);
                ports[row] = (input, output);
            }
        }

        void Add(Control row)
        {
            InsertRow(row);
            _segmentRows.Add(row);
        }
    }
}
