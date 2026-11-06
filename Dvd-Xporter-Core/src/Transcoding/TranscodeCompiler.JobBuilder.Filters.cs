using System.Text;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

public static partial class TranscodeCompiler
{
    // Filter nodes: the filtergraph chains each one adds.
    private sealed partial class JobBuilder
    {
        /// <summary>A one-in, one-out filter: its input, the filter text, and its output label.</summary>
        private void CompileFilter(CanvasNode node)
        {
            var type = NodeTypes.Get(node.Type)!;
            var kind = type.PassKind;
            if (kind is null)
                return;
            var input = Input(node, "in", "");
            if (input is null)
                return;

            var duration = input.Duration;
            WarnAboutScan(node, input.Picture, node.Type);
            string? filter = node.Type switch
            {
                NodeTypes.Deinterlace => new DeinterlaceNode(node).Filter,
                NodeTypes.InverseTelecine => new InverseTelecineNode(node).Filter,
                NodeTypes.Crop => new CropNode(node).Filter,
                NodeTypes.Scale => new ScaleNode(node).Filter,
                NodeTypes.Aspect => new AspectNode(node).Filter,
                NodeTypes.FrameRate => new FrameRateNode(node).Filter,
                NodeTypes.Downmix => new DownmixNode(node).Filter,
                NodeTypes.Loudness => LoudnessFilter(node),
                NodeTypes.DynamicRangeNode => DynamicRangeFilter(node, input.Picture),
                NodeTypes.Flip => new FlipNode(node).Filter,
                NodeTypes.DrawText => new DrawTextNode(node).Filter,
                NodeTypes.CustomVideo or NodeTypes.CustomAudio => new CustomFilterNode(node).Filter,
                NodeTypes.Volume => new VolumeNode(node).Filter,
                NodeTypes.AudioDelay => DelayFilter(node, ref duration),
                _ => null,
            };
            if (filter is null)
            {
                Issue(IssueLevel.Error, node, node.Type is NodeTypes.CustomVideo or NodeTypes.CustomAudio ? "Enter a filter." : "This filter can't be used here.");
                return;
            }

            var label = NewLabel(kind.Value);
            _job.Command.Filters.Add($"[{UseRef(input)}]{filter}[{label}]");
            _compiled[(node.Id, "out")] = input with
            {
                Ref = label,
                IsLabel = true,
                NeedsEncoding = true,
                Description = input.Description,
                Duration = duration,
                // A custom filter may deinterlace; it isn't second-guessed
                Deinterlaced = input.Deinterlaced || node.Type is NodeTypes.Deinterlace or NodeTypes.InverseTelecine or NodeTypes.CustomVideo,
                Transformed = input.Transformed || node.Type is NodeTypes.Crop or NodeTypes.Scale or NodeTypes.CustomVideo,
                // A Frame Rate node makes it steady again
                VariableRate = node.Type == NodeTypes.InverseTelecine ? new InverseTelecineNode(node).IsVariableRate
                    : node.Type != NodeTypes.FrameRate && input.VariableRate,
                // Tone mapped video is SDR from here on
                Picture = node.Type == NodeTypes.DynamicRangeNode && new DynamicRangeNode(node).TonesMap && input.Picture is not null
                    ? input.Picture with { Range = DynamicRange.Sdr, DolbyVision = false }
                    : input.Picture,
            };
        }

        /// <summary>
        /// A Loudness node's filter. Measuring first, a token that the measurement replaces with one gain to the target
        /// and a peak limiter (see <see cref="LoudnessMeasurer"/>); without a measurement, or set to one pass, its
        /// one-pass filter.
        /// </summary>
        private string LoudnessFilter(CanvasNode node)
        {
            var loudness = new LoudnessNode(node);
            if (!loudness.MeasuresFirst)
                return loudness.OnePassFilter;

            var token = $"{{loudness:{node.Id}}}";
            var link = canvas.LinkInto(node.Id, "in");
            if (link is not null && _job.Measurements.All(m => m.Token != token))
            {
                _job.Measurements.Add(new LoudnessMeasurement(token, link.FromNode, link.FromPort,
                    loudness.TargetLoudness, loudness.PeakLimit, loudness.OnePassFilter));
            }

            return token;
        }

        /// <summary>A Dynamic Range node's filter (see <see cref="DynamicRangeNode.Filter"/>), or nothing for video that isn't HDR.</summary>
        private string DynamicRangeFilter(CanvasNode node, PictureAnalysis? picture)
        {
            if (picture is { IsHdr: false })
            {
                Issue(IssueLevel.Warning, node, "The video isn't HDR, so this does nothing.");
                return "null";
            }

            var range = new DynamicRangeNode(node);
            if (range is { TonesMap: true, UsesGpu: true } && !_job.Command.GlobalOptions.Contains("vulkan"))
                _job.Command.GlobalOptions.AddRange(["-init_hw_device", "vulkan"]);
            return range.Filter;
        }

        /// <summary>
        /// Warns when deinterlacing or inverse telecine (<paramref name="filter"/>: a Deinterlace or Inverse Telecine node's
        /// type) doesn't suit the video it's given.
        /// </summary>
        private void WarnAboutScan(CanvasNode node, PictureAnalysis? picture, string filter)
        {
            if (picture is null)
                return;
            var message = (filter, picture.Type) switch
            {
                // Some frames that looked interlaced may be worth deinterlacing on their own
                (NodeTypes.Deinterlace, PictureType.Progressive or PictureType.Film) when !picture.ContainsInterlaced =>
                    $"The video is {picture.Summary.ToLowerInvariant()}, not interlaced; deinterlacing it only softens the picture.",
                (NodeTypes.Deinterlace, PictureType.Telecined) =>
                    "The video is telecined film: Inverse Telecine restores its original frames, where Deinterlace blurs them.",
                (NodeTypes.InverseTelecine, PictureType.Progressive or PictureType.Film) =>
                    $"The video is {picture.Summary.ToLowerInvariant()}, so there's no telecine to undo.",
                (NodeTypes.InverseTelecine, PictureType.Interlaced) =>
                    "The video is interlaced, not telecined film: use Deinterlace.",
                _ => null,
            };
            if (message is not null)
                Issue(IssueLevel.Warning, node, message);
        }

        private void CompileOverlay(CanvasNode node)
        {
            var main = Input(node, OverlayNode.MainPort, "Main");
            var overlay = Input(node, OverlayNode.OverlayPort, "Overlay");
            if (main is null || overlay is null)
                return;

            var label = NewLabel(PortKind.Video);
            _job.Command.Filters.Add($"[{UseRef(main)}][{UseRef(overlay)}]{new OverlayNode(node).Filter}[{label}]");
            _compiled[(node.Id, "out")] = main with { Ref = label, IsLabel = true, NeedsEncoding = true };
        }

        private void CompileConcat(CanvasNode node)
        {
            var concat = new ConcatNode(node);
            if (concat.Streams == 0)
            {
                Issue(IssueLevel.Error, node, "Join at least one video or audio stream.");
                return;
            }

            var inputs = new StringBuilder();
            var firsts = new Compiled?[concat.Streams];
            var totals = new PreciseTime[concat.Streams];
            bool complete = true;
            for (int s = 0; s < concat.Segments; s++)
            {
                for (int j = 0; j < concat.Streams; j++)
                {
                    var name = $"segment {s + 1} {(concat.IsVideo(j) ? "video" : "audio")}";
                    var stream = Input(node, concat.InputPort(s, j), name);
                    if (stream is null)
                    {
                        complete = false;
                        continue;
                    }

                    firsts[j] ??= stream;
                    totals[j] += stream.Duration ?? PreciseTime.Zero;
                    inputs.Append('[').Append(UseRef(stream)).Append(']');
                }
            }

            if (!complete)
                return;

            var outputs = new StringBuilder();
            var labels = new string[concat.Streams];
            for (int j = 0; j < labels.Length; j++)
            {
                labels[j] = NewLabel(concat.IsVideo(j) ? PortKind.Video : PortKind.Audio);
                outputs.Append('[').Append(labels[j]).Append(']');
            }

            _job.Command.Filters.Add($"{inputs}{concat.Filter}{outputs}");
            for (int j = 0; j < labels.Length; j++)
            {
                _compiled[(node.Id, concat.OutputPort(j))] = firsts[j]! with
                {
                    Ref = labels[j],
                    IsLabel = true,
                    NeedsEncoding = true,
                    Description = $"{concat.Segments} joined segments",
                    Duration = totals[j],
                };
            }
        }

        private static string DelayFilter(CanvasNode node, ref PreciseTime? duration)
        {
            var delay = new AudioDelayNode(node);
            duration = delay.Delayed(duration);
            return delay.Filter;
        }
    }
}
