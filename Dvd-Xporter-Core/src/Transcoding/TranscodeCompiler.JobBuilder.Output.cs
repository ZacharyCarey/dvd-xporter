using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

public static partial class TranscodeCompiler
{
    // Codec nodes and the output's options: encoders, their settings, and each track's metadata.
    private sealed partial class JobBuilder
    {
        private void CompileCodec(CanvasNode node)
        {
            var link = canvas.LinkInto(node.Id, "in");
            if (link is null)
            {
                Issue(IssueLevel.Error, node, "Connect its input.");
                return;
            }

            var stream = Resolve(link.FromNode, link.FromPort);
            if (stream is null)
                return;
            if (stream.Codec is not null)
            {
                Issue(IssueLevel.Error, node, "This track already went through a codec; a track can only be encoded once.");
                return;
            }

            // Interlaced or telecined video encoded as it is keeps its combing on every progressive screen
            if (node.Type == NodeTypes.VideoCodec && !stream.Deinterlaced && stream.Picture?.Advice is not null)
                Issue(IssueLevel.Warning, node, $"The video is {stream.Picture.Summary.ToLowerInvariant()}. {stream.Picture.Advice}");

            // HDR needs a codec and bit depth that keep it
            if (node.Type == NodeTypes.VideoCodec && stream.Picture is { IsHdr: true })
            {
                var video = new VideoCodecNode(node);
                if (video.Codec == "libx264")
                    Issue(IssueLevel.Warning, node, "The video is HDR, which H.264 can't keep: use H.265 or AV1, or add a Dynamic Range node to tone map it to SDR.");
                else if (video.PixelFormat == "yuv420p")
                    Issue(IssueLevel.Warning, node, "The video is HDR, which needs 10-bit: set Bit depth to 10-bit or Same as source.");
            }

            _compiled[(node.Id, "out")] = stream with { Codec = node };
        }

        /// <summary>Adds a track's encoder options and returns the encoder's name.</summary>
        private string AddCodecOptions(CanvasNode codec, int i)
        {
            if (codec.Type == NodeTypes.VideoCodec)
            {
                var video = new VideoCodecNode(codec);
                _job.Command.OutputOptions.AddRange(video.Options(i));
                return video.Codec;
            }

            var audio = new AudioCodecNode(codec);
            _job.Command.OutputOptions.AddRange(audio.Options(i));
            return audio.Codec;
        }

        private void AddTrackMetadata(OutputTrack track, int i)
        {
            var options = _job.Command.OutputOptions;
            if (track.Title is not null)
            {
                options.AddRange([$"-metadata:s:{i}", $"title={track.Title}"]);
                // MP4 keeps a track's name only as its handler name
                if (_job.Extension == "mp4")
                    options.AddRange([$"-metadata:s:{i}", $"handler_name={track.Title}"]);
            }
            if (track.Language is not null)
                options.AddRange([$"-metadata:s:{i}", $"language={track.Language}"]);

            var flags = new List<string>();
            if (track.Default)
                flags.Add("default");
            if (track.Forced)
                flags.Add("forced");
            if (track.HearingImpaired)
                flags.Add("hearing_impaired");
            if (track.VisualImpaired)
                flags.Add("visual_impaired");
            if (track.Original)
                flags.Add("original");
            if (track.Commentary)
                flags.Add("comment");
            if (track.Descriptions)
                flags.Add("descriptions");
            options.AddRange([$"-disposition:{i}", flags.Count > 0 ? string.Join("+", flags) : "0"]);
        }

        private static string CodecName(string codec) => codec switch
        {
            "libx264" => "H.264",
            "libx265" => "H.265",
            "libsvtav1" => "AV1",
            "ffv1" => "FFV1",
            "aac" => "AAC",
            "libopus" => "Opus",
            "ac3" => "AC-3",
            "eac3" => "E-AC-3",
            "flac" => "FLAC",
            "libmp3lame" => "MP3",
            _ => codec,
        };
    }
}
