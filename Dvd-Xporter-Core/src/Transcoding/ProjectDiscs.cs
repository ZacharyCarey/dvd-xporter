using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DiscFile = DiscDecoders.DiscFile;

namespace DvdXporter.Core.Transcoding;

/// <summary>
/// A project's discs: where each one is (on this computer or the render server), what's been learnt from it (its
/// <see cref="DiscCache"/>), and its saved contents.
/// </summary>
/// <param name="sources">Where each disc is, as resolved for this user (missing = not found).</param>
public sealed class ProjectDiscs(ProjectFolder folder, IReadOnlyDictionary<ProjectDisc, DiscSource?> sources)
{
    private readonly Dictionary<ProjectDisc, IReadOnlyList<DiscFile>?> _otherFiles = [];

    public Project Project => folder.Project;

    public DiscSource? GetSource(ProjectDisc disc) => sources.GetValueOrDefault(disc);

    public DiscCache Cache(ProjectDisc disc) => folder.GetDiscCache(disc);

    public DiscContents? GetContents(ProjectDisc disc) => Cache(disc).LoadContents();

    /// <summary>Forgets the discs' other files, e.g. after they're found somewhere else.</summary>
    public void Refresh()
    {
        lock (_otherFiles)
            _otherFiles.Clear();
    }

    /// <summary>
    /// The disc's other files, listed off it the first time they're asked for (it's quick: only folders are read).
    /// </summary>
    public IReadOnlyList<DiscFile>? GetDiscFiles(ProjectDisc disc)
    {
        lock (_otherFiles)
        {
            if (_otherFiles.TryGetValue(disc, out var cached))
                return cached;
        }

        var source = GetSource(disc);
        // Unreadable: listed as having none, which says so where they're picked
        var files = source?.ListOtherFiles();

        lock (_otherFiles)
            _otherFiles[disc] = files;
        return files;
    }

    /// <summary>
    /// A clip's subtitle colours in the form ffmpeg's DVD subtitle codec and Matroska use ("palette: rrggbb, ..."), or
    /// null if the disc doesn't give them apart from the subtitles.
    /// </summary>
    public string? SubtitlePalette(ProjectDisc disc, string clip)
    {
        var palette = GetContents(disc)?.FindClip(clip)?.SubtitlePalette;
        return palette is not null ? "palette: " + string.Join(", ", palette) : null;
    }
}
