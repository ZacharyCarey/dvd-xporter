using System.Globalization;
using System.Text;

namespace FFmpeg;

/// <summary>
/// Where an input's data is: a file, or a URL ffmpeg reads over a network protocol (e.g. a local http server streaming
/// a disc's clips). URLs are let through the protocol whitelist of the inputs that open them (see <see cref="ConcatInput"/>).
/// </summary>
public sealed record FFmpegSource
{
    private FFmpegSource(string location, Uri? url)
    {
        Location = location;
        Url = url;
    }

    /// <summary>What ffmpeg is given: the path, or the URL.</summary>
    public string Location { get; }

    /// <summary>The URL, if it is one.</summary>
    public Uri? Url { get; }

    public static FFmpegSource FromFile(string path) => new(path, null);

    public static FFmpegSource FromUrl(Uri url) => new(url.AbsoluteUri, url);

    /// <summary>The protocols ffmpeg needs to read it, e.g. "file", or "http" and "tcp".</summary>
    public IEnumerable<string> Protocols => Url?.Scheme switch
    {
        null => ["file"],
        "http" => ["http", "tcp"],
        "https" => ["https", "tls", "tcp"],
        var scheme => [scheme],
    };

    public override string ToString() => Location;
}

public abstract record FFmpegInput;

/// <param name="Start">Where to start reading, from the file's start. Null = the beginning.</param>
/// <param name="End">Where to stop reading, on the same timeline. Null = the end.</param>
/// <param name="Options">ffmpeg options for this input, e.g. "-r 24000/1001".</param>
public sealed record FileInput(FFmpegSource Source, FFmpegTime? Start, FFmpegTime? End, IReadOnlyList<string> Options) : FFmpegInput;

/// <summary>
/// Files played one after another with ffmpeg's concat demuxer, like a disc playlist. Its streams are declared
/// up front and found by ID in each file, so their order is fixed and streams that start late (a DVD's
/// subtitles may first appear minutes in) are there from the start. The demuxer only opens files unless told
/// otherwise, so parts that are URLs have their protocols allowed.
/// </summary>
/// <param name="Start">Where to start, on the parts' joined timeline. Null = the beginning.</param>
/// <param name="End">Where to stop, on the same timeline. Null = the end.</param>
public sealed record ConcatInput(IReadOnlyList<ConcatPart> Parts, IReadOnlyList<ConcatStream> Streams, FFmpegTime? Start, FFmpegTime? End,
    IReadOnlyList<string> Options) : FFmpegInput;

/// <param name="Id">The stream's ID in each file: a transport stream PID, or ffmpeg's ID for a program stream
/// stream (0x1E0 video, 0x80 AC-3, 0x20 subtitles, ...).</param>
/// <param name="Codec">The codec, for streams that might not be seen while probing (subtitles).</param>
/// <param name="Extradata">The codec's extradata, e.g. a DVD subtitle palette.</param>
public sealed record ConcatStream(int Id, string? Codec = null, string? Extradata = null);

/// <param name="Source">The file, or a URL ffmpeg can read (e.g. a local http stream).</param>
/// <param name="InPoint">Where the file starts playing, on its own timeline (a Blu-ray clip's often starts at 10:00).</param>
/// <param name="Duration">How long the part plays for, if known; lets a cut skip whole parts before it.</param>
/// <param name="FileStart">The timestamp the file starts at, if known; lets a partial run start partway into it.</param>
public sealed record ConcatPart(FFmpegSource Source, FFmpegTime? InPoint, FFmpegTime? OutPoint, FFmpegTime? Duration = null, FFmpegTime? FileStart = null);

/// <summary>A picture or sound ffmpeg makes itself, as a lavfi source, e.g. "color=c=0x000000:s=720x480:d=5".</summary>
public sealed record GeneratedInput(string Graph) : FFmpegInput;

/// <summary>A chapter: where it starts on the output's timeline, and its name.</summary>
public sealed record FFmpegChapter(FFmpegTime Start, string? Title);

/// <summary>A stretch of the output: from <see cref="Start"/> (on the output's timeline) for <see cref="Length"/>.</summary>
public readonly record struct FFmpegRange(FFmpegTime Start, FFmpegTime Length);

/// <summary>What an <see cref="FFmpegCommand"/> writes, and which part of it.</summary>
/// <param name="Path">The file to write; for an image sequence, the folder its numbered images go in.</param>
public sealed record FFmpegOutput(string Path)
{
    /// <summary>Where the output starts on its inputs' timeline (a start trim). Null = the beginning.</summary>
    public FFmpegTime? Start { get; init; }

    /// <summary>Where it ends on the same timeline. Null = the end.</summary>
    public FFmpegTime? End { get; init; }

    /// <summary>How long the whole output is, if known (for the last chapter's end).</summary>
    public FFmpegTime? Duration { get; init; }

    /// <summary>
    /// Only this stretch of the output, e.g. for a quick test. Its start may then be found by seeking to a keyframe a
    /// little before it, which is quick but can cut up to a keyframe early.
    /// </summary>
    public FFmpegRange? Part { get; init; }

    /// <summary>The output's chapters, on its own timeline (after <see cref="Start"/>), in order.</summary>
    public IReadOnlyList<FFmpegChapter> Chapters { get; init; } = [];

    /// <summary>When chapters are cut to <see cref="Part"/>: a chapter starting less than this before the next is dropped.</summary>
    public FFmpegTime MinimumChapterGap { get; init; }

    /// <summary>For numbered images (001.png, 002.png, ...) in the <see cref="Path"/> folder: their extension.</summary>
    public string? ImageSequenceExtension { get; init; }

    /// <summary>Text to put in place of placeholders in the filters, e.g. values measured in a first pass.</summary>
    public IReadOnlyDictionary<string, string> Placeholders { get; init; } = new Dictionary<string, string>();
}

/// <summary>
/// An ffmpeg command as its parts: options, inputs, a filtergraph and output options. <see cref="BuildArguments"/>
/// puts them together for an <see cref="FFmpegOutput"/>, cutting the inputs to the part being made.
/// </summary>
public sealed class FFmpegCommand
{
    /// <summary>
    /// In the filters, where the window being made is cut out of a video stream: for a probe that analyses a stretch,
    /// since an output seek would only drop frames after the filters had analysed everything decoded before it.
    /// </summary>
    public const string VideoWindow = "{window}";

    /// <summary>The same, for an audio stream.</summary>
    public const string AudioWindow = "{awindow}";

    // How far before a partial run's start the demuxer is asked to seek in a file
    private static readonly FFmpegTime PartPreroll = FFmpegTime.FromSeconds(3);

    /// <summary>Options before the inputs, e.g. setting up the GPU.</summary>
    public List<string> GlobalOptions { get; } = [];

    public List<FFmpegInput> Inputs { get; } = [];

    /// <summary>The <c>-filter_complex</c> graph's chains, joined with ";".</summary>
    public List<string> Filters { get; } = [];

    /// <summary>Options for the output: maps, codecs and metadata.</summary>
    public List<string> OutputOptions { get; } = [];

    /// <summary>
    /// The ffmpeg arguments that write <paramref name="output"/> (without the ffmpeg program itself). Concat lists
    /// and chapters go in <paramref name="workFolder"/>, and are written there if <paramref name="writeFiles"/>.
    /// </summary>
    public List<string> BuildArguments(FFmpegOutput output, string workFolder, bool writeFiles = false)
    {
        var args = new List<string> { "-hide_banner" };
        args.AddRange(GlobalOptions);

        // The part of the output to make: a partial run's start is on the output's timeline, after its own start trim
        var outputStart = output.Start ?? FFmpegTime.Zero;
        var from = outputStart;
        var stop = output.End;
        var range = output.Part;
        if (range is not null)
        {
            from += range.Value.Start;
            var partEnd = from + range.Value.Length;
            stop = stop is not null && stop.Value < partEnd ? stop.Value : partEnd;
        }

        // With one input, its own start and end and the output's become one window on it, so it can be cut quickly
        // and exactly. With several, each is cut on its own and the output after, so they stay lined up.
        bool single = Inputs.Count == 1;
        FFmpegTime? outputSeek = from, outputLength = Minus(stop, from);
        var name = System.IO.Path.GetFileNameWithoutExtension(output.Path);
        for (int i = 0; i < Inputs.Count; i++)
        {
            switch (Inputs[i])
            {
                case FileInput file when single:
                    // Seeking a file is fast, and exact once decoded
                    args.AddRange(file.Options);
                    AddRange(args, (file.Start ?? FFmpegTime.Zero) + from, file.End);
                    args.AddRange(["-i", file.Source.Location]);
                    outputSeek = null;
                    outputLength = Min(Minus(stop, from), Minus(file.End, (file.Start ?? FFmpegTime.Zero) + from));
                    break;
                case FileInput file:
                    args.AddRange(file.Options);
                    AddRange(args, file.Start ?? FFmpegTime.Zero, file.End);
                    args.AddRange(["-i", file.Source.Location]);
                    break;
                case ConcatInput concat:
                    // The concat demuxer doesn't seek reliably (a Blu-ray clip's timestamps start at 10:00), so the
                    // list starts at the part the window starts in, and the output is trimmed from there
                    int first = 0;
                    FFmpegTime? firstInPoint = null;
                    if (single)
                    {
                        var windowStart = (concat.Start ?? FFmpegTime.Zero) + from;
                        (first, var skipped) = FirstPart(concat, windowStart);
                        outputSeek = windowStart - skipped;
                        outputLength = Min(Minus(stop, from), Minus(concat.End, windowStart));

                        // A partial run can also start partway into that part: the demuxer seeks to a little before
                        // (it lands on a keyframe at or before), and the output is trimmed by the rest. ffmpeg counts
                        // from the first packet it gets, so the cut may come up to a keyframe early, but tracks stay
                        // in sync. Not used for whole runs, which cut exactly.
                        var part = concat.Parts[first];
                        var partStart = part.InPoint ?? part.FileStart;
                        if (output.Part is not null && partStart is not null && outputSeek > PartPreroll)
                        {
                            firstInPoint = partStart.Value + outputSeek.Value - PartPreroll;
                            outputSeek = PartPreroll;
                        }
                    }

                    var listPath = System.IO.Path.Combine(workFolder, $"{name}.input{i}.txt");
                    if (writeFiles)
                    {
                        Directory.CreateDirectory(workFolder);
                        File.WriteAllText(listPath, ConcatList(concat, first, firstInPoint));
                    }

                    args.AddRange(concat.Options);
                    if (concat.Parts.Any(p => p.Source.Url is not null))
                    {
                        var protocols = new[] { "file" }.Concat(concat.Parts.SelectMany(p => p.Source.Protocols)).Distinct();
                        args.AddRange(["-protocol_whitelist", string.Join(",", protocols)]);
                    }
                    if (!single)
                        AddRange(args, concat.Start ?? FFmpegTime.Zero, concat.End);
                    args.AddRange(["-f", "concat", "-safe", "0", "-i", listPath]);
                    break;
                case GeneratedInput generated:
                    args.AddRange(["-f", "lavfi", "-i", generated.Graph]);
                    break;
            }
        }

        // Chapters come from a metadata file written in the output's own time. ffmpeg moves chapters back by
        // the output's seek, so the file is offset by the same to cancel it.
        var chapters = output.Chapters.Count == 0 ? [] : TrimChapters(output.Chapters, from - outputStart, Minus(stop, outputStart), output.MinimumChapterGap);
        if (chapters.Count > 0)
        {
            var chaptersPath = System.IO.Path.Combine(workFolder, $"{name}.chapters.txt");
            if (writeFiles)
            {
                Directory.CreateDirectory(workFolder);
                File.WriteAllText(chaptersPath, FFMetadata(chapters, Minus(Min(stop, Plus(output.Duration, outputStart)), from)));
            }

            if (outputSeek is not null && outputSeek.Value > FFmpegTime.Zero)
                args.AddRange(["-itsoffset", outputSeek.Value.ToString()]);
            args.AddRange(["-f", "ffmetadata", "-i", chaptersPath]);
        }

        if (Filters.Count > 0)
        {
            var graph = string.Join(";", Filters);
            foreach (var (placeholder, value) in output.Placeholders)
                graph = graph.Replace(placeholder, value);
            if (graph.Contains(AudioWindow))
            {
                var window = outputSeek is not null && outputSeek.Value > FFmpegTime.Zero
                    ? $"atrim=start={outputSeek.Value}" + (outputLength is not null ? $":duration={outputLength.Value}" : "") + ",asetpts=PTS-STARTPTS,"
                    : outputLength is not null ? $"atrim=duration={outputLength.Value}," : "";
                graph = graph.Replace(AudioWindow, window);
                outputSeek = null;
                outputLength = null;
            }

            if (graph.Contains(VideoWindow))
            {
                // A probe: cut the window in the filters, so only it is analysed
                var window = "";
                if (outputSeek is not null && outputSeek.Value > FFmpegTime.Zero)
                    window = $"trim=start={outputSeek.Value}" + (outputLength is not null ? $":duration={outputLength.Value}" : "") + ",setpts=PTS-STARTPTS,";
                else if (outputLength is not null)
                    window = $"trim=duration={outputLength.Value},";
                graph = graph.Replace(VideoWindow, window);
                // The trim is the window; an output limit would count the filters' own timestamps (a quality
                // probe numbers frames one a second)
                outputSeek = null;
                outputLength = null;
            }

            args.AddRange(["-filter_complex", graph]);
        }

        args.AddRange(OutputOptions);
        args.AddRange(["-map_chapters", chapters.Count > 0 ? Inputs.Count.ToString(CultureInfo.InvariantCulture) : "-1"]);

        if (outputSeek is not null && outputSeek.Value > FFmpegTime.Zero)
            args.AddRange(["-ss", outputSeek.Value.ToString()]);
        if (outputLength is not null)
            args.AddRange(["-t", outputLength.Value.ToString()]);

        var extension = output.ImageSequenceExtension;
        if (extension is not null)
        {
            // Numbered images in the folder: 001.png, 002.png, ...
            if (writeFiles)
                Directory.CreateDirectory(output.Path);
            args.AddRange(["-f", "image2", System.IO.Path.Combine(output.Path, $"%03d.{extension}")]);
            return args;
        }

        args.Add(output.Path);
        return args;
    }

    private static FFmpegTime? Min(FFmpegTime? a, FFmpegTime? b) => a is null ? b : b is null ? a : a < b ? a : b;

    private static FFmpegTime? Minus(FFmpegTime? a, FFmpegTime b) => a is not null ? a.Value - b : null;

    private static FFmpegTime? Plus(FFmpegTime? a, FFmpegTime b) => a is not null ? a.Value + b : null;

    /// <summary>The part a concat input's timeline reaches <paramref name="time"/> in, and how much comes before it.</summary>
    private static (int Index, FFmpegTime Before) FirstPart(ConcatInput concat, FFmpegTime time)
    {
        var before = FFmpegTime.Zero;
        for (int i = 0; i < concat.Parts.Count - 1; i++)
        {
            var duration = concat.Parts[i].Duration;
            if (duration is null || before + duration.Value > time)
                return (i, before);
            before += duration.Value;
        }

        return (concat.Parts.Count - 1, before);
    }

    private static void AddRange(List<string> args, FFmpegTime start, FFmpegTime? end)
    {
        if (start > FFmpegTime.Zero)
            args.AddRange(["-ss", start.ToString()]);
        if (end is not null)
            args.AddRange(["-to", end.Value.ToString()]);
    }

    private static string ConcatList(ConcatInput concat, int firstPart, FFmpegTime? firstInPoint)
    {
        var text = new StringBuilder("ffconcat version 1.0\n");
        foreach (var stream in concat.Streams)
        {
            text.Append("stream\nexact_stream_id 0x").Append(stream.Id.ToString("x")).Append('\n');
            if (stream.Codec is not null)
                text.Append("stream_codec ").Append(stream.Codec).Append('\n');
            if (stream.Extradata is not null)
                text.Append("stream_extradata ").Append(Convert.ToHexString(Encoding.UTF8.GetBytes(stream.Extradata))).Append('\n');
        }

        for (int i = firstPart; i < concat.Parts.Count; i++)
        {
            var part = concat.Parts[i];
            text.Append("file '").Append(part.Source.Location.Replace("'", "'\\''")).Append("'\n");
            var inPoint = i == firstPart ? firstInPoint ?? part.InPoint : part.InPoint;
            if (inPoint is not null)
                text.Append("inpoint ").Append(inPoint.Value).Append('\n');
            var outPoint = part.OutPoint;
            if (outPoint is not null)
                text.Append("outpoint ").Append(outPoint.Value).Append('\n');
        }

        return text.ToString();
    }

    /// <summary>
    /// Chapters cut to the stretch from <paramref name="start"/> to <paramref name="end"/>, timed from its start: the one
    /// playing at the start begins at 0, and one starting less than <paramref name="minimumGap"/> before the next is
    /// dropped in favour of it.
    /// </summary>
    public static List<FFmpegChapter> TrimChapters(IReadOnlyList<FFmpegChapter> chapters, FFmpegTime start, FFmpegTime? end, FFmpegTime minimumGap)
    {
        var result = new List<FFmpegChapter>();
        for (int i = 0; i < chapters.Count; i++)
        {
            var chapter = chapters[i];
            if (end is not null && chapter.Start >= end.Value)
                break;
            var next = i + 1 < chapters.Count ? chapters[i + 1].Start : (FFmpegTime?)null;
            if (chapter.Start >= start)
                result.Add(chapter with { Start = chapter.Start - start });
            else if (next is null || next.Value > start)
                result.Add(chapter with { Start = FFmpegTime.Zero });
        }

        return result.Where((c, i) => i + 1 >= result.Count || result[i + 1].Start - c.Start >= minimumGap).ToList();
    }

    /// <summary>The chapters as an ffmpeg metadata file, which ffmpeg writes into the output as its chapters.</summary>
    /// <param name="length">How long the output is, for the last chapter's end.</param>
    public static string FFMetadata(IReadOnlyList<FFmpegChapter> chapters, FFmpegTime? length)
    {
        var text = new StringBuilder(";FFMETADATA1\n");
        for (int i = 0; i < chapters.Count; i++)
        {
            var start = chapters[i].Start.Microseconds / 1000;
            var end = i + 1 < chapters.Count ? chapters[i + 1].Start.Microseconds / 1000
                : length is not null ? Math.Max(start, length.Value.Microseconds / 1000) : start;
            text.Append("[CHAPTER]\nTIMEBASE=1/1000\n")
                .Append("START=").Append(start.ToString(CultureInfo.InvariantCulture)).Append('\n')
                .Append("END=").Append(end.ToString(CultureInfo.InvariantCulture)).Append('\n');
            if (!string.IsNullOrEmpty(chapters[i].Title))
                text.Append("title=").Append(EscapeMetadata(chapters[i].Title!)).Append('\n');
        }

        return text.ToString();
    }

    // ffmetadata files escape = ; # \ and newlines with a backslash
    private static string EscapeMetadata(string value)
    {
        var text = new StringBuilder();
        foreach (var c in value)
        {
            if (c is '=' or ';' or '#' or '\\' or '\n')
                text.Append('\\');
            text.Append(c);
        }

        return text.ToString();
    }
}
