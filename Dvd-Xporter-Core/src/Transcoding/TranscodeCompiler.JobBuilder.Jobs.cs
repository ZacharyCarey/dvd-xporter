using DvdXporter.Core.Emby;
using DvdXporter.Core.Transcoding.Nodes;
using FFmpeg;

namespace DvdXporter.Core.Transcoding;

public static partial class TranscodeCompiler
{
    // What each kind of output makes: an Output File's video file, an Image Gallery's pictures, and probes for
    // analysing a stream.
    private sealed partial class JobBuilder
    {
        // How long a picture must stay on screen to be kept in a gallery
        private static readonly PreciseTime GalleryMinimumHold = new(0, 0, 0, 500_000_000);

        /// <summary>
        /// A job that decodes the stream out of a node's port through <paramref name="filter"/>, for analysing it (the
        /// filter logs what it finds, and <paramref name="outputOptions"/> write nothing) or saving a picture of it.
        /// <c>output</c> is the node asking.
        /// </summary>
        public TranscodeJob? BuildProbe(int fromNode, string fromPort, string filter, IReadOnlyList<string> outputOptions)
        {
            _job = new TranscodeJob { Output = output, Name = "Probe", Extension = "null" };
            var stream = Resolve(fromNode, fromPort);
            if (stream is null || _failed)
                return null;
            if (stream.Kind is not (PortKind.Video or PortKind.Audio))
            {
                Issue(IssueLevel.Error, output, "This needs video or audio.");
                return null;
            }

            var label = NewLabel(stream.Kind);
            var window = stream.Kind == PortKind.Audio ? FFmpegCommand.AudioWindow : FFmpegCommand.VideoWindow;
            _job.Command.Filters.Add($"[{UseRef(stream)}]{window}{filter}[{label}]");
            _job.Command.OutputOptions.AddRange(["-map", $"[{Use(label)}]", .. outputOptions]);
            FinishLabels();
            _job.Duration = stream.Duration;
            return _job;
        }

        /// <summary>An Output File: its tracks, each copied or encoded, with its chapters and tags, as one video file.</summary>
        public (TranscodeJob Job, HashSet<int> Nodes)? Build()
        {
            _nodes.Add(output.Id);
            var export = FindExport("An output file");
            var file = new OutputFileNode(output);
            _job = OutputJob(file, export);

            var streams = ResolveTracks();
            if (_failed && streams.Count == 0)
                return (_job, _nodes);

            // Each track starts out as the default if it's the first of its kind
            var kinds = new HashSet<PortKind>();
            for (int i = 0; i < streams.Count; i++)
                AddTrack(file, i, streams[i].Link, streams[i].Stream, kinds.Add(streams[i].Stream.Kind));

            AddFileMetadata(file, export);
            FinishLabels();
            SetDuration(streams);
            return (_job, _nodes);
        }

        /// <summary>
        /// An Image Gallery: the video's pictures as numbered images in a folder (its Gallery extra's), at square pixels
        /// so wide DVD pictures aren't squashed: one per picture shown. Repeated frames are dropped here, and the frames
        /// of fades once it's made (see <see cref="GalleryImages.Tidy"/>).
        /// </summary>
        public (TranscodeJob Job, HashSet<int> Nodes)? BuildGallery()
        {
            _nodes.Add(output.Id);
            var export = FindExport("An image gallery");

            var gallery = new ImageGalleryNode(output);
            var extension = gallery.Extension;
            string? libraryPath = null;
            if (export is not null)
            {
                if (LibraryNaming.FindExtra(new ExportNode(export).Target, context.Project) is { IsGallery: true })
                    libraryPath = LibraryNaming.RelativePath(export, context.Project, extension);
                else if (!string.IsNullOrEmpty(new ExportNode(export).Target))
                    Issue(IssueLevel.Error, export, "An Image Gallery's pictures go to a Gallery extra. Add one in the Information step "
                        + "(type \"Gallery (images)\"), then pick it here.");
            }

            _job = new TranscodeJob
            {
                Output = output,
                Export = export,
                Name = export is not null ? LibraryNaming.Describe(export, context.Project) : $"Image Gallery {output.Id}",
                Extension = extension,
                LibraryPath = libraryPath,
                IsImageSequence = true,
                MinimumHold = GalleryMinimumHold,
                SkipBlack = gallery.SkipBlack,
            };

            var link = canvas.LinkInto(output.Id, "in");
            if (link is null)
            {
                Issue(IssueLevel.Error, output, "Connect a video to this gallery.");
                return (_job, _nodes);
            }

            var stream = Resolve(link.FromNode, link.FromPort);
            if (stream is null)
                return (_job, _nodes);
            if (stream.Kind != PortKind.Video)
            {
                Issue(IssueLevel.Error, output, "An image gallery takes a video.");
                return (_job, _nodes);
            }

            if (stream.Codec is not null)
            {
                Issue(IssueLevel.Error, output, "Connect the video before any codec: the gallery saves its pictures as images itself.");
                return (_job, _nodes);
            }

            // Repeated frames go now, and the frames of fades once it's made, from what its filters log
            var label = NewLabel(PortKind.Video);
            _job.Command.Filters.Add($"[{UseRef(stream)}]{gallery.Filters}[{label}]");
            _job.Command.OutputOptions.AddRange(["-map", $"[{Use(label)}]", "-fps_mode", "vfr"]);
            _job.Command.OutputOptions.AddRange(gallery.EncoderOptions);
            FinishLabels();
            _job.Duration = stream.Duration;
            return (_job, _nodes);
        }

        /// <summary>The Export the output goes to, if any; an output can only go to one (<paramref name="what"/> says what it is).</summary>
        private CanvasNode? FindExport(string what)
        {
            var exportLinks = canvas.Links.Where(l => l.FromNode == output.Id && l.FromPort == NodeTypes.FilePort).ToList();
            if (exportLinks.Count > 1)
                Issue(IssueLevel.Error, output, $"{what} can only go to one library item.");
            return exportLinks.Select(l => canvas.FindNode(l.ToNode)).FirstOrDefault(n => n?.Type == NodeTypes.Export);
        }

        /// <summary>An Output File's job, named after its library item, with its title and trim.</summary>
        private TranscodeJob OutputJob(OutputFileNode file, CanvasNode? export)
        {
            var extension = file.Extension;
            var libraryPath = export is null ? null : LibraryNaming.RelativePath(export, context.Project, extension);
            if (export is not null && LibraryNaming.FindExtra(new ExportNode(export).Target, context.Project) is { IsGallery: true })
            {
                Issue(IssueLevel.Error, export, "A Gallery extra holds the pictures of an Image Gallery node, not a video file: pick another library item.");
                libraryPath = null;
            }

            return new TranscodeJob
            {
                Output = output,
                Export = export,
                Name = export is not null ? LibraryNaming.Describe(export, context.Project) : $"Output File {output.Id}",
                Extension = extension,
                LibraryPath = libraryPath,
                Title = file.Title is { Length: > 0 } t ? t : export is null ? null : LibraryNaming.Title(export, context.Project),
                OutputStart = file.Start,
                OutputEnd = file.End,
            };
        }

        /// <summary>The streams linked into the Output File's track ports, in track order.</summary>
        private List<(CanvasLink Link, Compiled Stream)> ResolveTracks()
        {
            var trackLinks = canvas.Links
                .Where(l => l.ToNode == output.Id && NodeTypes.TryParseTrackIndex(l.ToPort) is not null)
                .OrderBy(l => NodeTypes.TryParseTrackIndex(l.ToPort))
                .ToList();
            if (trackLinks.Count == 0)
                Issue(IssueLevel.Error, output, "Connect at least one track to this output file.");

            var streams = new List<(CanvasLink Link, Compiled Stream)>();
            foreach (var link in trackLinks)
            {
                var stream = Resolve(link.FromNode, link.FromPort);
                if (stream is not null)
                    streams.Add((link, stream));
            }
            return streams;
        }

        /// <summary>
        /// Output track <paramref name="i"/>: mapped, copied or encoded, with its flags and metadata, and what's wrong
        /// with it. <paramref name="isFirstOfKind"/>: whether it's the file's first track of its kind.
        /// </summary>
        private void AddTrack(OutputFileNode file, int i, CanvasLink link, Compiled stream, bool isFirstOfKind)
        {
            var trackNumber = NodeTypes.TryParseTrackIndex(link.ToPort)!.Value + 1;
            if (stream.NeedsEncoding && stream.Codec is null)
            {
                Issue(IssueLevel.Error, output,
                    $"Track {trackNumber} is filtered or generated, so it has to be encoded: add a codec before the output.");
            }

            // The concat demuxer seeks exactly for decoded tracks, but copied ones start at the packet before
            if (stream.Codec is null && stream.Input is not null && _job.Command.Inputs[stream.Input.Value] is ConcatInput { Start: not null })
            {
                var source = canvas.FindNode(SourceNodeOf(link));
                if (source is not null)
                {
                    Issue(IssueLevel.Warning, source,
                        $"Track {trackNumber} is copied from a playlist with a start time, so it may start a little early. "
                        + "Encode it, or trim at the Output File instead.");
                }
            }

            if (stream.Kind == PortKind.Video && stream.Description.Contains("enhancement layer", StringComparison.Ordinal))
            {
                Issue(IssueLevel.Warning, output, $"Track {trackNumber} is a Dolby Vision enhancement layer, which players can't show on its own: "
                    + "use the main video track.");
            }

            _job.Command.OutputOptions.AddRange(["-map", stream.IsLabel ? $"[{Use(stream.Ref)}]" : stream.Ref]);
            var codec = stream.Codec is not null ? AddCodecOptions(stream, i) : "copy";
            HintPresetContainer(stream);
            // A preset's video can only be compared with what goes in when it doesn't filter it (e.g. scale it)
            if (stream.Kind == PortKind.Video && stream.Codec is not null && _job.QualityReference is null
                && (stream.Codec.Type != NodeTypes.Preset || _unfilteredPresets.Contains(stream.Codec.Id)))
            {
                var encoded = canvas.LinkInto(stream.Codec.Id, "in");
                if (encoded is not null)
                    _job.QualityReference = (i, encoded.FromNode, encoded.FromPort);
            }

            var hdrCodec = stream.Codec;
            // HDR stays 10-bit, which keeps its brightness information (the encoders carry the HDR10 metadata over)
            if (hdrCodec is not null && stream.Picture is { IsHdr: true } && codec is "libx265" or "libsvtav1"
                && new VideoCodecNode(hdrCodec).PixelFormat.Length == 0)
            {
                _job.Command.OutputOptions.AddRange([$"-pix_fmt:{i}", "yuv420p10le"]);
            }
            // Text subtitles go into MP4 as its own kind of text; Matroska takes them as they are
            if (codec == "copy" && stream.Kind == PortKind.Subtitle && !stream.IsBitmapSubtitle && _job.Extension == "mp4")
                codec = "mov_text";
            if (codec is "copy" or "mov_text")
                _job.Command.OutputOptions.AddRange([$"-c:{i}", codec]);
            // Its frames are kept as they come: ffmpeg mustn't repeat or drop any to make the rate steady
            if (stream.VariableRate)
                _job.Command.OutputOptions.AddRange([$"-fps_mode:{i}", "vfr"]);

            var key = link.ToPort;
            bool Flag(string option) => file.TrackFlag(key, option);
            var isDefault = file.TrackOption(key, DefaultOption, "auto") switch
            {
                "yes" => true,
                "no" => false,
                _ => isFirstOfKind && stream.Kind is PortKind.Video or PortKind.Audio,
            };

            var language = file.TrackOption(key, LanguageOption) is { Length: > 0 } lang ? lang : stream.Language;
            var outputTrack = new OutputTrack(
                stream.Kind,
                codec,
                stream.Description + (codec == "copy" ? "" : $" → {CodecName(codec)}"),
                file.TrackOption(key, TitleOption) is { Length: > 0 } trackTitle ? trackTitle : null,
                language,
                isDefault,
                Flag(ForcedOption) || stream.ForcedOnly,
                Flag(HearingImpairedOption),
                Flag(VisualImpairedOption),
                Flag(OriginalOption),
                Flag(CommentaryOption),
                Flag(DescriptionsOption));
            CheckPlayback(trackNumber, stream, codec, outputTrack, _job.Extension);
            _job.Tracks.Add(outputTrack);
            _job.TrackPorts.Add(link.ToPort);
            AddTrackMetadata(outputTrack, i);
        }

        /// <summary>The file's chapters and tags, and how it's laid out.</summary>
        private void AddFileMetadata(OutputFileNode file, CanvasNode? export)
        {
            // Leave the discs' own tags behind (but not the chapters' names, which "-map_metadata -1" would drop too)
            _job.Command.OutputOptions.AddRange(["-map_metadata:g", "-1", "-map_metadata:s", "-1"]);
            AddChapters();
            var title = _job.Title;
            if (title is not null)
                _job.Command.OutputOptions.AddRange(["-metadata", $"title={title}"]);
            if (export is not null && file.LibraryTags)
            {
                foreach (var (key, value) in LibraryNaming.Tags(export, context.Project, _job.Extension))
                    _job.Command.OutputOptions.AddRange(["-metadata", $"{key}={value}"]);
            }

            if (file.Comment is { Length: > 0 } comment)
                _job.Command.OutputOptions.AddRange(["-metadata", $"comment={comment}"]);
            if (_job.Extension == "mkv")
                _job.Command.OutputOptions.AddRange(["-max_interleave_delta", "0"]);
            else
                _job.Command.OutputOptions.AddRange(["-movflags", "+faststart"]);
        }

        /// <summary>The output is as long as its longest track, trimmed by its own start and end.</summary>
        private void SetDuration(List<(CanvasLink Link, Compiled Stream)> streams)
        {
            var duration = streams.Select(s => s.Stream.Duration).Where(d => d is not null).Select(d => d!.Value).DefaultIfEmpty().Max();
            if (duration > PreciseTime.Zero || _job.OutputEnd is not null)
            {
                var end = _job.OutputEnd is not null && (duration == PreciseTime.Zero || _job.OutputEnd.Value < duration) ? _job.OutputEnd.Value : duration;
                _job.Duration = end - (_job.OutputStart ?? PreciseTime.Zero);
            }
        }

        /// <summary>What the container can't hold (errors), and what Emby would transcode on the devices the output is for.</summary>
        private void CheckPlayback(int number, Compiled stream, string codec, OutputTrack track, string extension)
        {
            var copied = codec == "copy";
            int? bitDepth = null;
            if (stream.Kind == PortKind.Video)
            {
                // "Same as source" keeps HDR's 10 bits (and the 10 bits of UHD discs' H.265)
                bitDepth = (stream.Codec is not null ? new VideoCodecNode(stream.Codec).PixelFormat : null) switch
                {
                    "yuv420p10le" => 10,
                    "yuv420p" => 8,
                    _ => stream.Picture is { IsHdr: true } || stream.Origin?.Track.Codec == "H265" ? 10 : 8,
                };
            }

            var playback = new PlaybackTrack(
                number,
                stream.Kind,
                copied ? stream.Origin?.Track.Codec ?? "" : codec,
                copied,
                stream.Kind == PortKind.Video ? stream.Picture : null,
                stream.Deinterlaced,
                bitDepth,
                stream.IsBitmapSubtitle,
                track.Forced || track.Default);
            var unfit = false;
            foreach (var problem in EmbyPlayback.ContainerProblems(playback, extension))
            {
                Issue(IssueLevel.Error, output, problem);
                unfit = true;
            }

            // How a track plays doesn't matter until the file can hold it
            if (unfit)
                return;
            foreach (var (level, message) in EmbyPlayback.Check(playback, EmbyPlayback.ParseDevices(new OutputFileNode(output).Playback)))
                Issue(level, output, message);
        }

        /// <summary>The node a track's stream starts at, following codecs back (copied tracks only pass through codecs).</summary>
        private int SourceNodeOf(CanvasLink link)
        {
            var current = link;
            while (canvas.FindNode(current.FromNode) is { Type: NodeTypes.VideoCodec or NodeTypes.AudioCodec } codec)
            {
                var previous = canvas.LinkInto(codec.Id, "in");
                if (previous is null)
                    break;
                current = previous;
            }

            return current.FromNode;
        }

        /// <summary>The chapters linked into the output, moved to its own timeline (after its start trim).</summary>
        private void AddChapters()
        {
            var link = canvas.LinkInto(output.Id, NodeTypes.ChaptersPort);
            if (link is null)
                return;
            var from = canvas.FindNode(link.FromNode);
            if (from is null)
                return;

            _nodes.Add(from.Id);
            if (ChapterLists.Resolve(from, link.FromPort, context) is not { Chapters.Count: > 0 } set)
            {
                Issue(IssueLevel.Warning, output, "The chapters connected to this output are empty.");
                return;
            }

            _job.Chapters.AddRange(ChapterLists.Trim(set.Chapters, _job.OutputStart ?? PreciseTime.Zero, _job.OutputEnd));
        }
    }
}
