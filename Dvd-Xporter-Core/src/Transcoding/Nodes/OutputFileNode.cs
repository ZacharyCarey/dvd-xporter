namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// An Output File node: puts tracks together into one file, with each track's title, language and flags, and the
/// file's chapters and tags. It shows a track input for every linked track, plus one to add another.
/// </summary>
public sealed class OutputFileNode(CanvasNode node)
{
    public const string ContainerKey = "container";
    public const string TitleKey = "title";
    public const string LibraryTagsKey = "library-tags";
    public const string CommentKey = "comment";
    public const string PlaybackKey = "playback";
    public const string StartKey = "start";
    public const string EndKey = "end";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.OutputFile,
        Title = "Output File",
        Category = NodeCategory.Outputs,
        Description = "Puts tracks together into one file. Each track's title, language and flags are set here.",
        Settings =
        [
            new(ContainerKey, "Container", SettingKind.Choice, "mkv", [new("mkv", "Matroska (.mkv)"), new("mp4", "MP4 (.mp4)")]),
            new(TitleKey, "Title", SettingKind.Text, Placeholder: "From the export"),
            new(LibraryTagsKey, "Write library info", SettingKind.Toggle, "true",
                Tooltip: "Tag the file with its movie or episode details from the Information step: date, overview, genres, "
                    + "and for an episode its show, season and number. Needs an Export node."),
            new(CommentKey, "Comment", SettingKind.Text, Placeholder: "Optional"),
            new(PlaybackKey, "Check playback", SettingKind.Choice, "all",
            [
                new("all", "On every device"),
                new("tv", "On TVs and boxes"),
                new("off", "Off"),
            ], Tooltip: "Points out tracks Emby would have to transcode while playing them, on these devices. Every device "
                + "includes phones, tablets and web browsers; TVs and boxes are smart TV apps, Android TV, Apple TV, Fire TV and Roku."),
            new(StartKey, "Start", SettingKind.Time, Placeholder: "Beginning"),
            new(EndKey, "End", SettingKind.Time, Placeholder: "End"),
        ],
        Ports = (n, context) => new OutputFileNode(n).Ports(context),
    };

    /// <summary>The file's extension: "mkv" (Matroska) or "mp4".</summary>
    public string Extension => node.Get(ContainerKey, "mkv") == "mp4" ? "mp4" : "mkv";

    /// <summary>The file's title as set; empty = the library item's.</summary>
    public string Title => node.Get(TitleKey);

    /// <summary>Whether the file is tagged with its library item's details.</summary>
    public bool LibraryTags => node.GetBool(LibraryTagsKey, true);

    public string Comment => node.Get(CommentKey).Trim();

    /// <summary>The devices whose playback is checked (see <see cref="Emby.EmbyPlayback.ParseDevices"/>).</summary>
    public string Playback => node.Get(PlaybackKey, "all");

    /// <summary>Where the file starts on its tracks' timeline; null = their beginning.</summary>
    public PreciseTime? Start => node.GetTime(StartKey);

    /// <summary>Where the file ends on its tracks' timeline; null = their end.</summary>
    public PreciseTime? End => node.GetTime(EndKey);

    /// <summary>A track option, e.g. <see cref="TranscodeCompiler.TitleOption"/> of track "t0".</summary>
    public string TrackOption(string track, string option, string fallback = "") =>
        node.Get(TranscodeCompiler.TrackOptionKey(track, option), fallback);

    public bool TrackFlag(string track, string option) => node.GetBool(TranscodeCompiler.TrackOptionKey(track, option));

    /// <summary>The file it makes, its chapters, and its tracks: one input for every linked track, and one to add another.</summary>
    public NodePorts Ports(ITranscodeContext context)
    {
        var inputs = new List<NodePort> { new(NodeTypes.ChaptersPort, "Chapters", PortKind.Chapters) };
        int tracks = context is ICanvasContext canvasContext ? NodeTypes.OutputTrackCount(node, canvasContext.Canvas) : 1;
        for (int i = 0; i < tracks; i++)
            inputs.Add(new NodePort(NodeTypes.TrackPortPrefix + i, $"Track {i + 1}", PortKind.Stream));
        return new NodePorts(inputs, [new NodePort(NodeTypes.FilePort, "File", PortKind.File)]);
    }
}
