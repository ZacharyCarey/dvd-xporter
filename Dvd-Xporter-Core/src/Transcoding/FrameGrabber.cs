using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// Saves the picture at a time in one of a disc's clips as an image, with ffmpeg rather than a screenshot of the
/// player: at its full size, with square pixels (a DVD's stretched to its shape), deinterlaced if the clip was found to
/// be interlaced, and tone mapped to SDR if it's HDR.
/// </summary>
public static class FrameGrabber
{
    /// <summary>The kinds of image it saves, by extension.</summary>
    public static readonly IReadOnlyList<string> Extensions = [".png", ".jpg", ".jpeg"];

    // Reading starts this far before the time and is trimmed off: ffmpeg seeks a disc's video to about there, and some
    // decoders (VC-1) put out grey frames until the next keyframe
    private static readonly PreciseTime Preroll = new(0, 0, 5, 0);

    // And goes a little past it, so the frame there is in what's read
    private static readonly PreciseTime ReadAfter = new(0, 0, 1, 0);

    /// <summary>Saves the frame at <paramref name="time"/> (from the clip's start) to <paramref name="imagePath"/>.</summary>
    /// <returns>Null once it's saved, otherwise what went wrong.</returns>
    public static async Task<string?> SaveAsync(ProjectTranscodeSources sources, ProjectDisc disc, string clip, PreciseTime time,
        string imagePath, CancellationToken cancellation)
    {
        var extension = Path.GetExtension(imagePath).ToLowerInvariant();
        if (!Extensions.Contains(extension))
            return "Save it as a PNG or JPEG image (.png or .jpg).";

        // A canvas of its own: just the clip
        var canvas = new TranscodeCanvas();
        var source = SourceNode.Create(disc.Fingerprint, NodeTypes.ClipItemPrefix + clip, id: 1);
        canvas.Nodes.Add(source);
        var context = sources.ForCanvas(canvas);
        var selection = new SourceNode(source).Resolve(context);
        var video = selection?.Tracks.FirstOrDefault(t => t.Kind == ClipTrackKind.Video);
        if (selection is null || video is null)
            return "This video file has no picture.";

        var length = selection.Duration;
        if (time >= length)
            return "That's past the end of the video.";
        var preroll = time < Preroll ? time : Preroll;

        var options = extension == ".png"
            ? new[] { "-frames:v", "1", "-update", "1", "-c:v", "png" }
            : ["-frames:v", "1", "-update", "1", "-c:v", "mjpeg", "-q:v", "2", "-pix_fmt", "yuvj444p"];
        var probe = TranscodeCompiler.CompileProbe(canvas, context, sources, source.Id, source.Id, SourceNode.TrackPortKey(video),
            Filter(preroll, context.GetPicture(source)), out var problem, options);
        if (probe is null)
            return problem;

        // Made beside the disc's other working files, and moved over once it's whole
        var work = Path.Combine(sources.Discs.Cache(disc).FolderPath, "work", "frame");
        Directory.CreateDirectory(work);
        var made = Path.Combine(work, "frame" + extension);
        File.Delete(made);
        var read = preroll + ReadAfter;
        var args = probe.BuildArguments(made, work, new TestRange(time - preroll, read), writeFiles: true);
        var error = await FFmpegRunner.RunAsync(args, read, Path.Combine(work, "frame.log"), null, cancellation);
        if (error is not null)
            return error;
        if (!File.Exists(made))
            return "There's no picture at that time.";

        File.Move(made, imagePath, overwrite: true);
        return null;
    }

    /// <summary>
    /// What the picture goes through: from the frame <paramref name="preroll"/> into what's read, deinterlaced and tone
    /// mapped as it needs, at square pixels.
    /// </summary>
    private static string Filter(PreciseTime preroll, PictureAnalysis? picture)
    {
        // Not known to be whole frames: the frames marked interlaced are deinterlaced
        var steps = new List<string> { $"trim=start={preroll.ToFFmpegTime()},setpts=PTS-STARTPTS" };
        if (picture?.Type is PictureType.Interlaced or PictureType.Telecined)
            steps.Add("bwdif=mode=send_frame");
        else if (picture?.Type is not (PictureType.Progressive or PictureType.Film))
            steps.Add("bwdif=mode=send_frame:deint=interlaced");
        if (picture?.IsHdr == true)
            steps.Add(new DynamicRangeNode(new CanvasNode()).Filter);
        steps.Add("scale='trunc(iw*sar/2)*2':ih:flags=lanczos,setsar=1");
        return string.Join(",", steps);
    }
}
