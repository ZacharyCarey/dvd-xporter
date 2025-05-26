using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>Finishes an Image Gallery's folder of images once ffmpeg has made them.</summary>
public static class GalleryImages
{
    /// <summary>
    /// Leaves out the pictures that are all black (with <see cref="TranscodeJob.SkipBlack"/>) and those on screen for
    /// less than <see cref="TranscodeJob.MinimumHold"/> (the frames of fades and slides between pictures), then
    /// numbers what's left 001, 002, ... again. Which are black, and each one's time on screen, come from what
    /// blackframe and showinfo logged as they were made (in <paramref name="logPath"/>). If the times don't match the
    /// images, none are dropped for being short.
    /// </summary>
    /// <returns>How many images there are.</returns>
    public static int Tidy(TranscodeJob job, string folder, string logPath)
    {
        var images = Directory.EnumerateFiles(folder, "*." + job.Extension).Order(StringComparer.Ordinal).ToList();
        if (!File.Exists(logPath) || images.Count == 0)
            return images.Count;

        var log = File.ReadAllLines(logPath);
        // blackframe counts the frames it sees from 0, which are the images in order
        var black = job.SkipBlack ? FFmpegLog.BlackFrames(log) : [];
        var times = FFmpegLog.ShownFrameTimes(log);
        var timed = job.MinimumHold is not null && times.Count == images.Count;
        var hold = job.MinimumHold?.ToFFmpegTime() ?? FFmpegTime.Zero;

        var duration = job.Duration?.ToFFmpegTime();
        var end = duration is not null && times.Count > 0 && duration.Value > times[^1] ? duration : null;
        var kept = new List<string>();
        for (int i = 0; i < images.Count; i++)
        {
            // The last one's held until the video ends (or kept, if that isn't known)
            var next = !timed ? null : i + 1 < times.Count ? times[i + 1] : end;
            var tooShort = next is not null && next.Value - times[i] < hold;
            if (black.Contains(i) || tooShort)
                File.Delete(images[i]);
            else
                kept.Add(images[i]);
        }

        // Renumbered in two steps, so no name is taken twice
        var temporary = kept.Select((path, i) =>
        {
            var to = Path.Combine(folder, $"renumber-{i:00000}.tmp");
            File.Move(path, to);
            return to;
        }).ToList();
        for (int i = 0; i < temporary.Count; i++)
            File.Move(temporary[i], Path.Combine(folder, $"{i + 1:000}.{job.Extension}"));
        return kept.Count;
    }
}
