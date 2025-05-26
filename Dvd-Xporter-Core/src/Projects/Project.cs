using DvdXporter.Core.Metadata;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.Core.Projects;

/// <summary>
/// One movie or TV show, built from a single series of releases (e.g. Season 1 and Season 2 box sets).
/// Other versions of the same content, such as another country's release, belong in their own project.
/// </summary>
public sealed class Project
{
    public Project(string name, ProjectKind kind)
        : this(Guid.NewGuid().ToString("N"), name, kind)
    {
    }

    internal Project(string id, string name, ProjectKind kind)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Info = kind == ProjectKind.Movie ? new MovieInfo { Title = name } : new ShowInfo { Title = name };
    }

    /// <summary>Stays the same for the life of the project, across renames, sharing and re-imports.</summary>
    public string Id { get; }

    public string Name { get; set; }

    public ProjectKind Kind { get; }

    /// <summary>Movie or show details for Emby: a <see cref="MovieInfo"/> or <see cref="ShowInfo"/> matching <see cref="Kind"/>.</summary>
    public TitleInfo Info { get; internal set; }

    public MovieInfo? Movie => Info as MovieInfo;

    public ShowInfo? Show => Info as ShowInfo;

    public List<Release> Releases { get; } = [];

    /// <summary>The transcoding graphs, in the order the user arranged them.</summary>
    public List<TranscodeCanvas> Canvases { get; } = [];

    /// <summary>Every disc in every release, in release order.</summary>
    public IEnumerable<ProjectDisc> Discs => Releases.SelectMany(r => r.Discs);

    public Release? FindRelease(ProjectDisc disc) => Releases.Find(r => r.Discs.Contains(disc));

    /// <summary>
    /// Adds a canvas of <paramref name="kind"/>, named after its kind and the next free number. A template starts with
    /// its Template Input wired through an Output File to an Export, and a custom node with its input and output.
    /// </summary>
    public TranscodeCanvas AddCanvas(CanvasKind kind)
    {
        var baseName = TranscodeCanvas.KindName(kind);
        var number = Canvases.Count(c => c.Kind == kind) + 1;
        while (Canvases.Any(c => c.Name == $"{baseName} {number}"))
            number++;

        var canvas = new TranscodeCanvas { Name = $"{baseName} {number}", Kind = kind };
        if (kind == CanvasKind.Template)
        {
            canvas.Nodes.Add(new CanvasNode { Id = 1, Type = NodeTypes.TemplateInput, X = 0, Y = 0 });
            canvas.Nodes.Add(new CanvasNode { Id = 2, Type = NodeTypes.OutputFile, X = 900, Y = 0 });
            canvas.Nodes.Add(new CanvasNode { Id = 3, Type = NodeTypes.Export, X = 1350, Y = 0 });
            canvas.Links.Add(new CanvasLink(2, "file", 3, "file"));
        }
        else if (kind == CanvasKind.CustomNode)
        {
            canvas.Nodes.Add(new CanvasNode { Id = 1, Type = NodeTypes.CustomInput, X = 0, Y = 0 });
            canvas.Nodes.Add(new CanvasNode { Id = 2, Type = NodeTypes.CustomOutput, X = 800, Y = 0 });
        }

        Canvases.Add(canvas);
        return canvas;
    }

    public ProjectDisc? FindDisc(string fingerprint) => Discs.FirstOrDefault(d => d.Fingerprint == fingerprint);

    /// <summary>
    /// Moves a release <paramref name="by"/> places up (negative) or down the list, which changes which is preferred.
    /// Returns its new place, or null if it can't move that far.
    /// </summary>
    public int? MoveRelease(Release release, int by)
    {
        var index = Releases.IndexOf(release);
        var to = index + by;
        if (index < 0 || to < 0 || to >= Releases.Count)
            return null;

        Releases.RemoveAt(index);
        Releases.Insert(to, release);
        return to;
    }
}
