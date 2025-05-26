using System.Collections.Generic;
using System.Linq;
using DvdXporter.Core.Transcoding;
using DvdXporter.Core.Transcoding.Nodes;
using Godot;

namespace DvdXporter.UI;

/// <summary>A Chapters node (see <see cref="ChaptersNode"/>): its settings, and the chapters it gives.</summary>
public partial class ChaptersNodeView : SettingsNodeView
{
    // How many chapters it lists
    private const int ChaptersShown = 8;

    protected override string ExtraSignature(ICanvasContext context) => Summary(context);

    protected override void LayOut(ICanvasContext context, Dictionary<Control, (NodePort?, NodePort?)> ports) =>
        ShowText(GetNode<Label>("%Info"), Summary(context));

    /// <summary>"3 chapters (edited):" and the first few, each with where it starts.</summary>
    private string Summary(ICanvasContext context)
    {
        var chapters = ChapterLists.Resolve(Model, "out", context)?.Chapters ?? [];
        if (chapters.Count == 0)
            return "No chapters. Connect a Disc Video's chapters, or edit the list.";
        var edited = new ChaptersNode(Model).IsEdited ? " (edited)" : "";
        return $"{chapters.Count} {(chapters.Count == 1 ? "chapter" : "chapters")}{edited}:\n"
            + string.Join("\n", chapters.Take(ChaptersShown).Select(c => $"{NodeTypes.FormatDuration(c.Start)}  {c.Name}"))
            + (chapters.Count > ChaptersShown ? $"\n... and {chapters.Count - ChaptersShown} more" : "");
    }
}
