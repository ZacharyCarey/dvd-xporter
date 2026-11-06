using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

// Every node type, in the order they're offered. Each is defined by its node's class in Nodes/.
public static partial class NodeTypes
{
    public static readonly IReadOnlyList<SettingChoice> FrameRates =
    [
        new("24000/1001", "23.976 (NTSC film)"),
        new("24", "24"),
        new("25", "25 (PAL)"),
        new("30000/1001", "29.97 (NTSC)"),
        new("30", "30"),
        new("50", "50"),
        new("60000/1001", "59.94"),
        new("60", "60"),
    ];

    // After FrameRates, which the types use: static fields initialize in order within a file
    public static readonly IReadOnlyList<NodeType> All =
    [
        // ---- Inputs ----
        SourceNode.Type,
        SourceNode.TemplateInputType,
        SolidColorNode.Type,
        SilenceNode.Type,

        // ---- Video filters ----
        DeinterlaceNode.Type,
        InverseTelecineNode.Type,
        CropNode.Type,
        ScaleNode.Type,
        AspectNode.Type,
        Nodes.DynamicRangeNode.Type,
        FrameRateNode.Type,
        FlipNode.Type,
        DrawTextNode.Type,
        OverlayNode.Type,
        CustomFilterNode.VideoType,

        // ---- Audio filters ----
        VolumeNode.Type,
        DownmixNode.Type,
        LoudnessNode.Type,
        AudioDelayNode.Type,
        CustomFilterNode.AudioType,

        // ---- Combine ----
        ConcatNode.Type,
        ChaptersNode.Type,

        // ---- Subtitles ----
        ForcedSubtitlesNode.Type,
        SubtitleTextNode.Type,
        BurnSubtitlesNode.Type,

        // ---- Presets ----
        PresetNode.Type,

        // ---- Codecs ----
        VideoCodecNode.Type,
        AudioCodecNode.Type,

        // ---- Outputs ----
        OutputFileNode.Type,
        ImageGalleryNode.Type,
        ExportNode.Type,

        // ---- Templates and custom nodes ----
        TemplateNode.Type,
        CustomPortNode.InputType,
        CustomPortNode.OutputType,
    ];
}
