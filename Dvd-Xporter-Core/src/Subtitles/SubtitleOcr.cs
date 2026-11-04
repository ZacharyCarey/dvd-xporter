using System.Globalization;
using System.Text.RegularExpressions;
using DiscDecoders;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Subtitles;

/// <summary>How far reading a track's text has got: reading the disc (a fraction of it), then each caption.</summary>
/// <param name="Captions">How many captions there are, once the disc's been read; until then null.</param>
public sealed record SubtitleOcrProgress(double DiscRead, int CaptionsRead, int? Captions);

/// <summary>
/// Turns a picture-based subtitle track into text: draws its captions off the disc, then reads each with
/// <see cref="Tesseract"/>, several at once.
/// </summary>
public static partial class SubtitleOcr
{
    // Shown this long if the stream doesn't say when the last caption goes
    private static readonly DiscTime LastCaptionLength = new(4 * DiscTime.TicksPerSecond);

    /// <summary>
    /// The OCR language for a track: its own (an ISO 639-2 code, e.g. "fre") if Tesseract has it, otherwise English,
    /// otherwise the first it has; null if it has none.
    /// </summary>
    public static string? ChooseLanguage(string? trackLanguage, IReadOnlyList<string> installed)
    {
        var language = trackLanguage is null ? null : Iso639.Language.Database.FirstOrDefault(l =>
            string.Equals(l.Part2, trackLanguage, StringComparison.OrdinalIgnoreCase)
#pragma warning disable CS0612 // Part2B: the "fre"/"ger" codes discs and Matroska use
            || string.Equals(l.Part2B, trackLanguage, StringComparison.OrdinalIgnoreCase)
#pragma warning restore CS0612
            || string.Equals(l.Part3, trackLanguage, StringComparison.OrdinalIgnoreCase));
        // Tesseract names its data by the 639-2/T code ("fra", "deu"), with variants for some ("chi_sim")
        var code = language?.Part2 ?? language?.Part3;
        return installed.FirstOrDefault(i => code is not null && (i == code || i.StartsWith(code + "_", StringComparison.Ordinal)))
            ?? installed.FirstOrDefault(i => i == "eng")
            ?? installed.FirstOrDefault();
    }

    /// <summary>
    /// Reads every caption of a clip's subtitle track off its disc, keeps their pictures (see
    /// <see cref="SubtitleTexts.PictureFolder"/>, replacing any there), and reads their text in <paramref name="language"/>.
    /// </summary>
    /// <param name="progress">Told as the disc is read, then as each caption is (from other threads).</param>
    /// <exception cref="TesseractException">Tesseract can't be run, or failed.</exception>
    /// <exception cref="IOException">The disc couldn't be read.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    public static async Task<SubtitleText> ReadAsync(DiscSource source, DiscCache cache, string clip, ClipTrack track, string language,
        IProgress<SubtitleOcrProgress>? progress, CancellationToken cancellation)
    {
        var palette = cache.LoadContents()?.FindClip(clip)?.SubtitlePalette?.Select(ParseRgb).ToList();
        var captions = await Task.Run(() =>
        {
            using var stream = new ProgressStream(source.OpenClip(clip), fraction => progress?.Report(new SubtitleOcrProgress(fraction, 0, null)));
            return Disc.ReadCaptions(source.Format, stream, track.StreamId, palette, cancellation);
        }, cancellation);
        progress?.Report(new SubtitleOcrProgress(1, 0, captions.Count));

        var folder = SubtitleTexts.PictureFolder(cache, clip, track.StreamId);
        if (Directory.Exists(folder))
            Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);

        // DVD captions are small: Tesseract reads text best a few times bigger
        int scale = source.Format == DiscFormat.Dvd ? 3 : 1;
        var lines = new SubtitleLine[captions.Count];
        int read = 0;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = cancellation };
        await Parallel.ForEachAsync(Enumerable.Range(0, captions.Count), options, async (i, token) =>
        {
            var caption = captions[i];
            var picture = PngWriter.Encode(caption.Width, caption.Height, caption.Rgba, rgba: true);
            await File.WriteAllBytesAsync(SubtitleTexts.PicturePath(cache, clip, track.StreamId, i + 1), picture, token);
            var text = await Tesseract.ReadAsync(ForOcr(caption, scale), language, token);
            lines[i] = new SubtitleLine
            {
                Start = caption.Start,
                End = caption.End ?? caption.Start + LastCaptionLength,
                Forced = caption.Forced,
                Text = Tidy(text),
                Picture = i + 1,
            };
            progress?.Report(new SubtitleOcrProgress(1, Interlocked.Increment(ref read), captions.Count));
        });

        return new SubtitleText { Language = language, Lines = [.. lines] };
    }

    /// <summary>
    /// A caption as Tesseract reads it best: black text on white with a margin, <paramref name="scale"/> times the size.
    /// Captions are light text with a dark outline, in any colour, so the text is what's brighter than the outline:
    /// how far each pixel is from the darkest towards the brightest, times how opaque it is.
    /// </summary>
    internal static byte[] ForOcr(CaptionPicture caption, int scale)
    {
        const int Margin = 16;
        int width = caption.Width, height = caption.Height;
        var luma = new double[width * height];
        var opacity = new double[width * height];
        double darkest = 255, brightest = 0;
        for (int i = 0; i < luma.Length; i++)
        {
            var rgba = caption.Rgba.AsSpan(i * 4, 4);
            luma[i] = 0.299 * rgba[0] + 0.587 * rgba[1] + 0.114 * rgba[2];
            opacity[i] = rgba[3] / 255.0;
            if (rgba[3] >= 128)
            {
                darkest = Math.Min(darkest, luma[i]);
                brightest = Math.Max(brightest, luma[i]);
            }
        }

        // One colour with no outline: the text is whatever isn't see-through
        bool outlined = brightest - darkest >= 48;
        var ink = new double[luma.Length];
        for (int i = 0; i < ink.Length; i++)
            ink[i] = opacity[i] * (outlined ? Math.Clamp((luma[i] - darkest) / (brightest - darkest), 0, 1) : 1);

        int outWidth = width * scale + Margin * 2, outHeight = height * scale + Margin * 2;
        var grey = new byte[outWidth * outHeight];
        Array.Fill(grey, (byte)255);
        for (int y = 0; y < height * scale; y++)
        {
            // Sampled between the source pixels, so scaled-up text keeps smooth edges
            double sourceY = Math.Clamp((y + 0.5) / scale - 0.5, 0, height - 1);
            int y0 = (int)sourceY, y1 = Math.Min(y0 + 1, height - 1);
            double fy = sourceY - y0;
            for (int x = 0; x < width * scale; x++)
            {
                double sourceX = Math.Clamp((x + 0.5) / scale - 0.5, 0, width - 1);
                int x0 = (int)sourceX, x1 = Math.Min(x0 + 1, width - 1);
                double fx = sourceX - x0;
                double value = (ink[y0 * width + x0] * (1 - fx) + ink[y0 * width + x1] * fx) * (1 - fy)
                    + (ink[y1 * width + x0] * (1 - fx) + ink[y1 * width + x1] * fx) * fy;
                grey[(y + Margin) * outWidth + x + Margin] = (byte)Math.Round(255 * (1 - value));
            }
        }

        return PngWriter.Encode(outWidth, outHeight, grey, rgba: false);
    }

    /// <summary>Tesseract's text tidied: its usual slips on subtitles put right, blank lines and stray spaces taken out.</summary>
    internal static string Tidy(string text) =>
        string.Join('\n', text
            // A capital I read as a bar
            .Replace('|', 'I')
            .Split('\n')
            .Select(l => MusicNotes(Spaces().Replace(l.Trim(), " ")))
            .Where(l => l.Length > 0));

    /// <summary>
    /// A sung line ("♪ Sippin' the soup ♪"): Tesseract has no music note, so it reads each as some stray mark ("2 Sippin'
    /// the soup &gt;"). A line between two lone marks that aren't words is taken to be one.
    /// </summary>
    private static string MusicNotes(string line)
    {
        var match = SungLine().Match(line);
        return match.Success && !IsWord(match.Groups[1].Value) && !IsWord(match.Groups[3].Value) ? $"♪ {match.Groups[2].Value} ♪" : line;

        static bool IsWord(string mark) => mark is "I" or "A" or "a" or "-";
    }

    [GeneratedRegex(" {2,}")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"^(\S) (.+) (\S)$")]
    private static partial Regex SungLine();

    // "rrggbb", as DiscContents keeps a DVD cell's subtitle palette
    private static Rgb ParseRgb(string hex) =>
        int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value)
            ? new Rgb((byte)(value >> 16), (byte)(value >> 8), (byte)value)
            : default;

    /// <summary>Reports how much of a stream has been read, where its length is known.</summary>
    private sealed class ProgressStream(Stream inner, Action<double> progress) : Stream
    {
        private long _read;
        private long _reported;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            _read += read;
            // About every 64 MB
            if (inner.CanSeek && inner.Length > 0 && _read - _reported >= 64 << 20)
            {
                _reported = _read;
                progress(Math.Min(1, (double)_read / inner.Length));
            }

            return read;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
