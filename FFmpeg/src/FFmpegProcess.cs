using System.Diagnostics;
using System.Globalization;

namespace FFmpeg;

/// <summary>How an ffmpeg run went.</summary>
/// <param name="Error">Null on success, otherwise what went wrong (the end of ffmpeg's log).</param>
/// <param name="Elapsed">The whole run's wall-clock time.</param>
/// <param name="Speed">
/// Media seconds made per wall-clock second once output started, so seeking and decoding up to the start (for a run
/// from partway in) don't count. Null if too little was made to tell.
/// </param>
public sealed record FFmpegResult(string? Error, TimeSpan Elapsed, double? Speed)
{
    public bool Succeeded => Error is null;
}

/// <summary>Runs ffmpeg (and ffprobe), reporting progress and keeping a log, and can stop them.</summary>
public static class FFmpegProcess
{
    // How many lines from the end of the log to show when ffmpeg fails
    private const int ErrorLines = 6;

    // Every ffmpeg running, so they can all be stopped at once
    private static readonly HashSet<Process> Running = [];

    /// <summary>Stops every ffmpeg this runs, straight away (e.g. as an app closes, when there's no time to cancel).</summary>
    public static void StopAll()
    {
        lock (Running)
        {
            foreach (var process in Running)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }
    }

    /// <summary>
    /// Runs ffmpeg with <paramref name="args"/>. <paramref name="progress"/> gets 0–1 of <paramref name="duration"/>
    /// (from another thread). The command and ffmpeg's messages go to <paramref name="logPath"/>.
    /// </summary>
    /// <exception cref="OperationCanceledException">Cancelled; ffmpeg was stopped.</exception>
    public static async Task<FFmpegResult> RunAsync(IReadOnlyList<string> args, FFmpegTime? duration, string logPath,
        Action<double>? progress, CancellationToken cancellation)
    {
        var clock = Stopwatch.StartNew();
        // The first and latest progress reports once output began: (wall seconds, media seconds)
        (double Wall, double Media)? first = null, last = null;
        var info = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // Progress four times a second, which also times short runs well enough to tell their speed
        foreach (var arg in new[] { "-nostdin", "-y", "-nostats", "-progress", "pipe:1", "-stats_period", "0.25" }.Concat(args))
            info.ArgumentList.Add(arg);

        Directory.CreateDirectory(Path.GetDirectoryName(logPath)!);
        await using var log = new StreamWriter(logPath);
        await log.WriteLineAsync(FFmpegArguments.ToCommandLine(args));
        await log.WriteLineAsync();

        Process process;
        try
        {
            process = Process.Start(info)!;
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            return new FFmpegResult($"ffmpeg couldn't be started ({ex.Message}). Is it installed?", clock.Elapsed, null);
        }

        lock (Running)
            Running.Add(process);
        using (process)
        try
        {
            var tail = new Queue<string>();
            var errors = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardError.ReadLineAsync()) is not null)
                {
                    lock (log)
                        log.WriteLine(line);
                    lock (tail)
                    {
                        tail.Enqueue(line);
                        if (tail.Count > ErrorLines)
                            tail.Dequeue();
                    }
                }
            });

            double total = duration?.Microseconds ?? 0;
            var output = Task.Run(async () =>
            {
                string? line;
                while ((line = await process.StandardOutput.ReadLineAsync()) is not null)
                {
                    if (line.StartsWith("out_time_us=", StringComparison.Ordinal)
                        && long.TryParse(line["out_time_us=".Length..], NumberStyles.Integer, CultureInfo.InvariantCulture, out var us))
                    {
                        if (total > 0)
                            progress?.Invoke(Math.Clamp(us / total, 0, 1));
                        if (us > 0)
                        {
                            last = (clock.Elapsed.TotalSeconds, us / 1e6);
                            first ??= last;
                        }
                    }
                }
            });

            try
            {
                await process.WaitForExitAsync(cancellation);
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }

                // Let the readers finish with the log before it's closed
                await Task.WhenAll(errors, output);
                throw;
            }

            await Task.WhenAll(errors, output);
            // From the first output on; a run too short for that is timed whole (start-up and all)
            var speed = first is not null && last is not null && last.Value.Wall - first.Value.Wall > 0.5 && last.Value.Media > first.Value.Media
                ? (last.Value.Media - first.Value.Media) / (last.Value.Wall - first.Value.Wall)
                : last is not null && clock.Elapsed.TotalSeconds > 0 ? last.Value.Media / clock.Elapsed.TotalSeconds : (double?)null;

            if (process.ExitCode == 0)
            {
                progress?.Invoke(1);
                return new FFmpegResult(null, clock.Elapsed, speed);
            }

            lock (tail)
                return new FFmpegResult(string.Join("\n", tail), clock.Elapsed, speed);
        }
        finally
        {
            lock (Running)
                Running.Remove(process);
        }
    }

    /// <summary>
    /// The first line <paramref name="program"/> (ffmpeg or ffprobe) prints for -version ("ffmpeg version 7.1 ..."), or
    /// null if it couldn't be run: it isn't installed, or isn't on PATH.
    /// </summary>
    public static string? GetVersion(string program)
    {
        try
        {
            var info = new ProcessStartInfo(program) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            info.ArgumentList.Add("-version");
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEndAsync();
            // Something answering to the name that doesn't finish straight away isn't ffmpeg
            if (!process.WaitForExit(5000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }

            return process.ExitCode == 0 ? output.Result.Split('\n')[0].Trim() : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>Whether this ffmpeg has a filter (some builds leave some out, e.g. libvmaf).</summary>
    public static bool HasFilter(string name)
    {
        try
        {
            var info = new ProcessStartInfo("ffmpeg") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var arg in new[] { "-hide_banner", "-filters" })
                info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit();
            return output.Split('\n').Any(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries).ElementAtOrDefault(1) == name);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }
}
