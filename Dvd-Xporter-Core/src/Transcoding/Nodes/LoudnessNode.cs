using System.Globalization;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Loudness node: brings the audio to a standard loudness (EBU R128). Measuring first, one gain is worked out from a
/// measurement (see <see cref="LoudnessMeasurer"/>); in one pass, loudnorm adjusts as it goes.
/// </summary>
public sealed class LoudnessNode(CanvasNode node)
{
    public const string TargetKey = "target";
    public const string PeakKey = "peak";
    public const string MethodKey = "method";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Loudness,
        Title = "Loudness",
        Category = NodeCategory.AudioFilters,
        Description = "Brings the audio to a standard loudness (EBU R128), so everything in the library plays at about "
            + "the same volume. Put it after any Downmix.",
        PassKind = PortKind.Audio,
        Settings =
        [
            new(TargetKey, "Target", SettingKind.Choice, "-23",
            [
                new("-23", "−23 LUFS (EBU R128, broadcast)"),
                new("-24", "−24 LUFS (ATSC, US TV)"),
                new("-27", "−27 LUFS (film: quiet, most dynamic)"),
                new("-16", "−16 LUFS (streaming, Apple)"),
                new("-14", "−14 LUFS (streaming, loud)"),
            ], Tooltip: "How loud on average. Lower is quieter with more room for loud moments."),
            new(PeakKey, "Peak limit", SettingKind.Choice, "-2", [new("-2", "−2 dBTP"), new("-1", "−1 dBTP")],
                Tooltip: "The loudest any moment may get, with headroom for lossy codecs."),
            new(MethodKey, "Method", SettingKind.Choice, "measure",
            [
                new("measure", "Measure first (keeps dynamics)"),
                new("dynamic", "One pass (evens out)"),
            ], Tooltip: "Measure first reads the audio once, then turns it up or down by one amount, so quiet and "
                + "loud scenes keep their difference. One pass adjusts as it goes: faster, but it squashes the dynamics a little."),
        ],
    };

    /// <summary>The loudness to reach, in LUFS, as ffmpeg takes it (e.g. "-23").</summary>
    public string Target => node.Get(TargetKey, "-23");

    /// <summary>The loudest any moment may get, in dBTP (e.g. "-2").</summary>
    public string Peak => node.Get(PeakKey, "-2");

    /// <summary>Whether the audio is measured first, keeping its dynamics, rather than evened out in one pass.</summary>
    public bool MeasuresFirst => node.Get(MethodKey, "measure") == "measure";

    public double TargetLoudness => double.Parse(Target, CultureInfo.InvariantCulture);

    public double PeakLimit => double.Parse(Peak, CultureInfo.InvariantCulture);

    /// <summary>
    /// The one-pass filter: EBU R128 loudnorm, adjusting as it goes (which works at 192 kHz, so the result goes back to
    /// 48 kHz). It's also what's used when there's no measurement.
    /// </summary>
    public string OnePassFilter => $"loudnorm=I={Target}:TP={Peak}:LRA=11,aresample=48000";
}
