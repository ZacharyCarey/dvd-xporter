namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>
/// A Concat node: plays segments one after another. Each segment has the same number of video and audio inputs, and
/// the joined streams come out of it.
/// </summary>
public sealed class ConcatNode(CanvasNode node)
{
    public const string SegmentsKey = "segments";
    public const string VideoKey = "video";
    public const string AudioKey = "audio";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Concat,
        Title = "Concat",
        Category = NodeCategory.Combine,
        Description = "Plays segments one after another. Every segment needs the same picture size and aspect.",
        Settings =
        [
            new(SegmentsKey, "Segments", SettingKind.Integer, "2", Min: 2, Max: 64, Stepper: true),
            new(VideoKey, "Video streams", SettingKind.Integer, "1", Min: 0, Max: 4, Stepper: true),
            new(AudioKey, "Audio streams", SettingKind.Integer, "1", Min: 0, Max: 16, Stepper: true),
        ],
        Ports = (n, _) => new ConcatNode(n).Ports(),
    };

    public int Segments => Math.Clamp(node.GetInt(SegmentsKey, 2), 2, 64);

    /// <summary>How many video streams each segment has.</summary>
    public int VideoStreams => Math.Clamp(node.GetInt(VideoKey, 1), 0, 4);

    /// <summary>How many audio streams each segment has.</summary>
    public int AudioStreams => Math.Clamp(node.GetInt(AudioKey, 1), 0, 16);

    /// <summary>Every stream of a segment, in order: the videos, then the audio.</summary>
    public int Streams => VideoStreams + AudioStreams;

    public bool IsVideo(int stream) => stream < VideoStreams;

    /// <summary>A segment's input for one of its streams, e.g. "s0.v0" or "s1.a0".</summary>
    public string InputPort(int segment, int stream) => $"s{segment}.{StreamName(stream)}";

    /// <summary>A joined stream's output, e.g. "v0" or "a1".</summary>
    public string OutputPort(int stream) => StreamName(stream);

    private string StreamName(int stream) => IsVideo(stream) ? $"v{stream}" : $"a{stream - VideoStreams}";

    /// <summary>Each segment's inputs, segment by segment, and the joined streams.</summary>
    public NodePorts Ports()
    {
        var inputs = new List<NodePort>();
        for (int s = 0; s < Segments; s++)
        {
            for (int j = 0; j < Streams; j++)
                inputs.Add(new NodePort(InputPort(s, j), StreamLabel(j), IsVideo(j) ? PortKind.Video : PortKind.Audio));
        }

        var outputs = Enumerable.Range(0, Streams)
            .Select(j => new NodePort(OutputPort(j), StreamLabel(j), IsVideo(j) ? PortKind.Video : PortKind.Audio))
            .ToList();
        return new NodePorts(inputs, outputs);
    }

    // "Video", or "Video 2" when there are several
    private string StreamLabel(int stream) => IsVideo(stream)
        ? VideoStreams == 1 ? "Video" : $"Video {stream + 1}"
        : AudioStreams == 1 ? "Audio" : $"Audio {stream - VideoStreams + 1}";

    /// <summary>The ffmpeg filter that joins the segments, without its input and output labels.</summary>
    public string Filter => $"concat=n={Segments}:v={VideoStreams}:a={AudioStreams}";
}
