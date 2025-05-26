using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>How a video's frames are made up, which decides whether it needs deinterlacing or inverse telecine.</summary>
public enum PictureType
{
    /// <summary>Whole frames: nothing to do.</summary>
    Progressive,

    /// <summary>Film stored at 23.976 fps with flags telling players to telecine it (soft telecine): read as film, nothing to do.</summary>
    Film,

    /// <summary>Film converted to 29.97 fps by repeating fields (hard telecine): Inverse Telecine restores it.</summary>
    Telecined,

    /// <summary>Interlaced video, shot or made as fields: Deinterlace.</summary>
    Interlaced,

    /// <summary>Partly interlaced (e.g. interlaced credits on progressive video): Deinterlace frames marked interlaced.</summary>
    Mixed,

    /// <summary>Not checked: the disc was added without checking every frame.</summary>
    Unknown,
}

/// <summary>A stretch of a clip whose frames looked interlaced, timed from the clip's start.</summary>
/// <param name="Frames">How many of its frames looked interlaced.</param>
public sealed record InterlacedRange(PreciseTime Start, PreciseTime End, int Frames);

/// <summary>Whether a video is high dynamic range, and which kind.</summary>
public enum DynamicRange
{
    Sdr,
    /// <summary>PQ (SMPTE 2084) with static metadata: UHD Blu-ray's HDR, and Dolby Vision's base layer.</summary>
    Hdr10,
    /// <summary>Hybrid log-gamma, as broadcast HDR uses.</summary>
    Hlg,
}

/// <param name="TopFieldFirst">For interlaced video, its field order (null if progressive or unclear).</param>
/// <param name="InterlacedShare">The share of sampled frames that looked interlaced.</param>
/// <param name="RepeatedShare">The share of sampled frames with a repeated field, the mark of telecine.</param>
/// <param name="PeakNits">The brightness it was mastered for (HDR10's mastering display), if given.</param>
/// <param name="MaxCll">Its brightest pixel (content light level), if given.</param>
/// <param name="DolbyVision">Whether it has Dolby Vision: UHD Blu-ray's second, enhancement layer, or metadata in the video.</param>
/// <param name="InterlacedRanges">Where its frames looked interlaced, for a clip whose every frame was checked.</param>
public sealed record PictureAnalysis(PictureType Type, bool? TopFieldFirst, double InterlacedShare, double RepeatedShare, int Frames,
    DynamicRange Range = DynamicRange.Sdr, int? PeakNits = null, int? MaxCll = null, bool DolbyVision = false,
    IReadOnlyList<InterlacedRange>? InterlacedRanges = null)
{
    public bool IsHdr => Range != DynamicRange.Sdr;

    /// <summary>
    /// Several clips' pictures as one video, e.g. a playlist's: their type if they all have the same one (progressive
    /// and film count as one, whichever has more frames), or else mixed; unknown if any wasn't checked. Its dynamic range
    /// is that of the clip with the most frames. Null if there are none.
    /// </summary>
    public static PictureAnalysis? Combine(IReadOnlyList<PictureAnalysis> clips)
    {
        if (clips.Count <= 1)
            return clips.FirstOrDefault();

        int frames = clips.Sum(c => c.Frames);
        var main = clips.MaxBy(c => c.Frames)!;
        var whole = clips.Where(c => c.Type is PictureType.Progressive or PictureType.Film).ToList();
        PictureType type;
        if (clips.Any(c => c.Type == PictureType.Unknown))
            type = PictureType.Unknown;
        else if (whole.Count == clips.Count)
            type = whole.GroupBy(c => c.Type).MaxBy(g => g.Sum(c => c.Frames))!.Key;
        else if (clips.All(c => c.Type == clips[0].Type))
            type = clips[0].Type;
        else
            type = PictureType.Mixed;

        var fieldOrders = clips.Select(c => c.TopFieldFirst).OfType<bool>().Distinct().ToList();
        double Share(Func<PictureAnalysis, double> share) => frames == 0 ? 0 : clips.Sum(c => share(c) * c.Frames) / frames;
        return new PictureAnalysis(type, fieldOrders.Count == 1 && type is not (PictureType.Progressive or PictureType.Film) ? fieldOrders[0] : null,
            Share(c => c.InterlacedShare), Share(c => c.RepeatedShare), frames, main.Range, main.PeakNits, main.MaxCll, clips.Any(c => c.DolbyVision));
    }

    /// <summary>A line about its dynamic range, e.g. "HDR10, mastered for 1000 nits", or null for SDR.</summary>
    public string? RangeSummary
    {
        get
        {
            if (!IsHdr)
                return null;
            var mastered = PeakNits is not null ? $", mastered for {PeakNits.Value} nits" : "";
            if (DolbyVision)
                return $"Dolby Vision (its {(Range == DynamicRange.Hlg ? "HLG" : "HDR10")} base layer is used{mastered})";
            return Range == DynamicRange.Hlg ? "HDR (HLG)" : "HDR10" + mastered;
        }
    }

    /// <summary>A longer explanation of its dynamic range, for a tooltip, or null for SDR.</summary>
    public string? RangeExplanation => !IsHdr ? null
        : PictureHelp.Hdr + (DolbyVision ? "\n\n" + PictureHelp.DolbyVision : "")
            + (MaxCll is not null && MaxCll.Value > 0 ? $"\n\n(Brightest pixel: {MaxCll.Value} nits.)" : "");

    /// <summary>
    /// Whether it counts as progressive though some of its frames looked interlaced: too few to call it mixed, but worth
    /// a look.
    /// </summary>
    public bool ContainsInterlaced => Type == PictureType.Progressive && InterlacedShare > 0;

    /// <summary>A short line for the node, e.g. "Interlaced (bottom field first)".</summary>
    public string Summary => Type switch
    {
        // Under 1% would round to "0% interlaced"
        PictureType.Unknown => "Unknown",
        PictureType.Progressive => ContainsInterlaced ? $"Progressive ({(InterlacedShare < 0.01 ? "under 1%" : InterlacedShare.ToString("P0"))} interlaced)" : "Progressive",
        PictureType.Film => "Film (soft telecine, read as 23.976 fps)",
        PictureType.Telecined => "Film, hard telecined to 29.97 fps",
        PictureType.Interlaced => TopFieldFirst is not null ? $"Interlaced ({(TopFieldFirst.Value ? "top" : "bottom")} field first)" : "Interlaced",
        _ => $"Partly interlaced ({InterlacedShare:P0} of frames)",
    };

    /// <summary>What to do about it, or null if nothing.</summary>
    public string? Advice => Type switch
    {
        PictureType.Telecined => "Add Inverse Telecine before encoding.",
        PictureType.Interlaced => "Add Deinterlace before encoding.",
        PictureType.Mixed => "Consider Deinterlace, set to frames marked interlaced.",
        _ => null,
    };

    /// <summary>A longer explanation of the type, for a tooltip.</summary>
    public string Explanation => Type == PictureType.Unknown ? PictureHelp.Unchecked : Type switch
    {
        PictureType.Progressive => ContainsInterlaced
            ? "Nearly every frame is a whole picture, so it counts as progressive. A few looked interlaced: too few to need "
                + "deinterlacing as a whole, but they may be short interlaced inserts, or frames the check misread. If they "
                + "show combing, Deinterlace set to \"Frames marked interlaced\" only touches those."
            : "Every frame is a whole picture, so there's nothing to deinterlace.",
        PictureType.Film => PictureHelp.SoftTelecine,
        PictureType.Telecined => PictureHelp.Telecine + "\n\n" + PictureHelp.HardTelecine,
        PictureType.Interlaced => PictureHelp.Interlacing,
        _ => PictureHelp.Interlacing + "\n\nOnly some of this video looked interlaced, e.g. credits or inserts added on video. Deinterlace set to "
            + "\"Frames marked interlaced\" only touches those.",
    } + $"\n\n(Measured on {Frames} frames: {InterlacedShare:P0} interlaced, {RepeatedShare:P0} with a repeated field.)";
}

/// <summary>Explanations of interlacing, telecine and HDR, for tooltips.</summary>
public static class PictureHelp
{
    public const string Unchecked =
        "This disc's frames weren't checked for interlacing: \"Check every frame for interlacing\" was off when it was "
        + "added. Whether the video needs Deinterlace or Inverse Telecine isn't known, so look for combing (jagged edges "
        + "on anything moving) in the Clip Renamer's preview.";

    public const string Hdr =
        "HDR (high dynamic range) video holds far brighter highlights and more colours than ordinary (SDR) video, for "
        + "screens that can show them. Encoding it with H.265 or AV1 at 10-bit keeps it HDR: the brightness information "
        + "(HDR10 metadata) is carried over. For SDR screens, or players that can't handle HDR, add a Dynamic Range node "
        + "to tone map it: it squeezes the brightness range into SDR's so it doesn't look dim and washed out.";

    public const string DolbyVision =
        "This disc also has Dolby Vision, as a second layer with scene-by-scene brightness information. ffmpeg can't "
        + "re-encode that, so the HDR10 picture underneath (which every HDR screen can show) is used. Copying the video "
        + "without encoding keeps the base layer as it is; the enhancement layer is a separate track players can't use "
        + "on its own.";

    public const string Interlacing =
        "Interlaced video shows each frame as two half-pictures (fields): one with the odd lines, then one with the even "
        + "lines, a fiftieth or sixtieth of a second apart. Old TVs drew them one after the other. On a modern screen both "
        + "fields show at once, so anything moving gets comb-like jagged edges. Deinterlacing rebuilds whole frames from the fields.";

    public const string Telecine =
        "Telecine is how film, shot at 24 frames a second, is fitted to NTSC video's 29.97. Film frames are split into "
        + "fields and some fields are shown twice (\"3:2 pulldown\"): every 4 film frames become 5 video frames, and 2 "
        + "of those 5 mix fields from two different film frames, so they look combed when something moves.";

    public const string SoftTelecine =
        Telecine + "\n\nThis video uses soft telecine: the disc stores the original film frames and only flags which fields a "
        + "player should repeat. DVD Xporter reads the film frames directly at 23.976 fps, so it needs no deinterlacing or "
        + "inverse telecine.";

    public const string Hybrid =
        "Some discs mix film with parts made on video: TV shows edited on video, anime with video credits, documentaries. "
        + "Set Output to \"Film and video\" for those: film parts get their 23.976 fps frames back, and video parts keep "
        + "every frame.";

    public const string HardTelecine =
        "This video is hard telecined: the repeated fields are part of the video itself. Inverse Telecine matches the fields "
        + "back into the original film frames and drops the repeats, giving clean 23.976 fps. Deinterlacing instead would "
        + "blur the combed frames and keep the uneven 3:2 judder.";
}

/// <summary>
/// Tells progressive, interlaced and telecined video apart with ffmpeg's idet filter, judging every frame (when its
/// disc was added to have them checked), and reads its colour (HDR or not) from a short sample.
/// </summary>
public static class PictureDetector
{
    // showinfo logs each frame's colour details and HDR side data (without its slow checksums)
    public const string ColourFilter = "showinfo=checksum=0";

    // idet judges each frame, and metadata logs each verdict with the frame's time
    public const string FramesFilter = "idet,metadata=mode=print:key=lavfi.idet.multiple.current_frame";

    private static readonly PreciseTime SampleLength = new(0, 0, 4, 0);

    // Interlaced frames closer together than this are listed as one stretch
    private static readonly PreciseTime RangeGap = new(0, 0, 0, 500_000_000);

    // Under this share of interlaced frames, video counts as progressive (idet misreads a few)
    private const double ProgressiveBelow = 0.10;
    // 3:2 pulldown repeats a field in 2 of every 5 frames (40%); interlaced video seldom does
    private const double TelecineRepeats = 0.25;
    private const double InterlacedAbove = 0.5;
    private const int MinimumFrames = 30;

    /// <summary>
    /// Reads the video's colour from a sample of <paramref name="colourProbe"/> (a probe with <see cref="ColourFilter"/>,
    /// see <see cref="TranscodeCompiler.CompileProbe"/>), then judges every frame of <paramref name="framesProbe"/> (with
    /// <see cref="FramesFilter"/>) and classifies it; with no frames probe, its type is <see cref="PictureType.Unknown"/>.
    /// Null if too few frames could be read to tell.
    /// </summary>
    /// <param name="isFilm">Whether the disc marks it as soft-telecined film, which reads as progressive.</param>
    /// <param name="dolbyVisionLayer">Whether the video comes with a Dolby Vision enhancement layer.</param>
    /// <param name="progress">How far through its frames it is, 0–1.</param>
    public static async Task<PictureAnalysis?> DetectAsync(TranscodeJob colourProbe, TranscodeJob? framesProbe, bool isFilm, bool dolbyVisionLayer,
        string workFolder, Action<double>? progress, CancellationToken cancellation)
    {
        Directory.CreateDirectory(workFolder);
        var colour = await ReadColourAsync(colourProbe, workFolder, cancellation);
        if (colour is null)
            return null;
        var range = colour.Transfer switch
        {
            "smpte2084" => DynamicRange.Hdr10,
            "arib-std-b67" => DynamicRange.Hlg,
            _ => DynamicRange.Sdr,
        };
        var analysis = new PictureAnalysis(PictureType.Unknown, null, 0, 0, 0, range, range == DynamicRange.Hdr10 ? colour.MasteringPeak : null,
            range == DynamicRange.Sdr ? null : colour.MaxCll, range != DynamicRange.Sdr && (dolbyVisionLayer || colour.DolbyVision));
        if (framesProbe is null)
            return analysis;

        // A whole film's verdicts make a long log, so it's read as it goes, and not kept
        var log = Path.Combine(workFolder, "idet.log");
        try
        {
            var args = framesProbe.BuildArguments(Path.Combine(workFolder, "idet"), workFolder, writeFiles: true);
            await FFmpegRunner.RunAsync(args, framesProbe.Duration, log, progress, cancellation);
            if (!File.Exists(log))
                return null;

            // idet prints its totals when it closes
            var counts = FFmpegLog.Idet(string.Join('\n', File.ReadLines(log).Where(l => l.Contains("detection:") || l.Contains("Repeated Fields:"))));
            long decided = counts.TopFieldFirst + counts.BottomFieldFirst + counts.Progressive;
            if (decided < MinimumFrames)
                return null;

            double interlaced = (double)(counts.TopFieldFirst + counts.BottomFieldFirst) / decided;
            long repeated = counts.RepeatedTop + counts.RepeatedBottom;
            double repeatShare = (double)repeated / Math.Max(1, counts.RepeatedNeither + repeated);
            var type = interlaced < ProgressiveBelow ? (isFilm ? PictureType.Film : PictureType.Progressive)
                : repeatShare >= TelecineRepeats ? PictureType.Telecined
                : interlaced >= InterlacedAbove ? PictureType.Interlaced
                : PictureType.Mixed;
            long tff = counts.TopFieldFirst, bff = counts.BottomFieldFirst;
            bool? topFirst = tff + bff == 0 ? null : tff >= bff * 3 ? true : bff >= tff * 3 ? false : null;
            return analysis with
            {
                Type = type,
                TopFieldFirst = type is PictureType.Progressive or PictureType.Film ? null : topFirst,
                InterlacedShare = interlaced,
                RepeatedShare = repeatShare,
                Frames = (int)(decided + counts.Undetermined),
                InterlacedRanges = Ranges(FFmpegLog.IdetFrames(File.ReadLines(log))),
            };
        }
        finally
        {
            File.Delete(log);
        }
    }

    // A sample from the middle, which is past any black lead-in
    private static async Task<ColourInfo?> ReadColourAsync(TranscodeJob probe, string workFolder, CancellationToken cancellation)
    {
        var duration = probe.Duration ?? PreciseTime.Zero;
        var start = duration > SampleLength + SampleLength ? new PreciseTime((duration - SampleLength).Nanoseconds / 2) : PreciseTime.Zero;
        var log = Path.Combine(workFolder, "colour.log");
        var args = probe.BuildArguments(Path.Combine(workFolder, "colour"), workFolder, new TestRange(start, SampleLength), writeFiles: true);
        await FFmpegRunner.RunAsync(args, SampleLength, log, null, cancellation);
        if (!File.Exists(log))
            return null;
        var lines = await File.ReadAllLinesAsync(log, cancellation);
        return FFmpegLog.ShownFrameTimes(lines).Count > 0 ? FFmpegLog.Colour(string.Join('\n', lines)) : null;
    }

    /// <summary>The stretches where frames looked interlaced, timed from the first frame.</summary>
    private static List<InterlacedRange> Ranges(IEnumerable<IdetFrame> frames)
    {
        var ranges = new List<InterlacedRange>();
        PreciseTime? first = null, start = null;
        PreciseTime previous = PreciseTime.Zero, last = PreciseTime.Zero, frameLength = PreciseTime.Zero;
        int count = 0;
        foreach (var frame in frames)
        {
            var at = frame.Time.ToPreciseTime() - (first ??= frame.Time.ToPreciseTime());
            if (at > previous)
                frameLength = at - previous;
            previous = at;
            if (!frame.Interlaced)
                continue;

            if (start is not null && at - last > RangeGap)
            {
                ranges.Add(new InterlacedRange(start.Value, last + frameLength, count));
                start = null;
            }

            if (start is null)
            {
                start = at;
                count = 0;
            }

            last = at;
            count++;
        }

        if (start is not null)
            ranges.Add(new InterlacedRange(start.Value, last + frameLength, count));
        return ranges;
    }
}
