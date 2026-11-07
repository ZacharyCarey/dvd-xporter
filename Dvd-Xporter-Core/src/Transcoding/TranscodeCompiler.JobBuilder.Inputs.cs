using DvdXporter.Core.Clips;
using DvdXporter.Core.Transcoding.Nodes;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

public static partial class TranscodeCompiler
{
    // Disc Video sources and generated inputs (solid colour, silence): the job's ffmpeg inputs and their streams.
    private sealed partial class JobBuilder
    {
        private void CompileSource(CanvasNode node)
        {
            var source = new SourceNode(node);
            var selection = source.Resolve(context);
            if (selection is null)
            {
                var pickedDisc = source.FindDisc(context.Project);
                Issue(IssueLevel.Error, node, pickedDisc is not null && context.GetContents(pickedDisc) is null
                    ? $"\"{pickedDisc.Label}\" hasn't been read yet. It's read once it's found on this computer: use Locate on the project overview."
                    : "Pick a disc and a video file or playlist.");
                return;
            }

            if (selection.Entries.Count == 0)
            {
                Issue(IssueLevel.Error, node, $"The playlist \"{selection.Name}\" is empty.");
                return;
            }

            var disc = selection.Disc;
            _job.Discs.Add(disc);
            var media = files.GetMedia(disc, selection.Entries);
            if (media is null)
            {
                Issue(IssueLevel.Warning, node, $"\"{disc.Label}\" isn't on this computer. Use Locate on the project overview to find it.");
                _job.MissingDiscs.Add(disc);
                media = new SourceMedia(selection.Entries.Select(e => new ConcatPart(FFmpegSource.FromFile($"<{disc.Label}>/{e.Clip}"), e.InTime.ToFFmpegTime(), e.OutTime.ToFFmpegTime())).ToList());
            }

            var (start, end) = source.Range(context);
            if ((source.StartsByChapter || source.EndsByChapter) && ChapterLists.AllOfSource(node, context) is { Chapters.Count: <= 1 })
            {
                Issue(IssueLevel.Warning, node, "This video has no chapters to choose from, so it plays from the beginning to the end.");
            }
            else if (start is not null && end is not null && end.Value <= start.Value)
            {
                Issue(IssueLevel.Error, node, "The video ends before it starts.");
            }

            int index = _job.Command.Inputs.Count;
            var (input, specifiers) = SourceNode.BuildInput(selection, media, start, end, index);
            _job.Command.Inputs.Add(input);

            PreciseTime? duration = null;
            if (selection.Duration is not null)
            {
                var full = selection.Duration.Value;
                duration = (end is not null && end.Value < full ? end.Value : full) - (start ?? PreciseTime.Zero);
            }

            var picture = context.GetPicture(node);
            for (int t = 0; t < selection.Tracks.Count; t++)
            {
                var track = selection.Tracks[t];
                _compiled[(node.Id, SourceNode.TrackPortKey(track))] = new Compiled(
                    specifiers[t], false, SourceNode.TrackPortKind(track.Kind), false, null, track.Language,
                    selection.TrackLabel(track), duration, track.Kind == ClipTrackKind.Subtitle, index,
                    track.Kind == ClipTrackKind.Video ? picture : null,
                    Origin: new SourceTrack(disc, selection.Entries, track, start, end, selection.TrackName(track)));
            }
        }

        /// <summary>
        /// Says when a playlist's audio track is used but its first video file has none of it (e.g. a silent logo before
        /// the feature): ffmpeg learns what's in a playlist from its first file, so it can't tell what the track is.
        /// Every use of a source's track resolves its port, so it's checked there, once.
        /// </summary>
        private Compiled? CheckFirstClipHas(int nodeId, string port, Compiled? stream)
        {
            var origin = stream?.Origin;
            if (stream is null || origin is null || stream.Kind != PortKind.Audio || origin.Entries.Count < 2)
                return stream;
            var node = canvas.FindNode(nodeId);
            if (node is null || node.Type is not (NodeTypes.Source or NodeTypes.TemplateInput) || !_lateChecked.Add((nodeId, port)))
                return stream;

            var inFirst = context.GetPresentTracks(origin.Disc, [origin.Entries[0]]);
            if (inFirst is not null && !inFirst.Contains(origin.Track.StreamId))
            {
                Issue(IssueLevel.Error, node, $"The audio track \"{origin.Track.Label}\" can't be used: the playlist's first video file (\"{origin.Entries[0].Clip}\") "
                    + "has none of its sound, so ffmpeg can't tell what it is. Make a playlist without that file in the Clip Renamer, and "
                    + "join a Silence node in front of its sound with a Concat node if it needs to cover that time.");
            }

            return stream;
        }

        private void CompileSolidColor(CanvasNode node)
        {
            var solid = new SolidColorNode(node);
            int index = _job.Command.Inputs.Count;
            _job.Command.Inputs.Add(new GeneratedInput(solid.Graph));
            _compiled[(node.Id, "out")] = new Compiled($"{index}:v:0", false, PortKind.Video, true, null, null,
                $"Solid color {solid.Width}×{solid.Height}", solid.Duration);
        }

        private void CompileSilence(CanvasNode node)
        {
            var silence = new SilenceNode(node);
            int index = _job.Command.Inputs.Count;
            _job.Command.Inputs.Add(new GeneratedInput(silence.Graph));
            _compiled[(node.Id, "out")] = new Compiled($"{index}:a:0", false, PortKind.Audio, true, null, null, "Silence", silence.Duration);
        }
    }
}
