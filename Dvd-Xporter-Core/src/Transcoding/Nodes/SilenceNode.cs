namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Silence node: silent audio, e.g. to go with solid colour frames.</summary>
public sealed class SilenceNode(CanvasNode node)
{
    public const string LayoutKey = "layout";
    public const string SampleRateKey = "sample-rate";
    public const string DurationKey = "duration";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Silence,
        Title = "Silence",
        Category = NodeCategory.Inputs,
        Description = "Silent audio, e.g. to go with solid colour frames.",
        Settings =
        [
            new(LayoutKey, "Channels", SettingKind.Choice, "stereo", [new("mono", "Mono"), new("stereo", "Stereo"), new("5.1", "5.1")]),
            new(SampleRateKey, "Sample rate", SettingKind.Choice, "48000", [new("44100", "44.1 kHz"), new("48000", "48 kHz")]),
            new(DurationKey, "Duration", SettingKind.Time, "5"),
        ],
        Ports = (_, _) => new NodePorts([], [new NodePort("out", "Audio", PortKind.Audio)]),
    };

    /// <summary>The channel layout, as ffmpeg names it: "mono", "stereo" or "5.1".</summary>
    public string Layout => node.Get(LayoutKey, "stereo");

    public string SampleRate => node.Get(SampleRateKey, "48000");

    public PreciseTime Duration => node.GetTime(DurationKey) ?? new PreciseTime(0, 0, 5, 0);

    /// <summary>The ffmpeg source filter that makes the silence.</summary>
    public string Graph => $"anullsrc=r={SampleRate}:cl={Layout}:d={Duration.ToFFmpegTime()}";
}
