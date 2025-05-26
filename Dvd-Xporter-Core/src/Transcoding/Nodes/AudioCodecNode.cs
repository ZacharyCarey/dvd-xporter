namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>An Audio Codec node: encodes audio with AAC, Opus, AC-3, E-AC-3, FLAC or MP3.</summary>
public sealed class AudioCodecNode(CanvasNode node)
{
    public const string CodecKey = "codec";
    public const string BitrateKey = "bitrate";
    public const string ChannelsKey = "channels";
    public const string SampleRateKey = "sample-rate";
    public const string ExtraKey = "extra";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.AudioCodec,
        Title = "Audio Codec",
        Category = NodeCategory.Codecs,
        Description = "Encodes audio. Tracks that reach an output without a codec are copied as they are.",
        PassKind = PortKind.Audio,
        Settings =
        [
            new(CodecKey, "Codec", SettingKind.Choice, "aac",
            [
                new("aac", "AAC"),
                new("libopus", "Opus"),
                new("ac3", "Dolby Digital (AC-3)"),
                new("eac3", "Dolby Digital Plus (E-AC-3)"),
                new("flac", "FLAC (lossless)"),
                new("libmp3lame", "MP3"),
            ]),
            new(BitrateKey, "Bitrate (kbps)", SettingKind.Integer, Min: 8, Max: 1536, Placeholder: "Auto",
                Visible: n => new AudioCodecNode(n).Codec != "flac"),
            new(ChannelsKey, "Channels", SettingKind.Choice, "",
            [
                new("", "Same as source"),
                new("1", "Mono"),
                new("2", "Stereo"),
                new("6", "5.1"),
                new("8", "7.1"),
            ]),
            new(SampleRateKey, "Sample rate", SettingKind.Choice, "",
            [
                new("", "Same as source"),
                new("44100", "44.1 kHz"),
                new("48000", "48 kHz"),
                new("96000", "96 kHz"),
            ]),
            new(ExtraKey, "Extra options", SettingKind.Text, Placeholder: "e.g. -vbr on"),
        ],
    };

    /// <summary>ffmpeg's encoder, e.g. "aac" or "libopus".</summary>
    public string Codec => node.Get(CodecKey, "aac");

    /// <summary>The bitrate in kbps; null = the encoder's choice.</summary>
    public int? Bitrate => node.GetInt(BitrateKey) is > 0 and var bitrate ? bitrate : null;

    /// <summary>How many channels; empty = the source's.</summary>
    public string Channels => node.Get(ChannelsKey);

    /// <summary>The sample rate in Hz; empty = the source's.</summary>
    public string SampleRate => node.Get(SampleRateKey);

    public string Extra => node.Get(ExtraKey);

    /// <summary>The output options that encode output track <paramref name="track"/>.</summary>
    public List<string> Options(int track)
    {
        var options = new List<string> { $"-c:{track}", Codec };
        if (Codec != "flac" && Bitrate is not null)
            options.AddRange([$"-b:{track}", $"{Bitrate.Value}k"]);
        if (Channels is { Length: > 0 } channels)
            options.AddRange([$"-ac:{track}", channels]);
        if (SampleRate is { Length: > 0 } rate)
            options.AddRange([$"-ar:{track}", rate]);
        options.AddRange(CodecOptions.Extra(Extra, track));
        return options;
    }
}
