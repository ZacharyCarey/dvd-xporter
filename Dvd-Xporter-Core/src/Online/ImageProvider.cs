using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Online;

/// <summary>An image offered by an online source. <see cref="Url"/> is the full-size original.</summary>
/// <param name="Width">0 when the source doesn't say.</param>
public sealed record OnlineImage(string Url, string ThumbnailUrl, int Width, int Height, string? Language, double Rating);

/// <summary>An online source of artwork (TMDB, TVDB).</summary>
public interface IImageProvider
{
    string Name { get; }

    /// <summary>Whether it has images of a kind for a title, a season (with its number) or an episode (with both).</summary>
    bool HasImages(ImageKind kind, int? season, int? episode);

    /// <summary>The images it has for one kind of artwork, best first. Finds the title by its ID in <paramref name="ids"/>.</summary>
    /// <exception cref="OnlineMetadataException">The title has no ID for this source, or it couldn't be reached.</exception>
    Task<IReadOnlyList<OnlineImage>> GetImagesAsync(ProjectKind kind, ProviderIds ids, int? season, int? episode,
        ImageKind imageKind, CancellationToken cancellationToken = default);
}
