namespace DvdXporter.Core.Transcoding;

public enum IssueLevel
{
    /// <summary>Worth knowing, but nothing's wrong (e.g. that Emby would transcode on some devices).</summary>
    Hint,
    Warning,
    Error,
}

/// <param name="NodeId">The node the problem is on, if it's on one.</param>
public sealed record GraphIssue(IssueLevel Level, int? NodeId, string Message);

public sealed class CompileResult
{
    public List<TranscodeJob> Jobs { get; } = [];

    /// <summary>Problems, on the canvas's own nodes (those inside templates and custom nodes on the node using them).</summary>
    public List<GraphIssue> Issues { get; } = [];

    /// <summary>The Output File copies each Template node row makes, by (Template node, row); their jobs are in <see cref="Jobs"/>.</summary>
    public Dictionary<(int Node, int Row), List<int>> RowOutputs { get; } = [];

    /// <summary>
    /// Each output's own problems, worst first, by output node id: those on the nodes it reads and on its Export,
    /// each saying which node (on the canvas's own nodes, as <see cref="Issues"/>).
    /// </summary>
    public Dictionary<int, List<GraphIssue>> JobIssues { get; } = [];

    public IReadOnlyList<GraphIssue> IssuesOf(TranscodeJob job) => JobIssues.GetValueOrDefault(job.Output.Id) ?? [];

    public bool HasErrors(int outputNode) =>
        RawIssues.Any(i => i.Level == IssueLevel.Error && i.NodeId is not null && JobNodes.TryGetValue(outputNode, out var nodes) && nodes.Contains(i.NodeId.Value));

    /// <summary>Problems on the expanded canvas's nodes, for telling which outputs they stop.</summary>
    internal List<GraphIssue> RawIssues { get; } = [];

    /// <summary>Every node each output's job reads from, by output node id.</summary>
    internal Dictionary<int, HashSet<int>> JobNodes { get; } = [];
}
