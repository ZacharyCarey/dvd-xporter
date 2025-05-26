namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Deinterlace node: turns interlaced video into whole frames, with bwdif or yadif.</summary>
public sealed class DeinterlaceNode(CanvasNode node)
{
    public const string MethodKey = "method";
    public const string ModeKey = "mode";
    public const string ParityKey = "parity";
    public const string FramesKey = "deint";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Deinterlace,
        Title = "Deinterlace",
        Category = NodeCategory.VideoFilters,
        Description = "Turns interlaced video into whole frames.\n\n" + PictureHelp.Interlacing
            + "\n\nFor film that's been telecined, use Inverse Telecine instead.",
        PassKind = PortKind.Video,
        Settings =
        [
            new(MethodKey, "Method", SettingKind.Choice, "bwdif", [new("bwdif", "Bwdif"), new("yadif", "Yadif")]),
            new(ModeKey, "Output", SettingKind.Choice, "send_frame",
            [
                new("send_frame", "One frame per frame"),
                new("send_field", "One frame per field (double rate)"),
            ]),
            new(ParityKey, "Field order", SettingKind.Choice, "auto", [new("auto", "Auto"), new("tff", "Top first"), new("bff", "Bottom first")]),
            new(FramesKey, "Deinterlace", SettingKind.Choice, "all", [new("all", "All frames"), new("interlaced", "Frames marked interlaced")]),
        ],
    };

    /// <summary>Whether it uses yadif, rather than bwdif.</summary>
    public bool UsesYadif => node.Get(MethodKey, "bwdif") == "yadif";

    /// <summary>"send_frame" (a frame per frame) or "send_field" (a frame per field, at double rate).</summary>
    public string Mode => node.Get(ModeKey, "send_frame");

    /// <summary>The field order: "auto", "tff" or "bff".</summary>
    public string Parity => node.Get(ParityKey, "auto");

    /// <summary>Which frames it deinterlaces: "all", or "interlaced" (those marked so).</summary>
    public string Frames => node.Get(FramesKey, "all");

    public string Filter => $"{(UsesYadif ? "yadif" : "bwdif")}=mode={Mode}:parity={Parity}:deint={Frames}";
}
