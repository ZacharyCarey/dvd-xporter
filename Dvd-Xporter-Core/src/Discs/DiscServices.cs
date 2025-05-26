namespace DvdXporter.Core.Discs;

/// <summary>
/// The disc-related services the app shares: one fingerprint cache, identifier, library and resolver.
/// </summary>
public sealed class DiscServices
{
    public DiscServices(string fingerprintCachePath)
    {
        Identifier = new DiscIdentifier(FingerprintCache.Load(fingerprintCachePath));
        Library = new DiscLibrary(Identifier);
        Resolver = new DiscResolver(Identifier, Library);
    }

    public FingerprintCache Cache => Identifier.Cache;

    public DiscIdentifier Identifier { get; }

    public DiscLibrary Library { get; }

    public DiscResolver Resolver { get; }
}
