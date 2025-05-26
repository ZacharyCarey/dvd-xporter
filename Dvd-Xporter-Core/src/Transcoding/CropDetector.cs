using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <param name="Width">The picture's width without the bars.</param>
/// <param name="FrameWidth">The whole frame's width.</param>
/// <param name="Samples">How many of the samples had picture to measure (all-black ones are left out).</param>
public sealed record CropResult(int Width, int Height, int X, int Y, int FrameWidth, int FrameHeight, int Samples)
{
    public bool HasBars => Width < FrameWidth || Height < FrameHeight;
}

/// <summary>
/// Finds the black bars around a video's picture with ffmpeg's cropdetect, from short samples spread across it
/// (decoding all of a film would take minutes).
/// </summary>
public static class CropDetector
{
    // cropdetect keeps the biggest box it's seen (reset=0), rounded to even sizes for 4:2:0 video. It skips each
    // sample's first 60 frames (2-2.5 s): a seek lands mid-GOP, and some decoders (VC-1) put out broken frames
    // until the next keyframe, which look like picture in the bars. showinfo logs the frame size (on every frame:
    // ffmpeg ends the sample by counting frames out, so they can't be dropped).
    // The black threshold is a fraction (24/255) so it scales to 10-bit video, where black is 64 rather than 16.
    public const string Filter = "cropdetect=limit=0.094:round=2:reset=0:skip=60,showinfo";

    private const int SampleCount = 8;
    private static readonly PreciseTime SampleLength = new(0, 0, 4, 0);

    // A sample whose box is smaller than this share of the frame was mostly black (a fade or a dark title)
    private const double MinimumShare = 0.2;

    /// <summary>
    /// Samples the probe's video (see <see cref="TranscodeCompiler.CompileProbe"/> with <see cref="Filter"/>) and
    /// returns the box holding the picture in all of them, or null if no sample had picture.
    /// </summary>
    /// <param name="progress">0–1, as samples finish.</param>
    public static async Task<CropResult?> DetectAsync(TranscodeJob probe, string workFolder, Action<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workFolder);
        var duration = probe.Duration ?? PreciseTime.Zero;
        var starts = duration > SampleLength + SampleLength
            ? Enumerable.Range(1, SampleCount).Select(i => new PreciseTime((duration - SampleLength).Nanoseconds * i / (SampleCount + 1))).ToList()
            : [PreciseTime.Zero];

        int left = int.MaxValue, top = int.MaxValue, right = int.MinValue, bottom = int.MinValue, samples = 0;
        int frameWidth = 0, frameHeight = 0;
        for (int i = 0; i < starts.Count; i++)
        {
            var output = Path.Combine(workFolder, "cropdetect");
            var log = Path.Combine(workFolder, "cropdetect.log");
            var args = probe.BuildArguments(output, workFolder, new TestRange(starts[i], SampleLength), writeFiles: true);
            await FFmpegRunner.RunAsync(args, SampleLength, log, null, cancellation);
            progress?.Invoke((i + 1.0) / starts.Count);

            var text = File.Exists(log) ? await File.ReadAllTextAsync(log, cancellation) : "";
            var size = FFmpegLog.FrameSize(text);
            if (size is not null)
                (frameWidth, frameHeight) = size.Value;

            var crop = FFmpegLog.Crop(text);
            // The last line has the box for the whole sample
            if (crop is null || frameWidth == 0)
                continue;
            var (w, h, x, y) = (crop.Width, crop.Height, crop.X, crop.Y);
            if (w <= 0 || h <= 0 || (double)w * h < MinimumShare * frameWidth * frameHeight)
                continue;

            left = Math.Min(left, x);
            top = Math.Min(top, y);
            right = Math.Max(right, x + w);
            bottom = Math.Max(bottom, y + h);
            samples++;
        }

        if (samples == 0)
            return null;

        // Even sizes and offsets, as 4:2:0 video needs
        left -= left % 2;
        top -= top % 2;
        int width = Math.Min(frameWidth - left, (right - left) / 2 * 2);
        int height = Math.Min(frameHeight - top, (bottom - top) / 2 * 2);
        return new CropResult(width, height, left, top, frameWidth, frameHeight, samples);
    }

}
