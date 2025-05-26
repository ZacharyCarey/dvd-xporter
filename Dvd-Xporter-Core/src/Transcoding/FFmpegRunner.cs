using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>How a run went, measured as it ran.</summary>
public sealed class FFmpegRunStats
{
    /// <summary>The whole run's wall-clock time.</summary>
    public TimeSpan Elapsed { get; internal set; }

    /// <summary>
    /// Media seconds made per wall-clock second once output started, so seeking and decoding up to the start
    /// (for a test from partway in) don't count. Null if too little was made to tell.
    /// </summary>
    public double? Speed { get; internal set; }
}

/// <summary>
/// Runs ffmpeg for the app (see <see cref="FFmpegProcess"/>), taking the app's times and filling in
/// <see cref="FFmpegRunStats"/>.
/// </summary>
public static class FFmpegRunner
{
    /// <summary>Stops every ffmpeg this runs, straight away (e.g. as the app closes, when there's no time to cancel).</summary>
    public static void StopAll() => FFmpegProcess.StopAll();

    /// <summary>The programs the app runs (ffmpeg, ffprobe) that can't be run: they aren't installed, or aren't on PATH.</summary>
    public static List<string> FindMissing() => new[] { "ffmpeg", "ffprobe" }.Where(p => FFmpegProcess.GetVersion(p) is null).ToList();

    /// <summary>
    /// Runs ffmpeg with <paramref name="args"/>. <paramref name="progress"/> gets 0–1 of <paramref name="duration"/>
    /// (from another thread). ffmpeg's messages go to <paramref name="logPath"/>.
    /// </summary>
    /// <returns>Null on success, otherwise what went wrong (the end of ffmpeg's log).</returns>
    /// <exception cref="OperationCanceledException">Cancelled; ffmpeg was stopped.</exception>
    public static async Task<string?> RunAsync(IReadOnlyList<string> args, PreciseTime? duration, string logPath,
        Action<double>? progress, CancellationToken cancellation, FFmpegRunStats? stats = null)
    {
        var result = await FFmpegProcess.RunAsync(args, duration.ToFFmpegTime(), logPath, progress, cancellation);
        if (stats is not null)
        {
            stats.Elapsed = result.Elapsed;
            stats.Speed = result.Speed;
        }

        return result.Error;
    }
}
