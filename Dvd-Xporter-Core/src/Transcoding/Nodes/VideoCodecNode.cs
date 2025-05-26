using System.Globalization;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Video Codec node: encodes video with H.264, H.265, AV1 or FFV1.</summary>
public sealed class VideoCodecNode(CanvasNode node)
{
    public const string CodecKey = "codec";
    public const string QualityKey = "crf";
    public const string PresetKey = "preset";
    public const string SvtPresetKey = "svt-preset";
    public const string TuneKey = "tune";
    public const string PixelFormatKey = "pixel-format";
    public const string ExtraKey = "extra";

    private static readonly IReadOnlyList<SettingChoice> X264Presets =
        new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow", "placebo" }
            .Select(p => new SettingChoice(p, char.ToUpperInvariant(p[0]) + p[1..])).ToList();

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.VideoCodec,
        Title = "Video Codec",
        Category = NodeCategory.Codecs,
        Description = "Encodes video. Tracks that reach an output without a codec are copied as they are.",
        PassKind = PortKind.Video,
        Settings =
        [
            new(CodecKey, "Codec", SettingKind.Choice, "libx264",
            [
                new("libx264", "H.264 (libx264)"),
                new("libx265", "H.265 / HEVC (libx265)"),
                new("libsvtav1", "AV1 (SVT-AV1)"),
                new("ffv1", "FFV1 (lossless)"),
            ]),
            new(QualityKey, "Quality (CRF)", SettingKind.Integer, Min: 0, Max: 63, Visible: n => new VideoCodecNode(n).Codec != "ffv1",
                Placeholder: "Default",
                Tooltip: "Lower is better quality and bigger files. Defaults: 23 for H.264, 28 for H.265, 35 for AV1."),
            new(PresetKey, "Preset", SettingKind.Choice, "medium", X264Presets, Visible: n => new VideoCodecNode(n).IsX26x,
                Tooltip: "Slower presets make smaller files at the same quality."),
            new(SvtPresetKey, "Preset", SettingKind.Integer, "6", Min: 0, Max: 13, Visible: n => n.Get(CodecKey) == "libsvtav1",
                Tooltip: "0 is slowest and best; 13 is fastest."),
            new(TuneKey, "Tune", SettingKind.Choice, "", Visible: n => new VideoCodecNode(n).IsX26x, Choices:
            [
                new("", "None"),
                new("film", "Film"),
                new("animation", "Animation"),
                new("grain", "Grain"),
                new("stillimage", "Still image"),
            ], Tooltip: "H.265 has no Film or Still image tuning; they're left out for it."),
            new(PixelFormatKey, "Bit depth", SettingKind.Choice, "", Visible: n => new VideoCodecNode(n).Codec != "ffv1", Choices:
            [
                new("", "Same as source"),
                new("yuv420p", "8-bit"),
                new("yuv420p10le", "10-bit"),
            ]),
            new(ExtraKey, "Extra options", SettingKind.Text, Placeholder: "e.g. -x264-params ref=6",
                Tooltip: "Passed to ffmpeg for this track, after the options above."),
        ],
    };

    /// <summary>ffmpeg's encoder: "libx264", "libx265", "libsvtav1" or "ffv1".</summary>
    public string Codec => node.Get(CodecKey, "libx264");

    public bool IsX26x => Codec is "libx264" or "libx265";

    /// <summary>The quality (CRF) set; null = the encoder's default (see <see cref="Quality"/>).</summary>
    public int? Crf => node.Get(QualityKey) is { Length: > 0 } ? node.GetInt(QualityKey) : null;

    /// <summary>The quality (CRF) it encodes at: the one set, or the encoder's default.</summary>
    public int Quality => Crf ?? Codec switch
    {
        "libx265" => 28,
        "libsvtav1" => 35,
        _ => 23,
    };

    /// <summary>The x264 or x265 preset, e.g. "medium".</summary>
    public string Preset => node.Get(PresetKey, "medium");

    /// <summary>SVT-AV1's preset, 0 (slowest) to 13 (fastest).</summary>
    public int SvtPreset => Math.Clamp(node.GetInt(SvtPresetKey, 6), 0, 13);

    /// <summary>The x264 or x265 tuning; empty = none.</summary>
    public string Tune => node.Get(TuneKey);

    /// <summary>The pixel format, which sets the bit depth ("yuv420p" 8-bit, "yuv420p10le" 10-bit); empty = the source's.</summary>
    public string PixelFormat => node.Get(PixelFormatKey);

    public string Extra => node.Get(ExtraKey);

    /// <summary>The output options that encode output track <paramref name="track"/>.</summary>
    public List<string> Options(int track)
    {
        var options = new List<string> { $"-c:{track}", Codec };
        if (Codec != "ffv1")
            options.AddRange([$"-crf:{track}", Quality.ToString(CultureInfo.InvariantCulture)]);

        if (IsX26x)
        {
            options.AddRange([$"-preset:{track}", Preset]);
            if (Tune is { Length: > 0 } tune && !(Codec == "libx265" && tune is "film" or "stillimage"))
                options.AddRange([$"-tune:{track}", tune]);
        }
        else if (Codec == "libsvtav1")
        {
            options.AddRange([$"-preset:{track}", SvtPreset.ToString(CultureInfo.InvariantCulture)]);
        }
        else if (Codec == "ffv1")
        {
            options.AddRange([$"-level:{track}", "3"]);
        }

        if (Codec != "ffv1" && PixelFormat is { Length: > 0 } pixelFormat)
            options.AddRange([$"-pix_fmt:{track}", pixelFormat]);
        options.AddRange(CodecOptions.Extra(Extra, track));
        return options;
    }
}
