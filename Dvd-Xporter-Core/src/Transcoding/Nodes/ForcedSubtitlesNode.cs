namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Forced Subtitles node: just the forced captions of a disc subtitle track, as a track of their own.</summary>
public static class ForcedSubtitlesNode
{
    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.ForcedSubtitles,
        Title = "Forced Subtitles",
        Category = NodeCategory.Subtitles,
        Description = "Just the forced captions of a subtitle track, as a track of their own, marked forced so players show "
            + "it when subtitles are off.\n\n" + SubtitleHelp.Forced,
        PassKind = PortKind.Subtitle,
    };
}
