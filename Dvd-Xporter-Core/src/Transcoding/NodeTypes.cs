using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// Every kind of node a canvas can hold. Ids are saved in projects, so never change one; retire it instead.
/// </summary>
public static partial class NodeTypes
{
    public const string Source = "source";

    public const string SolidColor = "solid-color";

    public const string Silence = "silence";

    public const string Deinterlace = "deinterlace";

    public const string InverseTelecine = "inverse-telecine";

    public const string Crop = "crop";

    public const string Scale = "scale";

    public const string Aspect = "aspect";

    public const string FrameRate = "frame-rate";

    public const string DynamicRangeNode = "dynamic-range";

    public const string Flip = "flip";

    public const string DrawText = "draw-text";

    public const string Overlay = "overlay";

    public const string CustomVideo = "custom-video";

    public const string Volume = "volume";

    public const string Downmix = "downmix";

    public const string Loudness = "loudness";

    public const string AudioDelay = "audio-delay";

    public const string CustomAudio = "custom-audio";

    public const string Concat = "concat";

    public const string ForcedSubtitles = "forced-subtitles";

    public const string BurnSubtitles = "burn-subtitles";

    public const string SubtitleText = "subtitle-text";

    public const string Chapters = "chapters";

    public const string TemplateInput = "template-input";

    public const string Template = "template";

    public const string CustomInput = "custom-input";

    public const string CustomOutput = "custom-output";

    /// <summary>A custom node's type is this followed by the id of the canvas that defines it.</summary>
    public const string CustomPrefix = "custom:";

    public const string VideoCodec = "video-codec";

    public const string AudioCodec = "audio-codec";

    public const string OutputFile = "output-file";

    public const string Export = "export";

    public const string ImageGallery = "image-gallery";

    /// <summary>Whether a node makes something to export: an Output File, or an Image Gallery.</summary>
    public static bool IsOutput(string type) => type is OutputFile or ImageGallery;

    // Source "item" values: one video file, or one of the disc's playlists in the Clip Renamer
    public const string ClipItemPrefix = "clip:";

    public const string PlaylistItemPrefix = "playlist:";

    // Export "target" values
    public const string MovieTarget = "movie";

    public const string EpisodeTargetPrefix = "episode:";

    /// <summary>An extra in the Information step, by its ID: "extra:3f2a...".</summary>
    public const string ExtraTargetPrefix = "extra:";

    /// <summary>The finished file's port, on Output File and Image Gallery (out) and Export (in).</summary>
    public const string FilePort = "file";

    /// <summary>The chapters port on Disc Video (out) and Output File (in).</summary>
    public const string ChaptersPort = "chapters";

    /// <summary>An output file's tracks are keyed "t0", "t1", ...; their options are settings under the same key.</summary>
    public const string TrackPortPrefix = "t";

    /// <summary>Whether a node plays a disc video: a Disc Video, or a template's stand-in for one.</summary>
    public static bool IsSource(string type) => type is Source or TemplateInput;

    /// <summary>A node type by id, including the project's custom nodes; null if there's no such type.</summary>
    public static NodeType? Resolve(string id, Project project) =>
        id.StartsWith(CustomPrefix, StringComparison.Ordinal) ? CustomNodes.Type(id, project) : Get(id);

    public static NodeType? Get(string id) => All.FirstOrDefault(t => t.Id == id);

    private static NodeType Find(string id) => Get(id)!;

    public static string CategoryName(NodeCategory category) => category switch
    {
        NodeCategory.Inputs => "Inputs",
        NodeCategory.VideoFilters => "Video Filters",
        NodeCategory.AudioFilters => "Audio Filters",
        NodeCategory.Combine => "Combine",
        NodeCategory.Subtitles => "Subtitles",
        NodeCategory.Codecs => "Codecs",
        NodeCategory.Outputs => "Outputs",
        NodeCategory.Custom => "Templates & Custom Nodes",
        _ => category.ToString(),
    };

    /// <summary>A new node of a type, with any defaults that depend on the project filled in.</summary>
    public static CanvasNode Create(TranscodeCanvas canvas, NodeType type, double x, double y) =>
        new() { Id = canvas.NextNodeId(), Type = type.Id, X = x, Y = y };

    /// <summary>How many track inputs an output file shows: every linked one, plus an empty one to add another.</summary>
    public static int OutputTrackCount(CanvasNode node, TranscodeCanvas canvas)
    {
        int highest = -1;
        foreach (var link in canvas.Links.Where(l => l.ToNode == node.Id))
        {
            var index = TryParseTrackIndex(link.ToPort);
            if (index is not null)
                highest = Math.Max(highest, index.Value);
        }

        return highest + 2;
    }

    public static int? TryParseTrackIndex(string port) =>
        port.StartsWith(TrackPortPrefix, StringComparison.Ordinal) && int.TryParse(port[TrackPortPrefix.Length..], out var index) ? index : null;

    public static bool IsExtraTarget(string target) => target.StartsWith(ExtraTargetPrefix, StringComparison.Ordinal);
}
