using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Transcoding;

namespace DvdXporter.Core.Remote;

/// <summary>
/// How the app and a render server talk: HTTP on the server's port, every request but <see cref="InfoPath"/> carrying
/// the server's access key, and a UDP probe (multicast and broadcast) to find servers on the local network.
/// </summary>
/// <remarks>
/// Nothing is encrypted, and anyone with the key can run exports on the server: it's for a local network the user
/// trusts, which the server says when it's set up.
/// </remarks>
public static class RenderServerProtocol
{
    public const int DefaultPort = 47830;

    /// <summary>The UDP port servers listen on for <see cref="DiscoveryProbe"/>.</summary>
    public const int DiscoveryPort = 47831;

    public static readonly IPAddress DiscoveryGroup = IPAddress.Parse("239.255.47.83");

    /// <summary>What the app sends to find servers. Each answers with its <see cref="RenderServerInfo"/>, as JSON.</summary>
    public const string DiscoveryProbe = "DVD-XPORTER-RENDER-SERVER?";

    /// <summary>
    /// The version of DVD Xporter this was built as (set in Core's project file), which the server says in its
    /// <see cref="RenderServerInfo"/>. A server of another version may name or make files differently from the app.
    /// </summary>
    public static string Version { get; } = typeof(RenderServerProtocol).Assembly.GetName().Version?.ToString(3) ?? "";

    /// <summary>The one request that doesn't need the key: what the server is, so the app can show it before it has the key.</summary>
    public const string InfoPath = "/api/info";

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };
}

/// <summary>Where a server puts what it exports.</summary>
public enum RenderServerDestination
{
    /// <summary>Into its Emby libraries, one for movies and one for shows.</summary>
    Emby,

    /// <summary>Into a folder, laid out as an Emby library would be.</summary>
    Folder,
}

/// <param name="Id">Tells servers apart, as their names and addresses can change.</param>
/// <param name="Port">The HTTP port (what a discovery answer is for).</param>
/// <param name="HasDiscLibrary">Whether it has a Disc Library, so discs can be sent to keep there.</param>
/// <param name="Version">The version of DVD Xporter it was built as (see <see cref="RenderServerProtocol.Version"/>).</param>
public sealed record RenderServerInfo(string Name, string Id, string Version, int Port, RenderServerDestination ExportsTo, bool HasDiscLibrary)
{
    /// <summary>Whether it's the same version as this app, so it makes the files as the app expects.</summary>
    [JsonIgnore]
    public bool SameVersion => Version == RenderServerProtocol.Version;

    /// <summary>What to tell the user when it isn't <see cref="SameVersion"/>.</summary>
    [JsonIgnore]
    public string VersionWarning => $"\"{Name}\" is version {Version} of DVD Xporter, but this app is version {RenderServerProtocol.Version}, "
        + "so the server may not find some files or may make them differently. Update the server, then start it again.";
}

/// <summary>A disc file the server has been sent so far, by its path in the rip.</summary>
public sealed record RemoteDiscFile(string Path, long Length);

/// <summary>A disc sent to the server is whole: <paramref name="Name"/> is the rip's folder, or its image's file name.</summary>
public sealed record RemoteDiscFinish(string Name);

/// <summary>A disc in the server's Disc Library (see <see cref="Discs.RemoteDiscSource"/>).</summary>
/// <param name="Folder">The folder it's in, in the library (e.g. "Shows/Firefly"); null if it's in the library's own folder.</param>
/// <param name="OtherFiles">Its other files, when it's found for a project (not when the library's listed).</param>
public sealed record RemoteLibraryDisc(string Fingerprint, string Label, string? Folder, DiscFormat Format, List<DiscDecoders.DiscFile>? OtherFiles = null);

/// <summary>The server looked through its Disc Library again (see <see cref="RenderServerClient.RescanLibraryAsync"/>).</summary>
/// <param name="DiscCount">How many discs it found.</param>
/// <param name="Problems">Discs it couldn't read, as "path: message".</param>
public sealed record RemoteRescan(int DiscCount, List<string> Problems);

/// <summary>
/// One line of what the server says as it reads a disc for the app (see <see cref="RenderServerClient.ReadDiscAsync"/>):
/// how far it's got, a warning, or, last, what it read or why it couldn't.
/// </summary>
public sealed record RemoteReadUpdate(bool CheckingPictures = false, int Done = 0, int Total = 0, double Fraction = 0,
    string? Warning = null, string? Error = null, DiscContents? Contents = null);

/// <summary>How the server plays a clip, for a player to open its stream (see <see cref="RenderServerClient.OpenPreview"/>).</summary>
/// <param name="TransportStream">Whether it's a transport stream ("ts"), not a program stream ("ps").</param>
/// <param name="TracksInOrder">Whether the stream announces its tracks in the order asked for.</param>
/// <param name="CanSeekExactly">Whether the disc has an index for exact seeking (see <see cref="RenderServerClient.SeekOffset"/>).</param>
public sealed record RemoteClipInfo(long Length, bool TransportStream, bool TracksInOrder, bool CanSeekExactly);

/// <param name="Key">The file's <see cref="ExportFile.Key"/>.</param>
/// <param name="SourceId">Which alternate makes it (<see cref="ExportFile.SourceId"/>), as the user has it.</param>
public sealed record RenderJobFile(string Key, string SourceId, string Name);

public sealed record RenderJobRequest(string ProjectName, List<RenderJobFile> Files, bool WriteNfo, bool CopyArtwork);

public enum RenderJobState
{
    /// <summary>Waiting for the project to be sent.</summary>
    Receiving,
    Queued,
    Running,
    Done,
    Failed,
    Cancelled,
}

/// <param name="Stage">What's happening to it, e.g. "Waiting", "Encoding" or "Done".</param>
/// <param name="Outcome">How it went, once it's done with.</param>
public sealed record RenderFileStatus(string Name, string Stage, double Fraction, ExportOutcome? Outcome, string? Error);

/// <param name="Message">What went wrong, for a job that failed; a summary, for one that's done.</param>
/// <param name="Overall">How far through the whole job, 0–1.</param>
public sealed record RenderJobStatus(string Id, string ProjectName, RenderJobState State, DateTime Submitted, DateTime? Finished,
    double Overall, string? Message, List<RenderFileStatus> Files)
{
    public bool IsFinished => State is RenderJobState.Done or RenderJobState.Failed or RenderJobState.Cancelled;
}
