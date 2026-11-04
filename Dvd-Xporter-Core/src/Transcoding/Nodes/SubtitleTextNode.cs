using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>The disc track a Subtitle Text node turns into text, and whether it takes just the forced captions.</summary>
public sealed record SubtitleTextSource(ProjectDisc Disc, IReadOnlyList<PlaylistEntry> Entries, ClipTrack Track, bool ForcedOnly);

/// <summary>
/// A Subtitle Text node: a picture subtitle track (DVD or Blu-ray) as text subtitles, from its text read off the pictures
/// with Tesseract and corrected by the user (see <see cref="Subtitles.SubtitleTexts"/>).
/// </summary>
public static class SubtitleTextNode
{
    /// <summary>The button that opens the track's text, to read and correct.</summary>
    public const string EditAction = "edit";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.SubtitleText,
        Title = "Subtitle Text",
        Category = NodeCategory.Subtitles,
        Description = "Turns DVD or Blu-ray picture subtitles into text subtitles (SRT), which players show without Emby "
            + "transcoding the video. The text is read off the pictures with Tesseract OCR, then you correct it; it's kept in "
            + "the project.\n\nConnect a subtitle track straight from a Disc Video, or a Forced Subtitles node for just the "
            + "forced captions.",
        PassKind = PortKind.Subtitle,
        Settings =
        [
            new(EditAction, "Read & Correct Text...", SettingKind.Action,
                Tooltip: "Reads the text off the subtitles' pictures, and shows it beside them to correct."),
        ],
    };

    /// <summary>
    /// The disc track wired into the node: straight from a Disc Video (or a template's Template Input), or through a
    /// Forced Subtitles node. Null if it isn't wired to one, or the video can't be found.
    /// </summary>
    public static SubtitleTextSource? FindSource(TranscodeCanvas canvas, ITranscodeContext context, CanvasNode node)
    {
        var link = canvas.LinkInto(node.Id, "in");
        var forcedOnly = false;
        var from = link is null ? null : canvas.FindNode(link.FromNode);
        if (link is not null && from is not null && from.Type == NodeTypes.ForcedSubtitles)
        {
            forcedOnly = true;
            link = canvas.LinkInto(from.Id, "in");
            from = link is null ? null : canvas.FindNode(link.FromNode);
        }

        if (link is null || from is null || from.Type is not (NodeTypes.Source or NodeTypes.TemplateInput))
            return null;
        var selection = new SourceNode(from).Resolve(context);
        var track = selection?.Tracks.FirstOrDefault(t => SourceNode.TrackPortKey(t) == link.FromPort);
        if (selection is null || track is null || track.Kind != ClipTrackKind.Subtitle)
            return null;
        return new SubtitleTextSource(selection.Disc, selection.Entries, track, forcedOnly);
    }
}
