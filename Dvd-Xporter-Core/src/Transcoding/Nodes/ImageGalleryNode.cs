namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>An Image Gallery node: saves a video's pictures as image files, one per picture it shows.</summary>
public sealed class ImageGalleryNode(CanvasNode node)
{
    public const string FormatKey = "format";
    public const string SkipBlackKey = "skip-black";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.ImageGallery,
        Title = "Image Gallery",
        Category = NodeCategory.Outputs,
        Description = "Saves a video's pictures as image files, e.g. a disc's gallery of concept art or stills: one image "
            + "per picture it shows, skipping repeated frames and the fades between pictures. Connect its File to an Export "
            + "set to a Gallery extra (added in the Information step): the images go in a Gallery folder in the library. "
            + "Emby doesn't show that folder, but it keeps the pictures with the rest.",
        Settings =
        [
            new(FormatKey, "Format", SettingKind.Choice, "png", [new("png", "PNG (.png)"), new("jpg", "JPEG (.jpg)")],
                Tooltip: "PNG keeps every detail; JPEG files are much smaller."),
            new(SkipBlackKey, "Skip black pictures", SettingKind.Toggle, "true",
                Tooltip: "Leave out pictures that are all black, such as the blank moments before and after a gallery."),
        ],
        Ports = (_, _) => new NodePorts([new NodePort("in", "Video", PortKind.Video)], [new NodePort(NodeTypes.FilePort, "File", PortKind.File)]),
    };

    /// <summary>The images' extension: "png" or "jpg".</summary>
    public string Extension => node.Get(FormatKey, "png") == "jpg" ? "jpg" : "png";

    /// <summary>Whether pictures that are all black are left out.</summary>
    public bool SkipBlack => node.GetBool(SkipBlackKey, true);

    /// <summary>
    /// The filters that pick the pictures out: repeated frames go, at square pixels so wide DVD pictures aren't
    /// squashed; blackframe logs the black ones and showinfo each one's time, for dropping those and fades afterwards.
    /// </summary>
    public string Filters => "mpdecimate,scale='trunc(iw*sar/2)*2':ih:flags=lanczos,setsar=1"
        + (SkipBlack ? ",blackframe=amount=98:threshold=24" : "") + ",showinfo";

    /// <summary>The output options that write each picture as an image.</summary>
    public IReadOnlyList<string> EncoderOptions => Extension == "jpg" ? ["-c:v", "mjpeg", "-q:v", "2", "-pix_fmt", "yuvj444p"] : ["-c:v", "png"];
}
