using System.Globalization;
using DvdXporter.Core.Clips;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Preset node: a video and any number of audio tracks encoded with one of a few common sets of settings, chosen
/// from a list. The preset's values are written into the node when it's chosen (see <see cref="Settle"/>), so a project
/// keeps exporting the same however the presets change; the main ones show on the node to adjust.
/// </summary>
public sealed class PresetNode(CanvasNode node)
{
    public const string PresetKey = "preset";
    public const string PictureKey = "picture";
    public const string DoubleRateKey = "double-rate";
    public const string QualityKey = VideoCodecNode.QualityKey;
    public const string MaxBitrateKey = "max-bitrate";
    public const string SpeedKey = "speed";
    public const string SvtSpeedKey = VideoCodecNode.SvtPresetKey;
    public const string AudioModeKey = "audio-mode";
    public const string AudioBitrateKey = "audio-bitrate";

    // Written by the preset and not shown: the codecs and the rules for audio
    public const string VideoCodecKey = "video-codec";
    public const string PixelFormatKey = VideoCodecNode.PixelFormatKey;
    public const string KeepHdrKey = "keep-hdr";
    public const string FitHdKey = "fit-1080p";
    public const string AudioCodecKey = "audio-codec";
    public const string StereoKey = "stereo";
    public const string CopyDolbyKey = "copy-dolby";
    public const string NotAboveSourceKey = "not-above-source";
    // Which preset (and audio choice) the values were written for
    private const string AppliedVideoKey = "applied-video";
    private const string AppliedAudioKey = "applied-audio";

    public const string Streaming = "streaming";
    public const string Compatibility = "compatibility";
    public const string Mobile = "mobile";
    public const string Dvd = "dvd";

    public const string NoPicture = "none";
    public const string Deinterlace = "deinterlace";
    public const string InverseTelecine = "inverse-telecine";

    public const string StereoAudio = "stereo";
    public const string SurroundAudio = "surround";

    public const string VideoIn = "in";
    public const string VideoOut = "out";

    // E-AC-3 and AC-3 bitrates, for staying under a source's
    private static readonly int[] DolbyBitrates = [96, 128, 160, 192, 224, 256, 320, 384, 448];

    private static readonly IReadOnlyList<SettingChoice> X26xSpeeds =
        new[] { "ultrafast", "superfast", "veryfast", "faster", "fast", "medium", "slow", "slower", "veryslow" }
            .Select(p => new SettingChoice(p, char.ToUpperInvariant(p[0]) + p[1..])).ToList();

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Preset,
        Title = "Preset",
        Category = NodeCategory.Presets,
        Description = "Encodes a video and its audio tracks with a ready-made set of settings. Connect the video, and each "
            + "audio track to its own pair: another pair appears as the last is connected. The preset's values are kept in "
            + "the node, and the main ones can be adjusted here.",
        Ports = (n, context) => new PresetNode(n).Ports(context),
        Settle = n => new PresetNode(n).Settle(),
        Settings =
        [
            new(PresetKey, "Preset", SettingKind.Choice, Streaming,
            [
                new(Streaming, "Streaming"),
                new(Compatibility, "Compatibility"),
                new(Mobile, "Mobile"),
                new(Dvd, "DVD"),
            ], Tooltip: "Streaming: the original resolution in 10-bit H.265, HDR kept, E-AC-3 surround. For MKV.\n\n"
                + "Compatibility: at most 1080p in 8-bit H.264 (HDR tone mapped to SDR), AAC stereo. For MP4.\n\n"
                + "Mobile: at most 1080p in 10-bit AV1 (HDR tone mapped to SDR), AAC stereo. For MP4.\n\n"
                + "DVD: 8-bit H.264, with stereo AAC or surround E-AC-3. For MP4.\n\n"
                + "Choosing one sets the values below to its own."),
            new(PictureKey, "Picture", SettingKind.Choice, NoPicture,
            [
                new(NoPicture, "None"),
                new(Deinterlace, "Deinterlace"),
                new(InverseTelecine, "Inverse telecine"),
            ], Tooltip: "Deinterlace: for interlaced video (bwdif).\n\nInverse telecine: restores film's 23.976 fps frames "
                + "from hard-telecined 29.97 fps video."),
            new(DoubleRateKey, "Double rate", SettingKind.Toggle, "false", Visible: n => new PresetNode(n).Picture == Deinterlace,
                Tooltip: "A frame for each field (50 or 59.94 fps), which keeps video's smooth motion. Off: a frame for each frame."),
            new(QualityKey, "Quality (CRF)", SettingKind.Integer, Min: 0, Max: 63,
                Tooltip: "Lower is better quality and bigger files."),
            new(MaxBitrateKey, "Max bitrate (kbps)", SettingKind.Integer, Min: 0, Max: 200_000, Placeholder: "No limit",
                Visible: n => new PresetNode(n).VideoCodec != "libsvtav1",
                Tooltip: "The most any stretch of the video may use, so quality only gives way in the scenes that would go over."),
            new(SpeedKey, "Speed", SettingKind.Choice, "slower", X26xSpeeds, Visible: n => new PresetNode(n).VideoCodec != "libsvtav1",
                Tooltip: "Slower makes smaller files at the same quality."),
            new(SvtSpeedKey, "Speed", SettingKind.Integer, "3", Min: 0, Max: 13, Visible: n => new PresetNode(n).VideoCodec == "libsvtav1",
                Tooltip: "0 is slowest and best; 13 is fastest."),
            new(AudioModeKey, "Audio", SettingKind.Choice, StereoAudio,
                [new(StereoAudio, "Stereo (AAC)"), new(SurroundAudio, "Surround (E-AC-3)")],
                Visible: n => new PresetNode(n).Preset == Dvd),
            new(AudioBitrateKey, "Audio bitrate (kbps)", SettingKind.Integer, Min: 32, Max: 1536),

            new(VideoCodecKey, "Video codec", SettingKind.Text, Visible: _ => false),
            new(PixelFormatKey, "Bit depth", SettingKind.Text, Visible: _ => false),
            new(KeepHdrKey, "Keep HDR", SettingKind.Toggle, Visible: _ => false),
            new(FitHdKey, "At most 1080p", SettingKind.Toggle, Visible: _ => false),
            new(AudioCodecKey, "Audio codec", SettingKind.Text, Visible: _ => false),
            new(StereoKey, "Stereo", SettingKind.Toggle, Visible: _ => false),
            new(CopyDolbyKey, "Copy Dolby audio", SettingKind.Toggle, Visible: _ => false),
            new(NotAboveSourceKey, "Not above the source's bitrate", SettingKind.Toggle, Visible: _ => false),
        ],
    };

    public string Preset => node.Get(PresetKey, Streaming);

    /// <summary><see cref="NoPicture"/>, <see cref="Deinterlace"/> or <see cref="InverseTelecine"/>.</summary>
    public string Picture => node.Get(PictureKey, NoPicture);

    public bool DoubleRate => node.GetBool(DoubleRateKey);

    /// <summary>The video encoder, e.g. "libx265".</summary>
    public string VideoCodec => node.Get(VideoCodecKey, "libx264");

    public bool KeepsHdr => node.GetBool(KeepHdrKey);

    /// <summary>Whether bigger video is scaled down to fit 1920×1080.</summary>
    public bool FitsHd => node.GetBool(FitHdKey);

    /// <summary>The most the video's bitrate may reach, in kbps; null = no limit.</summary>
    public int? MaxBitrate => node.GetInt(MaxBitrateKey) is > 0 and var rate ? rate : null;

    public string AudioCodec => node.Get(AudioCodecKey, "aac");

    public int AudioBitrate => node.GetInt(AudioBitrateKey, 192);

    /// <summary>The container the preset is made for: "mkv" or "mp4".</summary>
    public string Container => Preset == Streaming ? "mkv" : "mp4";

    /// <summary>A line for the node saying which container suits it.</summary>
    public string ContainerHint => Container == "mkv" ? "Made for an MKV (Matroska) Output File." : "Made for an MP4 Output File.";

    // ---- Ports ----

    public static string AudioIn(int index) => $"a{index}";

    public static string AudioOut(int index) => $"a{index}-out";

    /// <summary>The audio pair an input port is (0 for "a0"), or null if it isn't one.</summary>
    public static int? AudioIndex(string inputPort) =>
        inputPort.Length > 1 && inputPort[0] == 'a' && int.TryParse(inputPort[1..], NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;

    /// <summary>The audio pair an output port is (0 for "a0-out"), or null if it isn't one.</summary>
    public static int? AudioOutIndex(string outputPort) =>
        outputPort.EndsWith("-out", StringComparison.Ordinal) ? AudioIndex(outputPort[..^4]) : null;

    /// <summary>The video's input and output, then an audio pair for every linked one and one to add another.</summary>
    public NodePorts Ports(ITranscodeContext context)
    {
        int pairs = 1;
        if (context is ICanvasContext canvasContext)
        {
            var linked = canvasContext.Canvas.Links.Where(l => l.ToNode == node.Id).Select(l => AudioIndex(l.ToPort)).OfType<int>().ToList();
            pairs = linked.Count > 0 ? linked.Max() + 2 : 1;
        }

        var inputs = new List<NodePort> { new(VideoIn, "Video", PortKind.Video) };
        var outputs = new List<NodePort> { new(VideoOut, "Video", PortKind.Video) };
        for (int i = 0; i < pairs; i++)
        {
            inputs.Add(new NodePort(AudioIn(i), $"Audio {i + 1}", PortKind.Audio));
            outputs.Add(new NodePort(AudioOut(i), $"Audio {i + 1}", PortKind.Audio));
        }

        return new NodePorts(inputs, outputs);
    }

    // ---- The presets' values ----

    /// <summary>
    /// Writes the chosen preset's values into the node, if they aren't already: its video values when the preset
    /// changes, its audio values when the preset or its audio choice does. Returns whether anything changed.
    /// </summary>
    public bool Settle()
    {
        var changed = false;
        if (node.Get(AppliedVideoKey) != Preset)
        {
            WriteVideo();
            node.Set(AppliedVideoKey, Preset);
            changed = true;
        }

        var audio = Preset == Dvd ? $"{Preset}/{node.Get(AudioModeKey, StereoAudio)}" : Preset;
        if (node.Get(AppliedAudioKey) != audio)
        {
            WriteAudio();
            node.Set(AppliedAudioKey, audio);
            changed = true;
        }

        return changed;
    }

    private void WriteVideo()
    {
        var (codec, pixelFormat, keepHdr, fitHd, crf, maxBitrate) = Preset switch
        {
            Compatibility => ("libx264", "yuv420p", false, true, 20, 8000),
            Mobile => ("libsvtav1", "yuv420p10le", false, true, 30, 0),
            Dvd => ("libx264", "yuv420p", false, false, 20, 0),
            _ => ("libx265", "yuv420p10le", true, false, 18, 20000),
        };
        node.Set(VideoCodecKey, codec);
        node.Set(PixelFormatKey, pixelFormat);
        node.Set(KeepHdrKey, keepHdr);
        node.Set(FitHdKey, fitHd);
        node.Set(QualityKey, crf.ToString(CultureInfo.InvariantCulture));
        node.Set(MaxBitrateKey, maxBitrate > 0 ? maxBitrate.ToString(CultureInfo.InvariantCulture) : "");
        node.Set(SpeedKey, "slower");
        node.Set(SvtSpeedKey, "3");
    }

    private void WriteAudio()
    {
        var surround = Preset == Streaming || (Preset == Dvd && node.Get(AudioModeKey, StereoAudio) == SurroundAudio);
        var bitrate = Preset switch
        {
            Mobile => 128,
            _ when surround => 448,
            _ => 192,
        };
        node.Set(AudioCodecKey, surround ? "eac3" : "aac");
        node.Set(AudioBitrateKey, bitrate.ToString(CultureInfo.InvariantCulture));
        node.Set(StereoKey, !surround);
        // Streaming copies Dolby audio that's already small enough, and never raises a bitrate
        node.Set(CopyDolbyKey, Preset == Streaming);
        node.Set(NotAboveSourceKey, Preset == Streaming);
    }

    // ---- Encoding ----

    /// <summary>
    /// The video's filters, in order: deinterlacing or inverse telecine, scaling down to fit 1080p, and tone mapping
    /// HDR to SDR, as each applies; empty if none do.
    /// </summary>
    public List<string> VideoFilters(PictureAnalysis? picture)
    {
        var filters = new List<string>();
        if (Picture == Deinterlace)
        {
            var deinterlace = new CanvasNode();
            deinterlace.Set(DeinterlaceNode.ModeKey, DoubleRate ? "send_field" : "send_frame");
            filters.Add(new DeinterlaceNode(deinterlace).Filter);
        }
        else if (Picture == InverseTelecine)
        {
            filters.Add(new InverseTelecineNode(new CanvasNode()).Filter);
        }

        // Within 1920×1080 keeping the shape, so 2.39:1 film stays 2.39:1; smaller video passes through as it is
        if (FitsHd)
            filters.Add("scale=w='min(1920,iw)':h='min(1080,ih)':force_original_aspect_ratio=decrease:force_divisible_by=2:flags=lanczos");
        if (TonesMap(picture))
            filters.Add(new DynamicRangeNode(new CanvasNode()).Filter);
        return filters;
    }

    /// <summary>Whether the video is tone mapped from HDR to SDR.</summary>
    public bool TonesMap(PictureAnalysis? picture) => !KeepsHdr && picture is not null && picture.IsHdr;

    /// <summary>The output options that encode output track <paramref name="track"/> as the preset's video.</summary>
    public List<string> VideoOptions(int track)
    {
        var codec = new CanvasNode();
        codec.Set(VideoCodecNode.CodecKey, VideoCodec);
        codec.Set(VideoCodecNode.QualityKey, node.Get(QualityKey));
        codec.Set(VideoCodecNode.PresetKey, node.Get(SpeedKey, "slower"));
        codec.Set(VideoCodecNode.SvtPresetKey, node.Get(SvtSpeedKey, "3"));
        codec.Set(VideoCodecNode.PixelFormatKey, node.Get(PixelFormatKey));
        var options = new VideoCodecNode(codec).Options(track);

        // A cap on the CRF's bitrate, over a buffer of two seconds' worth
        var maxBitrate = MaxBitrate;
        if (maxBitrate is not null && VideoCodec is "libx264" or "libx265")
            options.AddRange([$"-maxrate:{track}", $"{maxBitrate.Value}k", $"-bufsize:{track}", $"{maxBitrate.Value * 2}k"]);
        return options;
    }

    /// <summary>
    /// Whether a disc audio track is copied as it is: only by a preset that copies Dolby audio, for AC-3 or E-AC-3 at or
    /// under its bitrate, measured when the disc was read, and not filtered on the way.
    /// </summary>
    public bool CopiesAudio(ClipTrack? source, bool filtered) =>
        node.GetBool(CopyDolbyKey) && !filtered && source is not null && source.Codec is "AC3" or "AC3Plus"
        && source.Bitrate is not null && source.Bitrate.Value <= AudioBitrate * 1.02;

    /// <summary>The output options that encode output track <paramref name="track"/> as the preset's audio.</summary>
    /// <param name="source">The disc track it comes from, if it's straight from one; it may lower the bitrate.</param>
    public List<string> AudioOptions(int track, ClipTrack? source)
    {
        var bitrate = AudioBitrate;
        // Lossy audio below the bitrate stays at its own (to the next rate down); lossless is always above it
        if (node.GetBool(NotAboveSourceKey) && source?.Bitrate is not null && source.Bitrate.Value < bitrate)
            bitrate = AudioCodec == "eac3" ? DolbyBitrates.LastOrDefault(b => b <= source.Bitrate.Value, DolbyBitrates[0]) : source.Bitrate.Value;

        var options = new List<string> { $"-c:{track}", AudioCodec, $"-b:{track}", $"{bitrate}k" };
        // E-AC-3 holds up to 5.1: ffmpeg folds 7.1 into it, and leaves fewer channels as they are
        if (node.GetBool(StereoKey))
            options.AddRange([$"-ac:{track}", "2"]);
        if (source?.SampleRate is > 48000)
            options.AddRange([$"-ar:{track}", "48000"]);
        return options;
    }
}
