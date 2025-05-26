using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Transcoding;

// Finding a node's disc in the project.
public static partial class NodeTypes
{
    public static ProjectDisc? FindDisc(Project project, string fingerprint) =>
        project.Discs.FirstOrDefault(d => d.Fingerprint == fingerprint);
}
