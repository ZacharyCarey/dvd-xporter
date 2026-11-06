using DvdXporter.Core.Emby;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Transcoding.Nodes;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// Turns a canvas into ffmpeg jobs: one per Output File node, reading back along its links. Problems
/// (missing links, a filter after a codec, loops) become <see cref="GraphIssue"/>s instead of exceptions.
/// </summary>
public static partial class TranscodeCompiler
{
    // An output file's per-track options, as settings "t0.title", "t0.language", ...
    public const string TitleOption = "title";
    public const string LanguageOption = "language";
    public const string DefaultOption = "default";
    public const string ForcedOption = "forced";
    public const string HearingImpairedOption = "hearing-impaired";
    public const string VisualImpairedOption = "visual-impaired";
    public const string OriginalOption = "original";
    public const string CommentaryOption = "commentary";
    public const string DescriptionsOption = "descriptions";

    public static string TrackOptionKey(string track, string option) => $"{track}.{option}";

    public static CompileResult Compile(TranscodeCanvas canvas, ICanvasContext context, IClipFiles files)
    {
        var result = new CompileResult();

        // Templates and custom nodes become the nodes they stand for; problems in those map back below
        var expanded = CanvasExpander.Expand(canvas, context.Project);
        var graph = expanded.Canvas;
        var expandedContext = new ExpandedContext(context, graph);
        var issues = result.RawIssues;

        foreach (var node in graph.Nodes)
        {
            if (NodeTypes.Get(node.Type) is null)
                issues.Add(new GraphIssue(IssueLevel.Warning, node.Id, $"\"{node.Type}\" nodes aren't known to this version and are ignored."));
        }

        CheckLinkKinds(graph, expandedContext, issues);

        foreach (var output in graph.Nodes.Where(n => NodeTypes.IsOutput(n.Type)))
        {
            var builder = new JobBuilder(graph, expandedContext, files, output, issues);
            var job = output.Type switch
            {
                NodeTypes.ImageGallery => builder.BuildGallery(),
                _ => builder.Build(),
            };
            if (job is not null)
            {
                result.Jobs.Add(job.Value.Job);
                result.JobNodes[output.Id] = job.Value.Nodes;
            }
        }

        foreach (var export in graph.Nodes.Where(n => n.Type == NodeTypes.Export))
        {
            var target = new ExportNode(export).Target;
            if (graph.LinkInto(export.Id, NodeTypes.FilePort) is null)
                issues.Add(new GraphIssue(IssueLevel.Warning, export.Id, "Nothing is connected to this export."));
            // A template's library items are its videos', picked on the Template node
            else if (canvas.Kind == CanvasKind.Template)
                continue;
            else if (string.IsNullOrEmpty(target))
                issues.Add(new GraphIssue(IssueLevel.Error, export.Id, "Pick the library item this file is for."));
            else if (NodeTypes.IsExtraTarget(target))
            {
                var extra = LibraryNaming.FindExtra(target, context.Project);
                if (extra is null)
                    issues.Add(new GraphIssue(IssueLevel.Error, export.Id, "This extra was removed in the Information step. Pick another library item."));
                else if (string.IsNullOrWhiteSpace(extra.Name))
                    issues.Add(new GraphIssue(IssueLevel.Error, export.Id, "Give this extra a name in the Information step."));
                else if (LibraryNaming.BelongsToRemoved(extra, context.Project))
                    issues.Add(new GraphIssue(IssueLevel.Error, export.Id,
                        "This extra belongs to a season or episode that's gone from the show. Pick where it belongs in the Information step."));
            }
        }

        // Shown on the canvas's own nodes: a problem inside a template row or custom node goes on the Template or
        // custom node, saying where. A node shared by several outputs reports its problems once.
        // Hints that every row of a template shares are shown once.
        var rowHints = new Dictionary<(int Node, string Message), HashSet<string>>();
        var shown = expanded.Issues.Concat(issues.Select(issue =>
        {
            var id = issue.NodeId;
            if (id is null || expanded.OriginOf(id.Value) is not { Prefix: { } prefix } origin)
                return issue;
            var node = graph.FindNode(id.Value);
            var title = node is not null ? NodeTypes.Get(node.Type)?.Title ?? node.Type : "";
            if (issue.Level == IssueLevel.Hint)
            {
                var key = (origin.NodeId, $"{title}: {issue.Message}");
                if (!rowHints.TryGetValue(key, out var rows))
                    rowHints[key] = rows = [];
                rows.Add(prefix);
                return issue with { NodeId = origin.NodeId, Message = key.Item2 };
            }

            return issue with { NodeId = origin.NodeId, Message = $"{prefix}{title}: {issue.Message}" };
        })).ToList();
        result.Issues.AddRange(shown.Distinct()
            .Select(i => i.NodeId is not null && rowHints.GetValueOrDefault((i.NodeId.Value, i.Message))?.Count is > 0 and var count
                ? i with { Message = $"{(count == 1 ? "1 video" : $"{count} videos")} › {i.Message}" }
                : i)
            .OrderByDescending(i => i.Level));
        foreach (var ((node, row), outputs) in expanded.RowOutputs)
            result.RowOutputs[(node, row)] = outputs;

        // Expansion problems stop the outputs they affect
        foreach (var issue in expanded.Issues.Where(i => i.Level == IssueLevel.Error))
            issues.Add(issue);

        foreach (var job in result.Jobs)
        {
            var nodes = result.JobNodes.GetValueOrDefault(job.Output.Id) ?? [];
            result.JobIssues[job.Output.Id] = issues
                .Where(i => i.NodeId is not null && (nodes.Contains(i.NodeId.Value) || i.NodeId.Value == job.Export?.Id))
                .Select(i =>
                {
                    var node = graph.FindNode(i.NodeId!.Value);
                    var title = node is null ? "" : $"{NodeTypes.Get(node.Type)?.Title ?? node.Type}: ";
                    return i with { NodeId = expanded.OriginOf(i.NodeId.Value).NodeId, Message = title + i.Message };
                })
                .Distinct()
                .OrderByDescending(i => i.Level)
                .ToList();
        }

        return result;
    }

    /// <summary>
    /// A job that runs the video coming out of a node's port through an analysis filter (e.g. cropdetect), writing
    /// nothing; see <see cref="TranscodeJob.BuildArguments"/> for running part of it. Null, with
    /// <paramref name="problem"/> saying why, if that video can't be read.
    /// </summary>
    /// <param name="asking">The node the probe is for, which problems are reported on.</param>
    /// <param name="outputOptions">How it's written, if it writes something (e.g. one picture); otherwise nothing is.</param>
    public static TranscodeJob? CompileProbe(TranscodeCanvas canvas, ICanvasContext context, IClipFiles files, int asking,
        int fromNode, string fromPort, string filter, out string? problem, IReadOnlyList<string>? outputOptions = null)
    {
        var expanded = CanvasExpander.Expand(canvas, context.Project);
        var issues = new List<GraphIssue>();
        var askingNode = expanded.Canvas.FindNode(asking) ?? new CanvasNode { Id = asking };
        var job = new JobBuilder(expanded.Canvas, new ExpandedContext(context, expanded.Canvas), files, askingNode, issues)
            .BuildProbe(fromNode, fromPort, filter, outputOptions ?? ["-f", "null"]);
        problem = job is null ? issues.FirstOrDefault(i => i.Level == IssueLevel.Error)?.Message ?? "The video can't be read." : null;
        return job;
    }

    /// <summary>
    /// Flags wires whose ends no longer agree on what they carry, e.g. after a Custom Input is changed from video
    /// to audio while wired to a Video Codec.
    /// </summary>
    private static void CheckLinkKinds(TranscodeCanvas graph, ICanvasContext context, List<GraphIssue> issues)
    {
        var ports = new Dictionary<int, NodePorts>();
        NodePorts PortsOf(CanvasNode node)
        {
            if (!ports.TryGetValue(node.Id, out var nodePorts))
                ports[node.Id] = nodePorts = NodeTypes.Resolve(node.Type, context.Project)?.GetPorts(node, context) ?? new NodePorts([], []);
            return nodePorts;
        }

        foreach (var link in graph.Links)
        {
            var from = graph.FindNode(link.FromNode);
            var to = graph.FindNode(link.ToNode);
            if (from is null || to is null)
                continue;
            var fromKind = PortsOf(from).Outputs.FirstOrDefault(p => p.Key == link.FromPort)?.Kind;
            var toKind = PortsOf(to).Inputs.FirstOrDefault(p => p.Key == link.ToPort)?.Kind;
            if (fromKind is null || toKind is null)
                continue;

            bool fits = fromKind == toKind || (toKind == PortKind.Stream && fromKind is PortKind.Video or PortKind.Audio or PortKind.Subtitle);
            if (!fits)
            {
                issues.Add(new GraphIssue(IssueLevel.Error, to.Id,
                    $"A wire brings {NodeType.KindName(fromKind.Value).ToLowerInvariant()} into an input that takes {NodeType.KindName(toKind.Value).ToLowerInvariant()}; reconnect it."));
            }
        }
    }

    /// <summary>A context whose canvas is the expanded one, for node rows that read links (output tracks, chapters).</summary>
    private sealed class ExpandedContext(ICanvasContext inner, TranscodeCanvas canvas) : ICanvasContext
    {
        public Project Project => inner.Project;

        public TranscodeCanvas Canvas => canvas;

        public Clips.DiscContents? GetContents(ProjectDisc disc) => inner.GetContents(disc);

        public IReadOnlyCollection<int>? GetPresentTracks(ProjectDisc disc, IReadOnlyList<PlaylistEntry> entries) =>
            inner.GetPresentTracks(disc, entries);

        public PictureAnalysis? GetPicture(CanvasNode source) => inner.GetPicture(source);

        public IReadOnlyDictionary<int, SubtitleCount>? GetSubtitleCounts(CanvasNode source) => inner.GetSubtitleCounts(source);
    }

    /// <summary>A compiled stream: where ffmpeg finds it, and what's known about it.</summary>
    /// <param name="Ref">An input stream ("0:a:1") or a filter output label ("f3").</param>
    /// <param name="IsLabel">Whether <see cref="Ref"/> is a filter output, which can only be used once (see <see cref="JobBuilder.Use"/>).</param>
    /// <param name="NeedsEncoding">Whether it's been filtered or generated, so it can't be copied.</param>
    /// <param name="Codec">The codec node it passed through, if any.</param>
    /// <param name="VariableRate">Whether its frames come at a variable rate, so the file is written that way.</param>
    private sealed record Compiled(
        string Ref,
        bool IsLabel,
        PortKind Kind,
        bool NeedsEncoding,
        CanvasNode? Codec,
        string? Language,
        string Description,
        PreciseTime? Duration,
        bool IsBitmapSubtitle = false,
        int? Input = null,
        PictureAnalysis? Picture = null,
        bool Deinterlaced = false,
        SourceTrack? Origin = null,
        bool ForcedOnly = false,
        bool Transformed = false,
        bool VariableRate = false);

    /// <summary>The disc track a stream comes straight from, for working with its subtitles.</summary>
    private sealed record SourceTrack(ProjectDisc Disc, IReadOnlyList<PlaylistEntry> Entries, Clips.ClipTrack Track, PreciseTime? Start, PreciseTime? End);

    private sealed partial class JobBuilder(TranscodeCanvas canvas, ICanvasContext context, IClipFiles files, CanvasNode output, List<GraphIssue> issues)
    {
        private readonly Dictionary<(int Node, string Port), Compiled?> _compiled = [];

        private readonly Dictionary<int, int> _inputIndex = [];

        private readonly HashSet<int> _visiting = [];
        // The source tracks checked by CheckFirstClipHas, by node and port
        private readonly HashSet<(int Node, string Port)> _lateChecked = [];

        private readonly HashSet<int> _nodes = [];

        private readonly Dictionary<string, int> _labelUses = [];

        private readonly Dictionary<string, PortKind> _labelKinds = [];

        private int _nextLabel;

        private TranscodeJob _job = null!;

        private bool _failed;

        private void Issue(IssueLevel level, CanvasNode node, string message)
        {
            issues.Add(new GraphIssue(level, node.Id, message));
            if (level == IssueLevel.Error)
                _failed = true;
        }

        /// <summary>The stream coming out of a node's output port, compiling the node (once) if needed.</summary>
        private Compiled? Resolve(int nodeId, string port)
        {
            if (_compiled.TryGetValue((nodeId, port), out var done))
                return CheckFirstClipHas(nodeId, port, done);
            var node = canvas.FindNode(nodeId);
            if (node is null || NodeTypes.Get(node.Type) is null)
                return null;

            _nodes.Add(nodeId);
            if (!_visiting.Add(nodeId))
            {
                Issue(IssueLevel.Error, node, "This node feeds back into itself.");
                return null;
            }

            try
            {
                CompileNode(node, port);
            }
            finally
            {
                _visiting.Remove(nodeId);
            }

            if (_compiled.TryGetValue((nodeId, port), out var compiled))
                return CheckFirstClipHas(nodeId, port, compiled);

            // Either the node couldn't be compiled (it, or something before it, says why), or the port is gone
            // (e.g. the source now plays a clip without that track)
            if (NodeTypes.Get(node.Type)!.GetPorts(node, context).Outputs.All(p => p.Key != port))
            {
                Issue(IssueLevel.Error, node, node.Type switch
                {
                    NodeTypes.TemplateInput => "This video doesn't have a track the template uses: tracks are matched by their ID on the "
                        + "template's sample video. Use the template with another video, or detach this one and link its own tracks.",
                    NodeTypes.Source => "A linked track isn't in this video after all: the disc lists it, but there's no data for it here. Link another track.",
                    _ => "A link comes from an output this node doesn't have any more; reconnect it.",
                });
            }
            _compiled[(nodeId, port)] = null;
            return null;
        }

        /// <summary>The stream linked into one of a node's inputs, or null (with an issue) if nothing is.</summary>
        private Compiled? Input(CanvasNode node, string port, string name, bool rawOnly = true)
        {
            var link = canvas.LinkInto(node.Id, port);
            if (link is null)
            {
                Issue(IssueLevel.Error, node, name.Length > 0 ? $"Connect its {name} input." : "Connect its input.");
                return null;
            }

            var stream = Resolve(link.FromNode, link.FromPort);
            if (stream?.Codec is not null && rawOnly)
            {
                Issue(IssueLevel.Error, node, "Filters have to come before the codec: connect this before the codec node instead.");
                return null;
            }

            return stream;
        }

        /// <summary>Compiles a node's outputs; a Preset's only <paramref name="port"/>, the one asked for.</summary>
        private void CompileNode(CanvasNode node, string port)
        {
            switch (node.Type)
            {
                case NodeTypes.Preset:
                    CompilePreset(node, port);
                    break;
                case NodeTypes.Source or NodeTypes.TemplateInput:
                    CompileSource(node);
                    break;
                case NodeTypes.SolidColor:
                    CompileSolidColor(node);
                    break;
                case NodeTypes.Silence:
                    CompileSilence(node);
                    break;
                case NodeTypes.Concat:
                    CompileConcat(node);
                    break;
                case NodeTypes.Overlay:
                    CompileOverlay(node);
                    break;
                case NodeTypes.VideoCodec or NodeTypes.AudioCodec:
                    CompileCodec(node);
                    break;
                case NodeTypes.ForcedSubtitles:
                    CompileForcedSubtitles(node);
                    break;
                case NodeTypes.BurnSubtitles:
                    CompileBurnSubtitles(node);
                    break;
                case NodeTypes.SubtitleText:
                    CompileSubtitleText(node);
                    break;
                default:
                    CompileFilter(node);
                    break;
            }
        }

        private string NewLabel(PortKind kind)
        {
            var label = $"f{_nextLabel++}";
            _labelKinds[label] = kind;
            return label;
        }

        /// <summary>
        /// How a filter refers to a stream it reads: "0:v:0" for an input stream (usable any number of times),
        /// or a placeholder for a filter output, resolved by <see cref="FinishLabels"/>.
        /// </summary>
        private string UseRef(Compiled stream) => stream.IsLabel ? Use(stream.Ref) : stream.Ref;

        /// <summary>
        /// A filter output can only be read once, so each use gets its own placeholder name. Once every use is
        /// known, a single use becomes the label itself and several get a split filter.
        /// </summary>
        private string Use(string label)
        {
            var n = _labelUses.GetValueOrDefault(label);
            _labelUses[label] = n + 1;
            return $"{label}u{n}";
        }

        private void FinishLabels()
        {
            // A filter output this file doesn't use (e.g. the audio of a concat shared with another output)
            // still has to go somewhere
            foreach (var (label, kind) in _labelKinds)
            {
                if (!_labelUses.ContainsKey(label))
                    _job.Command.Filters.Add($"[{label}]{(kind == PortKind.Audio ? "anullsink" : "nullsink")}");
            }

            foreach (var (label, uses) in _labelUses)
            {
                if (uses == 1)
                {
                    Replace($"[{label}u0]", $"[{label}]");
                    continue;
                }

                var split = _labelKinds.GetValueOrDefault(label) == PortKind.Audio ? "asplit" : "split";
                var outputs = string.Concat(Enumerable.Range(0, uses).Select(i => $"[{label}u{i}]"));
                _job.Command.Filters.Add($"[{label}]{split}={uses}{outputs}");
            }
        }

        private void Replace(string from, string to)
        {
            for (int i = 0; i < _job.Command.Filters.Count; i++)
                _job.Command.Filters[i] = _job.Command.Filters[i].Replace(from, to);
            for (int i = 0; i < _job.Command.OutputOptions.Count; i++)
                _job.Command.OutputOptions[i] = _job.Command.OutputOptions[i].Replace(from, to);
        }
    }
}
