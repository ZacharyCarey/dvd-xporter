using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>A Template node (see <see cref="TemplateNode"/>): its template, and its videos, each with a test button.</summary>
public partial class TemplateNodeView : SettingsNodeView
{
    public override void _Ready()
    {
        base._Ready();
        TemplateVideos = GetNode<TemplateVideoList>("%Videos");
    }

    private void On_Videos_TestRequested(int index) => RequestTemplateRowTest(index);

    private void On_Videos_EditRequested() => RequestTemplateRowsEdit();

    protected override string ExtraSignature(ICanvasContext context) => string.Join("\n", Videos(context));

    protected override void LayOut(ICanvasContext context, Dictionary<Control, (NodePort?, NodePort?)> ports) =>
        TemplateVideos!.Setup(Videos(context));

    // Each video, as "disc › video  →  library item"
    private List<string> Videos(ICanvasContext context) =>
        new TemplateNode(Model).Rows.Select(r => TemplateRows.Describe(r, context)).ToList();
}
