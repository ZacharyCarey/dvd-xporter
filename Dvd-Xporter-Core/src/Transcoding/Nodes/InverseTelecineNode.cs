namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// An Inverse Telecine node: restores film's 23.976 frames from hard-telecined 29.97 video, matching fields back into
/// frames (fieldmatch) and dropping the repeats (decimate). Discs that mix film with parts made on video can keep those
/// parts' frames, at a variable frame rate.
/// </summary>
public sealed class InverseTelecineNode(CanvasNode node)
{
    public const string OutputKey = "output";
    public const string CleanupKey = "cleanup";

    /// <summary>The film's frames only: every fifth frame (the repeat) dropped, at 23.976 fps.</summary>
    public const string Film = "film";

    /// <summary>Film parts at 23.976 fps and parts made on video at 29.97: a variable frame rate.</summary>
    public const string Mixed = "mixed";

    /// <summary>Fields matched into whole frames, but none dropped: 29.97 fps.</summary>
    public const string Matched = "matched";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.InverseTelecine,
        Title = "Inverse Telecine",
        Category = NodeCategory.VideoFilters,
        Description = "Restores the original 23.976 fps film frames from hard-telecined 29.97 fps video.\n\n"
            + PictureHelp.Telecine + "\n\n" + PictureHelp.HardTelecine + "\n\n" + PictureHelp.Hybrid,
        PassKind = PortKind.Video,
        Settings =
        [
            new(OutputKey, "Output", SettingKind.Choice, Film,
            [
                new(Film, "Film (23.976 fps)"),
                new(Mixed, "Film and video (variable rate)"),
                new(Matched, "Matched fields (29.97 fps)"),
            ],
                Tooltip: "Film: the film's frames, with the repeats dropped. For a disc that's all film.\n\n"
                    + "Film and video: drops repeats only where the video is film, and keeps every frame of parts made on "
                    + "video (credits, effects, interviews), so they don't judder. The file has a variable frame rate.\n\n"
                    + "Matched fields: whole frames, none dropped, at 29.97 fps. Film plays with a slight 3:2 judder."),
            new(CleanupKey, "Deinterlace leftovers", SettingKind.Toggle, "true",
                Tooltip: "Deinterlaces frames that still look combed after matching fields (e.g. video-based effects)."),
        ],
    };

    /// <summary>What it gives: <see cref="Film"/>, <see cref="Mixed"/> or <see cref="Matched"/>.</summary>
    public string Output => node.Get(OutputKey, Film);

    /// <summary>Whether frames still combed after matching fields are deinterlaced.</summary>
    public bool Cleanup => node.GetBool(CleanupKey, true);

    /// <summary>Whether its frames come at a variable rate, so the file has to be written that way.</summary>
    public bool IsVariableRate => Output == Mixed;

    public string Filter
    {
        get
        {
            var filter = Cleanup ? "fieldmatch=order=auto:combmatch=full,yadif=deint=interlaced" : "fieldmatch=order=auto";
            return Output switch
            {
                Matched => filter,
                Mixed => filter + ",decimate=mixed=1",
                _ => filter + ",decimate",
            };
        }
    }
}
