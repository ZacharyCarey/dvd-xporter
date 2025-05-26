using DvdXporter.Core.Projects;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

/// <summary>A test encode: part of the output, from <see cref="Start"/> for <see cref="Length"/>.</summary>
public readonly record struct TestRange(PreciseTime Start, PreciseTime Length);

/// <summary>
/// One output track and how it's tagged. The same details a Matroska remux (mkvmerge) would set, kept apart
/// from ffmpeg's options so a later step can apply them itself.
/// </summary>
/// <param name="Codec">The encoder, or "copy".</param>
/// <param name="Description">Where it came from, e.g. "English · AC3 · 6 ch → AAC".</param>
public sealed record OutputTrack(
    PortKind Kind,
    string Codec,
    string Description,
    string? Title,
    string? Language,
    bool Default,
    bool Forced,
    bool HearingImpaired,
    bool VisualImpaired,
    bool Original,
    bool Commentary,
    bool Descriptions = false);

/// <summary>One output file and the ffmpeg command that makes it.</summary>
public sealed class TranscodeJob
{
    public required CanvasNode Output { get; init; }

    /// <summary>Where the file goes in the library, if the output is wired to an Export node.</summary>
    public CanvasNode? Export { get; init; }

    /// <summary>What to call the job: the library item's name, or the output's.</summary>
    public required string Name { get; init; }

    /// <summary>"mkv" or "mp4"; for an image gallery, its images' "png" or "jpg".</summary>
    public required string Extension { get; init; }

    /// <summary>
    /// Whether it makes a folder of numbered images (an Image Gallery) rather than one file: the output path is then
    /// the folder.
    /// </summary>
    public bool IsImageSequence { get; init; }

    /// <summary>
    /// For an image gallery that keeps each picture shown: how long a picture has to stay on screen to be kept, so the
    /// frames of fades between pictures are dropped (see <see cref="GalleryImages.Tidy"/>). Null keeps every image.
    /// </summary>
    public PreciseTime? MinimumHold { get; init; }

    /// <summary>For an image gallery: whether to leave out pictures that are all black (see <see cref="GalleryImages.Tidy"/>).</summary>
    public bool SkipBlack { get; init; }

    /// <summary>Whether it makes a folder (an image gallery) rather than one file.</summary>
    public bool IsFolder => IsImageSequence;

    /// <summary>The file's path in the library (see <see cref="LibraryNaming.RelativePath"/>), if it's exported.</summary>
    public string? LibraryPath { get; init; }

    public string? Title { get; init; }

    /// <summary>The ffmpeg command's parts: options, inputs, filters, and output options (maps, codecs, track metadata).</summary>
    public FFmpegCommand Command { get; } = new();

    public List<OutputTrack> Tracks { get; } = [];

    /// <summary>The output's chapters, on its own timeline (after its start trim). Empty = none.</summary>
    public List<Chapter> Chapters { get; } = [];

    /// <summary>The output node's input port each of <see cref="Tracks"/> came in on ("t0", "t1", ...).</summary>
    public List<string> TrackPorts { get; } = [];

    /// <summary>
    /// Measurements to make before running (Loudness nodes measuring first): each fills its token in the filters,
    /// see <see cref="LoudnessMeasurer"/>.
    /// </summary>
    public List<LoudnessMeasurement> Measurements { get; } = [];

    /// <summary>
    /// For measuring quality: the first encoded video track's index in the output, and the stream that went into its
    /// codec (on the expanded canvas, see <see cref="TranscodeCompiler.CompileProbe"/>), which it's compared with.
    /// </summary>
    public (int OutputIndex, int Node, string Port)? QualityReference { get; set; }

    /// <summary>The discs whose clips the job reads.</summary>
    public HashSet<ProjectDisc> Discs { get; } = [];

    /// <summary>Discs the job reads that aren't on this computer; it can't run until they're found.</summary>
    public HashSet<ProjectDisc> MissingDiscs { get; } = [];

    /// <summary>Whether the job can run now.</summary>
    public bool IsReady => MissingDiscs.Count == 0;

    /// <summary>How long the output will be, as far as can be told, for showing progress.</summary>
    public PreciseTime? Duration { get; set; }

    public PreciseTime? OutputStart { get; set; }

    public PreciseTime? OutputEnd { get; set; }

    /// <summary>
    /// The ffmpeg arguments that write the output to <paramref name="outputPath"/> (without the ffmpeg program
    /// itself), or the part of it in <paramref name="test"/>. Concat lists go in <paramref name="workFolder"/>, and are
    /// written there if <paramref name="writeFiles"/>.
    /// </summary>
    /// <param name="measured">What measurements found, by token (see <see cref="Measurements"/>); without one, a Loudness
    /// node adjusts as it goes instead.</param>
    public List<string> BuildArguments(string outputPath, string workFolder, TestRange? test = null, bool writeFiles = false,
        IReadOnlyDictionary<string, string>? measured = null) =>
        Command.BuildArguments(new FFmpegOutput(outputPath)
        {
            Start = OutputStart.ToFFmpegTime(),
            End = OutputEnd.ToFFmpegTime(),
            Duration = Duration.ToFFmpegTime(),
            Part = test is not null ? new FFmpegRange(test.Value.Start.ToFFmpegTime(), test.Value.Length.ToFFmpegTime()) : null,
            Chapters = ChapterLists.ToFFmpeg(Chapters),
            MinimumChapterGap = ChapterLists.MinimumGap.ToFFmpegTime(),
            ImageSequenceExtension = IsImageSequence ? Extension : null,
            Placeholders = Measurements.ToDictionary(m => m.Token, m => measured?.GetValueOrDefault(m.Token) ?? m.Unmeasured),
        }, workFolder, writeFiles);

    /// <summary>The arguments as one line for showing or copying into a terminal, each quoted as needed.</summary>
    public static string ToCommandLine(IEnumerable<string> args) => FFmpegArguments.ToCommandLine(args);
}
