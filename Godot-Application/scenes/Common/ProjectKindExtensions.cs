using DvdXporter.Core.Projects;

namespace DvdXporter.UI;

public static class ProjectKindExtensions
{
    public static string DisplayName(this ProjectKind kind) => kind switch
    {
        ProjectKind.Movie => "Movie",
        ProjectKind.TvShow => "TV Show",
        _ => kind.ToString(),
    };
}
