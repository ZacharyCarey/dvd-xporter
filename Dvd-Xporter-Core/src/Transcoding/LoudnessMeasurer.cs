using System.Globalization;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>A Loudness node measuring first: what it measures (the audio going into it) and with which targets.</summary>
/// <param name="Token">Where the measured values go in the job's filters.</param>
/// <param name="Node">The node feeding the Loudness node, on the expanded canvas.</param>
/// <param name="Target">The loudness to reach (LUFS).</param>
/// <param name="Peak">The highest a peak may go (dBTP).</param>
/// <param name="Unmeasured">The filter to use if it can't be measured: loudnorm adjusting as it goes.</param>
public sealed record LoudnessMeasurement(string Token, int Node, string Port, double Target, double Peak, string Unmeasured);

/// <summary>Runs a job's loudness measurements (the first pass of two-pass loudnorm) before it encodes.</summary>
public static class LoudnessMeasurer
{
    // The limiter works on sample peaks; true peaks between samples can be a little higher
    private const double TruePeakMargin = 0.5;

    // Audio this quiet (or silent) is left alone rather than amplified hugely
    private const double MaximumGain = 30;

    /// <summary>
    /// Measures each of the job's Loudness inputs over <paramref name="range"/> (a test's stretch, or everything), and
    /// returns what goes in each token: one gain to the target, which keeps the mix's dynamics, and a limiter for the
    /// few peaks it would push over. (loudnorm's own "linear" mode only manages that with enough headroom, and
    /// otherwise quietly falls back to adjusting as it goes.)
    /// </summary>
    public static async Task<Dictionary<string, string>> MeasureAsync(TranscodeCanvas canvas, ICanvasContext context, IClipFiles files,
        TranscodeJob job, TestRange? range, string workFolder, CancellationToken cancellation)
    {
        var results = new Dictionary<string, string>();
        Directory.CreateDirectory(workFolder);
        foreach (var measurement in job.Measurements)
        {
            results[measurement.Token] = measurement.Unmeasured;
            var probe = TranscodeCompiler.CompileProbe(canvas, context, files, job.Output.Id, measurement.Node, measurement.Port,
                $"loudnorm=I={measurement.Target.ToString(CultureInfo.InvariantCulture)}:print_format=json", out _);
            if (probe is null)
                continue;

            var log = Path.Combine(workFolder, "loudness.log");
            var args = probe.BuildArguments(Path.Combine(workFolder, "loudness"), workFolder, range, writeFiles: true);
            if (await FFmpegRunner.RunAsync(args, range?.Length ?? job.Duration, log, null, cancellation) is not null)
                continue;

            var loudness = FFmpegLog.MeasuredLoudness(await File.ReadAllTextAsync(log, cancellation));
            // Silence (-inf) or no measurement is left to the fallback
            if (loudness is null)
                continue;

            var gain = Math.Min(MaximumGain, measurement.Target - loudness.Value);
            var limit = Math.Clamp(Math.Pow(10, (measurement.Peak - TruePeakMargin) / 20), 0.0625, 1);
            results[measurement.Token] = string.Create(CultureInfo.InvariantCulture,
                $"volume={gain:0.00}dB,alimiter=limit={limit:0.0000}:attack=5:release=50:level=0");
        }

        return results;
    }
}
