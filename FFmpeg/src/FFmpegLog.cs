using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace FFmpeg;

/// <summary>A crop box, as cropdetect gives it.</summary>
public sealed record CropBox(int Width, int Height, int X, int Y);

/// <summary>What idet counted: frames by field order, and how often a field was repeated (3:2 pulldown).</summary>
public sealed record IdetCounts(long TopFieldFirst, long BottomFieldFirst, long Progressive, long Undetermined,
    long RepeatedNeither, long RepeatedTop, long RepeatedBottom);

/// <summary>One frame as idet saw it (logged by the metadata filter): its time, and whether it looked interlaced.</summary>
public readonly record struct IdetFrame(FFmpegTime Time, bool Interlaced);

/// <summary>Colour facts showinfo logs for a video: its transfer, and HDR mastering and content light levels.</summary>
/// <param name="Transfer">Its colour transfer, e.g. "smpte2084" (PQ) or "arib-std-b67" (HLG).</param>
/// <param name="MasteringPeak">The mastering display's peak brightness, in nits.</param>
/// <param name="MaxCll">The brightest pixel's brightness (MaxCLL), in nits.</param>
/// <param name="DolbyVision">Whether frames carry Dolby Vision metadata (RPU).</param>
public sealed record ColourInfo(string? Transfer, int? MasteringPeak, int? MaxCll, bool DolbyVision);

/// <summary>
/// Reads what ffmpeg's analysis filters print to its log (see <see cref="FFmpegProcess.RunAsync"/>'s log). Each reads
/// the last result in the text, which for filters that print totals when they close is the whole run's.
/// </summary>
public static partial class FFmpegLog
{
    [GeneratedRegex(@"crop=(\d+):(\d+):(\d+):(\d+)")]
    private static partial Regex CropRegex();

    [GeneratedRegex(@"\bs:(\d+)x(\d+)")]
    private static partial Regex SizeRegex();

    [GeneratedRegex(@"Multi frame detection: TFF:\s*(\d+)\s+BFF:\s*(\d+)\s+Progressive:\s*(\d+)\s+Undetermined:\s*(\d+)")]
    private static partial Regex FramesRegex();

    [GeneratedRegex(@"Repeated Fields: Neither:\s*(\d+)\s+Top:\s*(\d+)\s+Bottom:\s*(\d+)")]
    private static partial Regex RepeatsRegex();

    [GeneratedRegex(@"color_trc:(\S+)")]
    private static partial Regex TransferRegex();

    [GeneratedRegex(@"max_luminance=([\d.]+)")]
    private static partial Regex PeakRegex();

    [GeneratedRegex(@"MaxCLL=(\d+)")]
    private static partial Regex MaxCllRegex();

    [GeneratedRegex(@"\{[^{}]*""input_i""[^{}]*\}")]
    private static partial Regex LoudnormRegex();

    [GeneratedRegex(@"SSIM Y:[\d.]+ .*All:([\d.]+)")]
    private static partial Regex SsimRegex();

    [GeneratedRegex(@"Parsed_showinfo.*\bn:\s*\d+\s.*\bpts_time:\s*(-?[\d.]+)")]
    private static partial Regex ShownFrameRegex();

    [GeneratedRegex(@"Parsed_blackframe.*\bframe:\s*(\d+)\s")]
    private static partial Regex BlackFrameRegex();

    [GeneratedRegex(@"Parsed_metadata.*\bpts_time:\s*(-?[\d.]+)")]
    private static partial Regex MetadataFrameRegex();

    [GeneratedRegex(@"lavfi\.idet\.multiple\.current_frame=(\w+)")]
    private static partial Regex IdetVerdictRegex();

    /// <summary>The frame size showinfo logged first, or null.</summary>
    public static (int Width, int Height)? FrameSize(string log) =>
        SizeRegex().Match(log) is { Success: true } size ? (Int(size.Groups[1]), Int(size.Groups[2])) : null;

    /// <summary>cropdetect's last box (with reset=0, the box for everything it saw), or null.</summary>
    public static CropBox? Crop(string log)
    {
        var crop = CropRegex().Matches(log).LastOrDefault();
        return crop is not null ? new CropBox(Int(crop.Groups[1]), Int(crop.Groups[2]), Int(crop.Groups[3]), Int(crop.Groups[4])) : null;
    }

    /// <summary>idet's totals (printed as it closes); 0 for any it didn't print.</summary>
    public static IdetCounts Idet(string log)
    {
        var frames = FramesRegex().Matches(log).LastOrDefault();
        var repeats = RepeatsRegex().Matches(log).LastOrDefault();
        long Frame(int group) => frames is null ? 0 : Long(frames.Groups[group]);
        long Repeat(int group) => repeats is null ? 0 : Long(repeats.Groups[group]);
        return new IdetCounts(Frame(1), Frame(2), Frame(3), Frame(4), Repeat(1), Repeat(2), Repeat(3));
    }

    /// <summary>The colour facts showinfo logged (the first of each).</summary>
    public static ColourInfo Colour(string log) => new(
        TransferRegex().Match(log) is { Success: true } t ? t.Groups[1].Value : null,
        PeakRegex().Match(log) is { Success: true } p && double.TryParse(p.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var nits)
            ? (int)Math.Round(nits) : null,
        MaxCllRegex().Match(log) is { Success: true } c ? (int)Long(c.Groups[1]) : null,
        log.Contains("Dolby Vision RPU", StringComparison.Ordinal));

    /// <summary>
    /// The integrated loudness (LUFS) loudnorm measured (with print_format=json), or null if it printed none or the
    /// audio was silent (-inf).
    /// </summary>
    public static double? MeasuredLoudness(string log)
    {
        var match = LoudnormRegex().Matches(log).LastOrDefault();
        if (match is null)
            return null;
        using var json = JsonDocument.Parse(match.Value);
        return double.TryParse(json.RootElement.GetProperty("input_i").GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var loudness)
            && !double.IsInfinity(loudness)
                ? loudness
                : null;
    }

    /// <summary>The ssim filter's overall score (0–1), or null.</summary>
    public static double? Ssim(string log)
    {
        var match = SsimRegex().Matches(log).LastOrDefault();
        return match is not null ? double.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : null;
    }

    /// <summary>The pooled mean and worst frame from libvmaf's JSON log (log_fmt=json).</summary>
    /// <exception cref="JsonException">It isn't a libvmaf log.</exception>
    public static (double Mean, double Min) Vmaf(string json)
    {
        using var document = JsonDocument.Parse(json);
        var pooled = document.RootElement.GetProperty("pooled_metrics").GetProperty("vmaf");
        return (pooled.GetProperty("mean").GetDouble(), pooled.GetProperty("min").GetDouble());
    }

    /// <summary>Each frame showinfo logged, by its time (in the order logged).</summary>
    public static List<FFmpegTime> ShownFrameTimes(IEnumerable<string> lines) =>
        lines.Select(line => ShownFrameRegex().Match(line))
            .Where(m => m.Success && FFmpegTime.TryParse(m.Groups[1].Value, out _))
            .Select(m => { FFmpegTime.TryParse(m.Groups[1].Value, out var time); return time; })
            .ToList();

    /// <summary>The frames blackframe found black, by number (counted from 0 in the order it saw them).</summary>
    public static HashSet<int> BlackFrames(IEnumerable<string> lines) =>
        lines.Select(line => BlackFrameRegex().Match(line)).Where(m => m.Success).Select(m => Int(m.Groups[1])).ToHashSet();

    /// <summary>
    /// Each frame idet judged, as the metadata filter logged it (<c>idet,metadata=mode=print:key=lavfi.idet.multiple.current_frame</c>),
    /// in order. Read as it goes, as a whole film's is a long log.
    /// </summary>
    public static IEnumerable<IdetFrame> IdetFrames(IEnumerable<string> lines)
    {
        FFmpegTime? time = null;
        foreach (var line in lines)
        {
            if (MetadataFrameRegex().Match(line) is { Success: true } frame)
            {
                time = FFmpegTime.TryParse(frame.Groups[1].Value, out var parsed) ? parsed : null;
            }
            else if (time is not null && IdetVerdictRegex().Match(line) is { Success: true } verdict)
            {
                yield return new IdetFrame(time.Value, verdict.Groups[1].Value is "tff" or "bff");
                time = null;
            }
        }
    }

    private static int Int(Group group) => int.Parse(group.Value, CultureInfo.InvariantCulture);

    private static long Long(Group group) => long.Parse(group.Value, CultureInfo.InvariantCulture);
}
