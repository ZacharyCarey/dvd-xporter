using DvdXporter.Core.Transcoding.Nodes;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

public static partial class TranscodeCompiler
{
    // Forced-only subtitle tracks, and subtitles burned into the picture.
    private sealed partial class JobBuilder
    {
        /// <summary>
        /// The forced captions of a disc subtitle track, as an input of their own: a file made from what the disc's
        /// subtitles were read for (a .sup for Blu-ray, VobSub for DVD), timed like the source.
        /// </summary>
        private Compiled? ForcedInput(CanvasNode node, Compiled subtitles)
        {
            if (subtitles.ForcedOnly)
                return subtitles;
            var origin = subtitles.Origin;
            if (subtitles.Kind != PortKind.Subtitle || origin is null)
            {
                Issue(IssueLevel.Error, node, "Connect a subtitle track straight from a Disc Video.");
                return null;
            }

            var path = files.GetForcedSubtitles(origin.Disc, origin.Entries, origin.Track);
            if (path is null)
            {
                Issue(IssueLevel.Error, node, $"\"{origin.Track.Label}\" has no forced captions.");
                return null;
            }

            // Each use reads the file as its own input: ffmpeg stalls when one subtitle input is both copied to a
            // track and drawn by a burn-in. The files are tiny.
            int index = _job.Command.Inputs.Count;
            _job.Command.Inputs.Add(new FileInput(FFmpegSource.FromFile(path), origin.Start.ToFFmpegTime(), origin.End.ToFFmpegTime(), []));

            return subtitles with
            {
                Ref = $"{index}:s:0",
                IsLabel = false,
                Input = index,
                ForcedOnly = true,
                Description = subtitles.Description + " (forced only)",
            };
        }

        private void CompileForcedSubtitles(CanvasNode node)
        {
            var subtitles = Input(node, "in", "");
            if (subtitles is null)
                return;
            var forced = ForcedInput(node, subtitles);
            if (forced is not null)
                _compiled[(node.Id, "out")] = forced;
        }

        /// <summary>Draws subtitles (by default only forced ones) into the video.</summary>
        private void CompileBurnSubtitles(CanvasNode node)
        {
            var video = Input(node, "in", "video");
            var subtitles = Input(node, BurnSubtitlesNode.SubtitlesPort, "subtitles");
            if (video is null || subtitles is null)
                return;
            if (subtitles.Kind != PortKind.Subtitle)
            {
                Issue(IssueLevel.Error, node, "Connect a subtitle track to its subtitles input.");
                return;
            }

            var burned = new BurnSubtitlesNode(node).OnlyForced ? ForcedInput(node, subtitles) : subtitles;
            if (burned is null)
                return;
            if (video.Transformed)
                Issue(IssueLevel.Warning, node, "The video was cropped or scaled before this, but subtitles are placed on the original picture: put this before any Crop or Scale.");

            // Bitmap subtitles become a picture stream in the filters; laid over the video while they show
            var label = NewLabel(PortKind.Video);
            _job.Command.Filters.Add($"[{UseRef(video)}][{UseRef(burned)}]{BurnSubtitlesNode.Filter}[{label}]");
            _compiled[(node.Id, "out")] = video with { Ref = label, IsLabel = true, NeedsEncoding = true };
        }
    }
}
