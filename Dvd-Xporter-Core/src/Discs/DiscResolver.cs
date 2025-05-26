using DiscDecoders.Common;
using DvdXporter.Core.Projects;

namespace DvdXporter.Core.Discs;

public enum DiscStatus
{
    Found,
    ProbableMatch,
    Missing,
}

public sealed record DiscResolution(DiscStatus Status, DiscSource? Source, string? Message);

/// <summary>
/// Works out where a project's disc is on this computer, using the project's cache and the Disc Library.
/// </summary>
public sealed class DiscResolver(DiscIdentifier identifier, DiscLibrary library)
{
    private const string ProbableMatchMessage =
        "This disc's fingerprint doesn't match the project. It may be a different release, so check its titles before continuing.";

    /// <summary>
    /// Rechecks one disc. Blocking (may read discs); run it off the UI thread. Updates <paramref name="cache"/>.
    /// </summary>
    public DiscResolution Resolve(ProjectDisc disc, ProjectCache cache)
    {
        var cached = cache.Get(disc.Fingerprint);
        DiscSource? cachedSource = null;
        string? cachedProblem = null;
        if (cached is not null)
        {
            try
            {
                cachedSource = identifier.Identify(cached.Path);
            }
            catch (DiscIdentificationException ex)
            {
                cachedProblem = ex.Message;
            }

            if (cachedSource?.Fingerprint == disc.Fingerprint)
                return new DiscResolution(DiscStatus.Found, cachedSource, null);
        }

        var indexed = library.Find(disc.Fingerprint);
        // An exact match in the library beats a manually chosen near-match.
        // Re-identify it, as the library may be stale if the disc moved since the last scan.
        var librarySource = indexed is not null ? TryIdentify(indexed.Path) : null;
        if (librarySource is not null && librarySource.Fingerprint == disc.Fingerprint)
        {
            cache.Set(disc.Fingerprint, new ProjectCache.DiscLocation(librarySource.Path, IsManual: false));
            return new DiscResolution(DiscStatus.Found, librarySource, null);
        }

        // TODO: Compare title layouts so library discs that nearly match can also be offered as probable matches.
        if (cached is { IsManual: true })
        {
            // Keep manual choices even when unreadable; the drive may just be disconnected.
            if (cachedSource is not null && cachedSource.Format == disc.Format)
                return new DiscResolution(DiscStatus.ProbableMatch, cachedSource, ProbableMatchMessage);

            return new DiscResolution(DiscStatus.Missing, null,
                cachedProblem ?? $"The disc chosen at \"{cached.Path}\" is no longer a {FormatName(disc.Format)} rip.");
        }

        if (cached is not null)
            cache.Remove(disc.Fingerprint);

        return new DiscResolution(DiscStatus.Missing, null,
            "Not found in the Disc Library. Use Locate to choose it manually.");
    }

    /// <summary>
    /// Points a disc at a path the user chose. Blocking; run it off the UI thread.
    /// </summary>
    /// <exception cref="DiscIdentificationException">The path isn't a usable rip for this disc.</exception>
    public DiscResolution Locate(ProjectDisc disc, string path, ProjectCache cache)
    {
        var source = identifier.Identify(path);
        if (source.Format != disc.Format)
        {
            throw new DiscIdentificationException(
                $"That is a {FormatName(source.Format)} rip, but \"{disc.Label}\" is a {FormatName(disc.Format)}.");
        }

        cache.Set(disc.Fingerprint, new ProjectCache.DiscLocation(source.Path, IsManual: true));
        return source.Fingerprint == disc.Fingerprint
            ? new DiscResolution(DiscStatus.Found, source, null)
            : new DiscResolution(DiscStatus.ProbableMatch, source, ProbableMatchMessage);
    }

    public static string FormatName(DiscFormat format) => format switch
    {
        DiscFormat.Dvd => "DVD",
        DiscFormat.Bluray => "Blu-ray",
        _ => format.ToString(),
    };

    private DiscSource? TryIdentify(string path)
    {
        try
        {
            return identifier.Identify(path);
        }
        catch (DiscIdentificationException)
        {
            return null;
        }
    }
}
