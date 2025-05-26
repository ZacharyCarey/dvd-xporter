using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <param name="Label">What the track is, e.g. "Video · H.264".</param>
/// <param name="Bytes">Its size in the test.</param>
public sealed record TrackSize(string Label, long Bytes);

/// <param name="Metric">"VMAF" or "SSIM".</param>
/// <param name="Mean">The average over the test: VMAF 0–100, or SSIM 0–1.</param>
/// <param name="Worst">The worst frame (VMAF), or null.</param>
public sealed record QualityScore(string Metric, double Mean, double? Worst)
{
    /// <summary>A rough reading of the score, e.g. "excellent".</summary>
    public string Verdict => Metric == "VMAF"
        ? Mean >= 95 ? "visually lossless" : Mean >= 90 ? "excellent" : Mean >= 80 ? "good" : Mean >= 70 ? "fair" : "poor"
        : Mean >= 0.99 ? "visually lossless" : Mean >= 0.98 ? "excellent" : Mean >= 0.95 ? "good" : Mean >= 0.90 ? "fair" : "poor";

    public override string ToString() => Metric == "VMAF"
        ? $"VMAF {Mean:0.0}{(Worst is not null ? $" (worst frame {Worst.Value:0})" : "")}, {Verdict}"
        : $"SSIM {Mean:0.000}, {Verdict}";
}

/// <summary>What a test encode says about the full encode: its size, how long it'll take, and optionally its quality.</summary>
/// <param name="SampleLength">How long the test actually came out.</param>
/// <param name="FullLength">How long the full output will be, if known.</param>
/// <param name="Tracks">Each track's estimated full size (or test size, if the full length isn't known).</param>
/// <param name="Speed">Encoding speed, as media seconds per second.</param>
public sealed record TestReport(PreciseTime SampleLength, long SampleBytes, PreciseTime? FullLength, IReadOnlyList<TrackSize> Tracks,
    double? Speed, QualityScore? Quality)
{
    private double Scale => FullLength is not null && SampleLength > PreciseTime.Zero ? FullLength.Value.Nanoseconds / (double)SampleLength.Nanoseconds : 1;

    /// <summary>The full output's size, scaled up from the test's.</summary>
    public long EstimatedBytes => (long)(SampleBytes * Scale);

    /// <summary>The full encode's time at the test's speed.</summary>
    public TimeSpan? EstimatedTime => FullLength is not null && Speed is > 0 and var speed ? TimeSpan.FromSeconds(FullLength.Value.AsTimeSpan().TotalSeconds / speed) : null;

    /// <summary>The average bitrate, in bits per second.</summary>
    public double Bitrate => SampleLength > PreciseTime.Zero ? SampleBytes * 8 / SampleLength.AsTimeSpan().TotalSeconds : 0;
}

/// <summary>Measures a finished test encode: each track's size, and (optionally) its picture quality.</summary>
public static class TestReports
{
    private static bool? _hasVmaf;

    /// <summary>Whether this ffmpeg can measure VMAF (some builds leave libvmaf out; SSIM is used then).</summary>
    public static bool HasVmaf() => _hasVmaf ??= FFmpegProcess.HasFilter("libvmaf");

    /// <summary>The test's length and each track's size, from its packets (ffprobe).</summary>
    public static async Task<TestReport> MeasureAsync(TranscodeJob job, string testFile, FFmpegRunStats stats, CancellationToken cancellation)
    {
        var probed = await FFprobe.ReadPacketSizesAsync(testFile, cancellation);
        var duration = probed.Duration.TotalSeconds;
        var sample = probed.Duration.ToPreciseTime();
        double scale = job.Duration is not null && duration > 0 ? job.Duration.Value.AsTimeSpan().TotalSeconds / duration : 1;
        var tracks = job.Tracks
            .Select((track, i) => new TrackSize($"{NodeType.KindName(track.Kind)} · {(track.Codec == "copy" ? "copied" : track.Codec)}",
                (long)(probed.StreamBytes.GetValueOrDefault(i) * scale)))
            .ToList();
        return new TestReport(sample, probed.Size, job.Duration, tracks, stats.Speed, null);
    }

    /// <summary>
    /// Compares the test's encoded video with the video that went into its codec, over the same stretch: VMAF if
    /// ffmpeg has it, otherwise SSIM. Null if the output has no encoded video (copied video loses nothing).
    /// </summary>
    public static async Task<QualityScore?> MeasureQualityAsync(TranscodeCanvas canvas, ICanvasContext context, IClipFiles files,
        TranscodeJob job, string testFile, TestRange range, string workFolder, CancellationToken cancellation)
    {
        if (job.QualityReference is not var (outputIndex, node, port))
            return null;

        Directory.CreateDirectory(workFolder);
        var vmaf = HasVmaf();
        var log = Path.Combine(workFolder, "quality.json");
        File.Delete(log);
        // Both sides in one format, their frames numbered 0, 1, 2... so they pair up by order. (Pairing by time goes wrong:
        // Matroska stores times to the millisecond, so a 23.976 fps encode's times are a little off the source's.)
        const string Pairing = "settb=1,setpts=N,format=yuv420p10le";
        var distorted = $"movie={FFmpegFilters.EscapeValue(testFile)}:si={outputIndex},{Pairing}";
        var metric = vmaf
            ? $"libvmaf=n_threads={Environment.ProcessorCount}:shortest=1:log_fmt=json:log_path={FFmpegFilters.EscapeValue(log)}"
            : "ssim=shortest=1";
        var filter = $"{Pairing}[ref];{distorted}[dist];[dist][ref]{metric}";

        var probe = TranscodeCompiler.CompileProbe(canvas, context, files, job.Output.Id, node, port, filter, out _);
        if (probe is null)
            return null;

        var ffmpegLog = Path.Combine(workFolder, "quality.log");
        var args = probe.BuildArguments(Path.Combine(workFolder, "quality"), workFolder, range, writeFiles: true);
        if (await FFmpegRunner.RunAsync(args, range.Length, ffmpegLog, null, cancellation) is not null)
            return null;

        if (vmaf)
        {
            if (!File.Exists(log))
                return null;
            var (mean, min) = FFmpegLog.Vmaf(await File.ReadAllTextAsync(log, cancellation));
            return new QualityScore("VMAF", mean, min);
        }

        var text = File.Exists(ffmpegLog) ? await File.ReadAllTextAsync(ffmpegLog, cancellation) : "";
        var ssim = FFmpegLog.Ssim(text);
        return ssim is not null ? new QualityScore("SSIM", ssim.Value, null) : null;
    }
}
