using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

public static partial class TranscodeCompiler
{
    // Preset nodes: their video's filters, and which tracks they encode.
    private sealed partial class JobBuilder
    {
        // Presets whose video goes through no filters, so the video going in can be compared with what's encoded
        private readonly HashSet<int> _unfilteredPresets = [];

        // Presets already said to suit another container, so it's said once
        private readonly HashSet<int> _containerHinted = [];

        /// <summary>
        /// One of a Preset node's outputs: its video through the preset's filters, or one of its audio tracks, marked to be
        /// encoded with the preset's settings (audio it copies passes as it is). Only the output asked for is compiled, so
        /// a file taking one of its tracks reads only that one.
        /// </summary>
        private void CompilePreset(CanvasNode node, string port)
        {
            var preset = new PresetNode(node);
            if (port == PresetNode.VideoOut)
            {
                var video = Input(node, PresetNode.VideoIn, "Video");
                if (video is null)
                    return;

                WarnAboutPresetPicture(node, preset, video.Picture);
                if (preset.KeepsHdr && video.Picture is { DolbyVision: true })
                    Issue(IssueLevel.Hint, node, "Dolby Vision isn't kept: the video keeps its HDR10 base, which every HDR screen shows.");

                var filters = preset.VideoFilters(video.Picture);
                var stream = video;
                if (filters.Count > 0)
                {
                    var label = NewLabel(PortKind.Video);
                    _job.Command.Filters.Add($"[{UseRef(video)}]{string.Join(',', filters)}[{label}]");
                    stream = video with
                    {
                        Ref = label,
                        IsLabel = true,
                        NeedsEncoding = true,
                        Deinterlaced = video.Deinterlaced || preset.Picture != PresetNode.NoPicture,
                        Transformed = video.Transformed || preset.FitsHd,
                        // Tone mapped video is SDR from here on
                        Picture = preset.TonesMap(video.Picture) ? video.Picture! with { Range = DynamicRange.Sdr, DolbyVision = false } : video.Picture,
                    };
                }
                else
                {
                    _unfilteredPresets.Add(node.Id);
                }

                _compiled[(node.Id, port)] = stream with { Codec = node };
                return;
            }

            var index = PresetNode.AudioOutIndex(port);
            if (index is null)
                return;
            var audio = Input(node, PresetNode.AudioIn(index.Value), $"Audio {index.Value + 1}");
            if (audio is null)
                return;
            if (preset.CopiesAudio(audio.Origin?.Track, audio.NeedsEncoding))
            {
                _compiled[(node.Id, port)] = audio;
                return;
            }

            // A Blu-ray TrueHD track carries an AC-3 core under the same ID, which ffmpeg opens as a stream of its own: mapped
            // by ID, both would be encoded. A filter reads only the first, the TrueHD.
            if (!audio.IsLabel && audio.Origin?.Track.Codec == "TruHD")
            {
                var label = NewLabel(PortKind.Audio);
                _job.Command.Filters.Add($"[{UseRef(audio)}]anull[{label}]");
                audio = audio with { Ref = label, IsLabel = true };
            }

            _compiled[(node.Id, port)] = audio with { Codec = node };
        }

        /// <summary>Warns when a preset's Picture setting doesn't suit the video, as Deinterlace and Inverse Telecine nodes do.</summary>
        private void WarnAboutPresetPicture(CanvasNode node, PresetNode preset, PictureAnalysis? picture)
        {
            if (preset.Picture == PresetNode.Deinterlace)
            {
                WarnAboutScan(node, picture, NodeTypes.Deinterlace);
                return;
            }

            if (preset.Picture == PresetNode.InverseTelecine)
            {
                WarnAboutScan(node, picture, NodeTypes.InverseTelecine);
                return;
            }

            // Encoded as it is, interlaced or telecined video keeps its combing on every progressive screen
            var choice = picture?.Type switch
            {
                PictureType.Telecined => "Inverse telecine",
                PictureType.Interlaced or PictureType.Mixed => "Deinterlace",
                _ => null,
            };
            if (choice is not null)
                Issue(IssueLevel.Warning, node, $"The video is {picture!.Summary.ToLowerInvariant()}: set Picture to {choice}.");
        }

        /// <summary>A hint when a track encoded by a preset goes into a file of another container than it's made for.</summary>
        private void HintPresetContainer(Compiled stream)
        {
            var codec = stream.Codec;
            if (codec is null || codec.Type != NodeTypes.Preset)
                return;
            var preset = new PresetNode(codec);
            if (preset.Container != _job.Extension && _containerHinted.Add(codec.Id))
            {
                var name = PresetNode.Type.FindSetting(PresetNode.PresetKey)!.Choices!.First(c => c.Value == preset.Preset).Label;
                Issue(IssueLevel.Hint, output, $"The {name} preset is made for {preset.Container.ToUpperInvariant()} files, and this is "
                    + $"{_job.Extension.ToUpperInvariant()}.");
            }
        }
    }
}
