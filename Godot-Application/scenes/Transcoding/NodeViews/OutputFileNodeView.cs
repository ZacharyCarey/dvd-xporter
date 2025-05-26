using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>
/// An Output File node (see <see cref="OutputFileNode"/>): its settings and chapters, then a row for each track (every
/// linked one, and one to add another), saying what it is, with a button to set its title, language and flags.
/// </summary>
public partial class OutputFileNodeView : SettingsNodeView
{
    private readonly List<Control> _trackRows = [];

    protected override void LayOut(ICanvasContext context, Dictionary<Control, (NodePort?, NodePort?)> ports)
    {
        RemoveRows(_trackRows);
        ClearTrackRows();
        foreach (var track in Type.GetPorts(Model, context).Inputs.Where(p => NodeTypes.TryParseTrackIndex(p.Key) is not null))
        {
            var row = BuildTrackRow(track.Key);
            InsertRow(row);
            _trackRows.Add(row);
            ports[row] = (track, null);
        }
    }
}
