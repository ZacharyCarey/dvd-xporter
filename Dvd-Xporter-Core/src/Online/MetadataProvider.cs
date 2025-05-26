using DvdXporter.Core.Metadata;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Online;

/// <summary>A match from an online search.</summary>
/// <param name="Id">The provider's ID, e.g. a TMDB or TVDB number or an IMDb "tt" ID.</param>
public sealed record SearchResult(string Id, string Title, int? Year, string? Overview, string? PosterThumbnailUrl);

/// <summary>What to bring in from an online source, and whether it may replace what's already entered.</summary>
public sealed record ImportOptions(bool Details, bool Episodes, bool IncludeSpecials, bool Overwrite);

/// <summary>An online source of movie and show details (TMDB, TVDB, or IMDb data via OMDb).</summary>
public interface IMetadataProvider
{
    string Name { get; }

    Task<IReadOnlyList<SearchResult>> SearchAsync(ProjectKind kind, string query, int? year, CancellationToken cancellationToken = default);

    /// <summary>
    /// Downloads a title's details into a new, detached <see cref="TitleInfo"/>; merge it with
    /// <see cref="MetadataMerger"/>. Image fields are left empty.
    /// </summary>
    Task<TitleInfo> FetchAsync(ProjectKind kind, string id, ImportOptions options, CancellationToken cancellationToken = default);

    /// <summary>Checks the API key with a cheap request. Returns null if it works, or the problem.</summary>
    Task<string?> TestKeyAsync(CancellationToken cancellationToken = default);
}

/// <summary>Problems talking to an online source, with a message suitable for the user.</summary>
public sealed class OnlineMetadataException(string message, Exception? innerException = null) : Exception(message, innerException);
