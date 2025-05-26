using DiscDecoders;
using DiscDecoders.Common;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;

namespace DvdXporter.Core.Discs;

/// <summary>
/// Used to bridge the interface between the app and accessing discs.
/// It allows a clean interface to load discs from both locally, or off a server.
/// </summary>
public abstract class DiscProvider
{
    /// <summary>
    /// Where each of the discs is, as far as this provider can tell: <see cref="DiscStatus.Missing"/>, with why, for
    /// those it hasn't got. Discs aren't read here (see <see cref="DiscSource.ReadInfoAsync"/>).
    /// </summary>
    public abstract Task<Dictionary<ProjectDisc, DiscResolution>> GetAvailableDiscsAsync(IReadOnlyList<ProjectDisc> discs,
        CancellationToken cancellation = default);
}

/// <summary>
/// Discs are read as an ISO or folder structure on the host computer, found where the project last found them or in
/// the Disc Library (see <see cref="DiscResolver"/>).
/// </summary>
public sealed class LocalDiscProvider(DiscServices services, ProjectCache cache) : DiscProvider
{
    /// <remarks>Updates the project's cache with where they were found.</remarks>
    public override Task<Dictionary<ProjectDisc, DiscResolution>> GetAvailableDiscsAsync(IReadOnlyList<ProjectDisc> discs,
        CancellationToken cancellation = default) => Task.Run(() =>
    {
        // Looking for them may read discs, so it's done in the background
        var found = new Dictionary<ProjectDisc, DiscResolution>();
        foreach (var disc in discs)
        {
            cancellation.ThrowIfCancellationRequested();
            try
            {
                found[disc] = services.Resolver.Resolve(disc, cache);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                found[disc] = new DiscResolution(DiscStatus.Missing, null, $"Couldn't look for it: {ex.Message}");
            }
        }

        cache.Save();
        services.Cache.Save();
        return found;
    }, cancellation);
}

/// <summary>
/// Discs are accessed through an HTTP server to a remote server which serves up the data.
/// </summary>
public sealed class RemoteDiscProvider(RenderServerClient server) : DiscProvider
{
    /// <exception cref="RenderServerException">The server couldn't be reached.</exception>
    public override async Task<Dictionary<ProjectDisc, DiscResolution>> GetAvailableDiscsAsync(IReadOnlyList<ProjectDisc> discs,
        CancellationToken cancellation = default)
    {
        var found = (await server.FindDiscsAsync(discs.Select(d => d.Fingerprint), cancellation))
            .ToDictionary(d => d.Fingerprint, StringComparer.OrdinalIgnoreCase);
        // So its name is known, to show where they are
        if (found.Count > 0 && server.Info is null)
            await server.ConnectAsync(cancellation);

        return discs.ToDictionary(d => d, d => found.TryGetValue(d.Fingerprint, out var remote)
            ? new DiscResolution(DiscStatus.Found, new RemoteDiscSource(server, remote), null)
            : new DiscResolution(DiscStatus.Missing, null, $"The render server \"{server.Name}\" hasn't got it."));
    }
}

/// <summary>
/// Discs looked for through several providers in turn, e.g. on this computer, then on the render server: each disc is
/// used from the first that has it, and those none has are missing, as the first said.
/// </summary>
/// <param name="onProblem">Called when a provider couldn't look (e.g. the server couldn't be reached); the others are
/// still asked.</param>
public sealed class CombinedDiscProvider(IReadOnlyList<DiscProvider> providers, Action<string>? onProblem = null) : DiscProvider
{
    public override Task<Dictionary<ProjectDisc, DiscResolution>> GetAvailableDiscsAsync(IReadOnlyList<ProjectDisc> discs,
        CancellationToken cancellation = default) => GetAvailableDiscsAsync(discs, null, cancellation);

    /// <param name="onFound">Called with where each disc is so far, as each provider answers, e.g. to show the discs
    /// here before the render server answers.</param>
    public async Task<Dictionary<ProjectDisc, DiscResolution>> GetAvailableDiscsAsync(IReadOnlyList<ProjectDisc> discs,
        Action<IReadOnlyDictionary<ProjectDisc, DiscResolution>>? onFound, CancellationToken cancellation = default)
    {
        var found = new Dictionary<ProjectDisc, DiscResolution>();
        foreach (var provider in providers)
        {
            // Each is only asked for those the ones before it haven't got
            var missing = discs.Where(d => found.GetValueOrDefault(d)?.Source is null).ToList();
            if (missing.Count == 0)
                break;

            Dictionary<ProjectDisc, DiscResolution> results;
            try
            {
                results = await provider.GetAvailableDiscsAsync(missing, cancellation);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                onProblem?.Invoke(ex.Message);
                continue;
            }

            foreach (var (disc, resolution) in results)
            {
                if (resolution.Source is not null || !found.ContainsKey(disc))
                    found[disc] = resolution;
            }

            onFound?.Invoke(found);
        }

        foreach (var disc in discs)
            found.TryAdd(disc, new DiscResolution(DiscStatus.Missing, null, "Couldn't look for it."));
        return found;
    }
}

public enum DiscSourceKind
{
    /// <summary>A folder containing a <c>VIDEO_TS</c> or <c>BDMV</c> structure.</summary>
    Folder,

    /// <summary>An ISO disc image.</summary>
    Image,

    /// <summary>On the render server, not this computer (see <see cref="RemoteDiscSource"/>).</summary>
    Server,
}

/// <summary>
/// Represents a disc that can be read from. The disc can either be locally, or read from a remote server.
/// </summary>
/// <remarks>
/// What's learnt from it is kept in the project (see <see cref="DiscCache"/>); this only reads it. Clips are opened
/// as streams for ffmpeg, which reads them through <see cref="Transcoding.ClipStreamServer"/>.
/// </remarks>
public abstract class DiscSource(string name, string path, DiscSourceKind kind, DiscFormat format, string fingerprint)
{
    /// <summary>What it's called where it is: its folder's or image's name.</summary>
    public string Name { get; } = name;

    /// <summary>Where it is: a path on this computer, or where it is in the server's Disc Library, e.g. "Shows/Firefly/Disc 1".</summary>
    public string Path { get; } = path;

    public DiscSourceKind Kind { get; } = kind;

    public DiscFormat Format { get; } = format;

    public string Fingerprint { get; } = fingerprint;

    /// <summary>Whether it's on this computer, rather than the render server.</summary>
    public bool IsHere => Kind != DiscSourceKind.Server;

    /// <summary>What the render server it's on is called, or null if it's on this computer.</summary>
    public virtual string? ServerName => null;

    /// <summary>Tells streams from this disc apart from the same disc's somewhere else.</summary>
    internal virtual string StreamKey => Fingerprint;

    /// <summary>
    /// Reads everything about the disc's clips and playlists straight from it, reading every clip through once (see
    /// <see cref="Disc.ReadInfo"/>), then checks each clip's pictures for interlacing (see
    /// <see cref="Transcoding.PictureAnalyses.CheckClipsAsync"/>): every frame if <c>cache.Disc</c> is to have them
    /// checked. Takes as long as reading the whole disc: a minute or so for an image on an SSD, far longer from a disc
    /// drive. Nothing is saved; <paramref name="cache"/> holds what's read while the pictures are checked.
    /// </summary>
    /// <param name="progress">Told as each clip is read, then as each is checked.</param>
    /// <param name="onWarning">Called with problems that didn't stop it (e.g. a damaged cell), possibly from another
    /// thread.</param>
    /// <exception cref="DiscException">The disc couldn't be read.</exception>
    /// <exception cref="RenderServerException">The server couldn't read it or be reached.</exception>
    public abstract Task<DiscContents> ReadInfoAsync(DiscCache cache, IProgress<DiscContentsProgress>? progress,
        Action<string>? onWarning, CancellationToken cancellation);

    /// <summary>How to play <paramref name="clip"/>: a Blu-ray folder's file if there is one, otherwise a stream.</summary>
    /// <param name="tracks">The clip's tracks (<see cref="ClipSummary.Tracks"/>). For a DVD cell, they're announced at the
    /// start of the stream in this order, so players number them the same way (see <see cref="PlaybackOrder"/>).</param>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    /// <exception cref="RenderServerException"/>
    public abstract Task<ClipPreview> OpenPreviewAsync(string clip, IReadOnlyList<ClipTrack>? tracks = null);

    /// <summary>A clip read straight from the disc, for ffmpeg. The caller owns it. Nothing is read until it's read.</summary>
    /// <exception cref="DiscException">The clip isn't on the disc, or the disc couldn't be opened.</exception>
    public abstract Stream OpenClip(string clip);

    /// <summary>
    /// A run of clips (see <see cref="Disc.GroupSeamless"/>) read one after another as one stream, for ffmpeg. The
    /// caller owns it.
    /// </summary>
    /// <param name="blank">Bytes to turn into padding as they're read (the run's lead-in, see <see cref="ClipScan.LeadIn"/>).</param>
    /// <exception cref="DiscException">A clip isn't on the disc, or the clips can't be read as one stream.</exception>
    public abstract Stream OpenClips(IReadOnlyList<string> clips, IReadOnlyList<ByteRange>? blank);

    /// <summary>
    /// The clip as an ordinary file, for opening in another app: the file in a Blu-ray folder. Null if there's no such
    /// file (the clip is only in an image or DVD VOBs, or the disc isn't here).
    /// </summary>
    public virtual string? GetClipFilePath(string clip) => null;

    /// <summary>
    /// The disc's files outside its video structure (see <see cref="Disc.ListOtherFiles"/>), or none if they couldn't be
    /// read. Quick, so it's asked for while canvases are compiled.
    /// </summary>
    public abstract IReadOnlyList<DiscFile> ListOtherFiles();

    /// <summary>
    /// Copies one of the disc's other files to <paramref name="destination"/>, or a folder's into the folder
    /// <paramref name="destination"/> (see <see cref="Disc.CopyOtherFilesAsync"/>).
    /// </summary>
    /// <param name="progress">Called with how far it's got, 0–1.</param>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    /// <exception cref="RenderServerException"/>
    /// <exception cref="IOException">A file couldn't be read or written, or there are none.</exception>
    public abstract Task CopyOtherFilesAsync(string path, string destination, Action<double>? progress, CancellationToken cancellation);

    /// <summary>
    /// The order to announce a clip's streams in for a player (see <see cref="Disc.OpenForPlayback"/>): audio, then
    /// subtitles, as the clip lists them, so a player's n-th track of each is the n-th listed. Null if there are none.
    /// </summary>
    internal static List<int>? PlaybackOrder(IReadOnlyList<ClipTrack>? tracks) => tracks is { Count: > 0 }
        ? tracks.Where(t => t.Kind == ClipTrackKind.Audio).Concat(tracks.Where(t => t.Kind == ClipTrackKind.Subtitle)).Select(t => t.StreamId).ToList()
        : null;
}

/// <summary>
/// Represents a disc stored locally on the host computer. This could be either an ISO or folder structure.
/// </summary>
public sealed class LocalDiscSource(string path, DiscSourceKind kind, DiscFormat format, string fingerprint)
    : DiscSource(kind == DiscSourceKind.Image ? System.IO.Path.GetFileNameWithoutExtension(path) : System.IO.Path.GetFileName(path),
        path, kind, format, fingerprint)
{
    private readonly Lock _readerLock = new();
    private Disc? _reader;

    // Opened when first needed, and kept: it keeps what it has read, but no files open
    private Disc Reader
    {
        get
        {
            lock (_readerLock)
                return _reader ??= Disc.Open(Path);
        }
    }

    public override async Task<DiscContents> ReadInfoAsync(DiscCache cache, IProgress<DiscContentsProgress>? progress,
        Action<string>? onWarning, CancellationToken cancellation)
    {
        var reader = Reader;
        void log(DiscMessage message)
        {
            if (message.Level == DiscMessageLevel.Warning)
                onWarning?.Invoke(message.Text);
        }

        // One bar for the whole disc, shared by the steps as they take on a typical disc: reading its clips through, then
        // checking their pictures (a few seconds a clip, or every frame decoded)
        double readShare = cache.Disc.CheckEveryFrame ? 0.2 : 0.45;

        DiscInfo info;
        reader.MessageLogged += log;
        try
        {
            var reading = progress is null ? null : new ReadProgress(progress, readShare);
            info = await Task.Run(() => reader.ReadInfo(reading, cancellation), cancellation);
        }
        finally
        {
            reader.MessageLogged -= log;
        }

        // The pictures are checked through the cache, as the clips will be transcoded, so what was read is held there
        var contents = DiscContents.From(info);
        cache.Hold(contents);
        var checking = progress is null ? null : new CheckProgress(progress, readShare);
        var pictures = await Transcoding.PictureAnalyses.CheckClipsAsync(cache, this, checking, onWarning, cancellation);
        contents.Clips = contents.Clips.Select(c => c with { Picture = pictures.GetValueOrDefault(c.File) }).ToList();
        return contents;
    }

    // Reading the clips through: the first part of the bar
    private sealed class ReadProgress(IProgress<DiscContentsProgress> progress, double share) : IProgress<DiscReadProgress>
    {
        public void Report(DiscReadProgress value) =>
            progress.Report(new DiscContentsProgress(false, value.ClipsRead, value.Clips, share * value.ClipsRead / Math.Max(1, value.Clips)));
    }

    // Checking the pictures, which says how far through that it is: the rest of the bar
    private sealed class CheckProgress(IProgress<DiscContentsProgress> progress, double readShare) : IProgress<DiscContentsProgress>
    {
        public void Report(DiscContentsProgress value) =>
            progress.Report(value with { Fraction = readShare + (1 - readShare) * value.Fraction });
    }

    public override Task<ClipPreview> OpenPreviewAsync(string clip, IReadOnlyList<ClipTrack>? tracks = null) => Task.Run(() =>
    {
        var file = GetClipFilePath(clip);
        if (file is not null)
            return new ClipPreview(file, null, PreviewTrackIds.TransportStreamPid);

        var playback = OpenForPlayback(clip, PlaybackOrder(tracks));
        // A Blu-ray's clips are transport streams, which list their tracks; a DVD's are program streams
        bool isTransportStream = Format == DiscFormat.Bluray;
        var trackIds = isTransportStream ? PreviewTrackIds.TransportStreamPid
            : playback.TracksInOrder ? PreviewTrackIds.PrimedOrder
            : PreviewTrackIds.None;
        return new ClipPreview(null, playback.Stream, trackIds, isTransportStream ? "ts" : "ps", playback.SeekOffset);
    });

    /// <summary>The clip opened for a player, as a stream even where it's a file (see <see cref="Disc.OpenForPlayback"/>).</summary>
    /// <param name="trackOrder">Streams to announce at its start, in this order (see <see cref="PlaybackOrder"/>).</param>
    /// <exception cref="DiscException">The clip isn't on the disc, or the disc couldn't be opened.</exception>
    public ClipPlayback OpenForPlayback(string clip, IReadOnlyList<int>? trackOrder) => Reader.OpenForPlayback(clip, trackOrder);

    public override Stream OpenClip(string clip) => Reader.OpenClip(clip);

    public override Stream OpenClips(IReadOnlyList<string> clips, IReadOnlyList<ByteRange>? blank) => Reader.OpenClips(clips, blank);

    public override string? GetClipFilePath(string clip)
    {
        if (Kind != DiscSourceKind.Folder || !Directory.Exists(Path))
            return null;
        try
        {
            return Reader.GetClipFilePath(clip);
        }
        catch (DiscException)
        {
            return null;
        }
    }

    public override IReadOnlyList<DiscFile> ListOtherFiles()
    {
        try
        {
            return Reader.ListOtherFiles();
        }
        catch (Exception ex) when (ex is DiscException or IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    public override Task CopyOtherFilesAsync(string path, string destination, Action<double>? progress, CancellationToken cancellation) =>
        Reader.CopyOtherFilesAsync(path, destination, progress, cancellation);

    /// <summary>One of the disc's other files (see <see cref="ListOtherFiles"/>), for reading. The caller owns it.</summary>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    /// <exception cref="FileNotFoundException">It isn't one of the disc's other files.</exception>
    public Stream OpenOtherFile(string path) => Reader.OpenOtherFile(path);

    /// <summary>The rip's own files, e.g. to send it to the render server: the image, or every file in the folder.</summary>
    /// <exception cref="IOException"/>
    /// <exception cref="UnauthorizedAccessException"/>
    public List<RipFile> ListFiles()
    {
        var name = System.IO.Path.GetFileName(Path);
        if (Kind == DiscSourceKind.Image)
            return [new RipFile(Path, name, new FileInfo(Path).Length)];
        return Directory.EnumerateFiles(Path, "*", SearchOption.AllDirectories)
            .Select(f => new RipFile(f, $"{name}/{System.IO.Path.GetRelativePath(Path, f).Replace(System.IO.Path.DirectorySeparatorChar, '/')}",
                new FileInfo(f).Length))
            .ToList();
    }

    /// <summary>How big the rip is: the image, or the folder's files together.</summary>
    /// <exception cref="IOException"/>
    /// <exception cref="UnauthorizedAccessException"/>
    public long GetSize() => ListFiles().Sum(f => f.Length);
}

/// <summary>One of a rip's own files (see <see cref="LocalDiscSource.ListFiles"/>).</summary>
/// <param name="Path">Where it is on this computer.</param>
/// <param name="Name">Where it is in the rip, under the folder's or image's name, "/" separated, e.g. "Disc 1/VIDEO_TS/VTS_01_1.VOB".</param>
public sealed record RipFile(string Path, string Name, long Length);

/// <summary>
/// Represents a disc stored on a remote server where all requests must go through an HTTP API.
/// </summary>
/// <remarks>It's read on the server as one here would be, and its clips are streamed from it.</remarks>
public sealed class RemoteDiscSource(RenderServerClient server, RemoteLibraryDisc disc)
    : DiscSource(disc.Label, disc.Folder is null ? disc.Label : $"{disc.Folder}/{disc.Label}", DiscSourceKind.Server, disc.Format,
        disc.Fingerprint)
{
    public RenderServerClient Server { get; } = server;

    // Listed by the server when the disc was found, as they're asked for while canvases are compiled, which mustn't
    // wait on the network. Null if they couldn't be.
    private readonly IReadOnlyList<DiscFile>? _otherFiles = disc.OtherFiles;

    public override string? ServerName => Server.Name;

    internal override string StreamKey => $"{Server.Address}/{Fingerprint}";

    public override Task<DiscContents> ReadInfoAsync(DiscCache cache, IProgress<DiscContentsProgress>? progress,
        Action<string>? onWarning, CancellationToken cancellation) =>
        Server.ReadDiscAsync(Fingerprint, cache.Disc.CheckEveryFrame, progress, onWarning, cancellation);

    public override Task<ClipPreview> OpenPreviewAsync(string clip, IReadOnlyList<ClipTrack>? tracks = null) =>
        Server.OpenPreviewAsync(Fingerprint, clip, PlaybackOrder(tracks));

    public override Stream OpenClip(string clip) => Server.OpenClips(Fingerprint, [clip], null);

    public override Stream OpenClips(IReadOnlyList<string> clips, IReadOnlyList<ByteRange>? blank) => Server.OpenClips(Fingerprint, clips, blank);

    public override IReadOnlyList<DiscFile> ListOtherFiles() => _otherFiles ?? [];

    public override Task CopyOtherFilesAsync(string path, string destination, Action<double>? progress, CancellationToken cancellation) =>
        Server.CopyOtherFilesAsync(Fingerprint, _otherFiles ?? [], path, destination, progress, cancellation);
}

public sealed class DiscIdentificationException(string message, Exception? innerException = null)
    : Exception(message, innerException);
