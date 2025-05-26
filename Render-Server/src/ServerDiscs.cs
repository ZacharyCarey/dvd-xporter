using System.Text.RegularExpressions;
using DiscDecoders;
using DvdXporter.Core.Clips;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Remote;

namespace DvdXporter.Server;

/// <summary>
/// The discs the server has: those in its Disc Library, found by fingerprint as the app does. The app can send it
/// discs too: for one job only (see <see cref="RenderJobs"/>), or to keep in the Disc Library.
/// </summary>
public sealed partial class ServerDiscs(ServerConfig config, string dataFolder)
{
    // Where a disc sent to keep is put together, in the Disc Library (so it's moved, not copied, into place) but
    // out of sight of its scans, which skip hidden folders
    private const string IncomingFolderName = ".dvd-xporter-incoming";

    private readonly DiscServices _services = new(Path.Combine(dataFolder, "disc-fingerprints.json"));
    private readonly Lock _lock = new();
    // One scan at a time: the app's, the console's, or a job's
    private readonly Lock _scanLock = new();
    // Discs sent to keep since the library was last scanned
    private readonly Dictionary<string, LocalDiscSource> _added = new(StringComparer.OrdinalIgnoreCase);

    public int LibraryCount => _services.Library.Discs.Count;

    /// <summary>Looks through the Disc Library again. Takes a while on a big library, the first time.</summary>
    /// <returns>Problems with discs that couldn't be read, as "path: message".</returns>
    public List<string> Rescan()
    {
        var problems = new List<string>();
        if (config.DiscLibraryPath is { Length: > 0 } library)
        {
            try
            {
                lock (_scanLock)
                {
                    var result = _services.Library.Scan(library);
                    problems.AddRange(result.Errors.Select(e => $"{e.Path}: {e.Message}"));
                }
            }
            catch (DirectoryNotFoundException ex)
            {
                problems.Add(ex.Message);
            }
        }

        lock (_lock)
            _added.Clear();
        return problems;
    }

    /// <summary>Where the disc with <paramref name="fingerprint"/> is in the Disc Library, if it is.</summary>
    public LocalDiscSource? Find(string fingerprint)
    {
        lock (_lock)
        {
            if (_added.TryGetValue(fingerprint, out var added))
                return added;
        }

        var source = _services.Library.Find(fingerprint);
        return source is not null && (File.Exists(source.Path) || Directory.Exists(source.Path)) ? source : null;
    }

    /// <exception cref="DiscIdentificationException">It isn't a disc that can be read.</exception>
    public LocalDiscSource Identify(string path) => _services.Identifier.Identify(path);

    /// <summary>Every disc in the Disc Library, those sent to keep since it was scanned included.</summary>
    public List<RemoteLibraryDisc> ListLibrary()
    {
        List<LocalDiscSource> added;
        lock (_lock)
            added = [.. _added.Values];
        return _services.Library.Discs.Concat(added)
            .DistinctBy(s => s.Fingerprint, StringComparer.OrdinalIgnoreCase)
            .Select(s => Describe(s, null))
            .ToList();
    }

    /// <summary>Those of the discs (by fingerprint) that are in the Disc Library, with their other files, for a project.</summary>
    public List<RemoteLibraryDisc> FindForProject(IEnumerable<string> fingerprints)
    {
        var found = new List<RemoteLibraryDisc>();
        foreach (var source in fingerprints.Select(Find).OfType<LocalDiscSource>())
            found.Add(Describe(source, [.. source.ListOtherFiles()]));
        return found;
    }

    private RemoteLibraryDisc Describe(LocalDiscSource source, List<DiscFile>? otherFiles) =>
        new(source.Fingerprint, source.Name, _services.Library.GetParentFolder(source), source.Format, otherFiles);

    /// <summary>
    /// Reads a disc in the Disc Library for the app, as it reads one when it's added to a project (see
    /// <see cref="DiscCache.ReadContentsAsync(DiscSource, IProgress{DiscContentsProgress}?, Action{string}?, CancellationToken)"/>).
    /// That's done in a project of its own, thrown away afterwards: the app keeps what's read.
    /// </summary>
    /// <exception cref="KeyNotFoundException">It isn't in the Disc Library.</exception>
    /// <exception cref="DiscException">It couldn't be read.</exception>
    /// <param name="everyFrame">Whether every frame is checked for interlacing (see <see cref="ProjectDisc.CheckEveryFrame"/>).</param>
    public async Task<DiscContents> ReadContentsAsync(string fingerprint, bool everyFrame, IProgress<DiscContentsProgress> progress,
        Action<string> onWarning, CancellationToken cancellation)
    {
        var source = Find(fingerprint) ?? throw new KeyNotFoundException("This server doesn't have that disc.");
        var folder = Path.Combine(Path.GetTempPath(), $"dvdx-server-reading-{Guid.NewGuid():N}");
        try
        {
            // The pictures are checked through the project, so the disc has to be in it
            var project = ProjectFolder.Create(folder, new Project(source.Name, ProjectKind.Movie));
            var release = new Release(source.Name);
            project.Project.Releases.Add(release);
            var disc = project.AddDisc(release, source, isManual: false);
            disc.CheckEveryFrame = everyFrame;
            return await project.GetDiscCache(disc).ReadContentsAsync(source, progress, onWarning, cancellation);
        }
        finally
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
                // Left for the system to clear out of its temporary folder
            }
        }
    }

    // ---- Being sent a disc to keep ----

    /// <summary>Where a disc sent to keep is put together.</summary>
    /// <exception cref="ArgumentException">There's no Disc Library to keep it in, it has the disc already, or the
    /// fingerprint isn't one.</exception>
    public string LibraryIncomingFolder(string fingerprint)
    {
        CheckFingerprint(fingerprint);
        if (config.DiscLibraryPath is not { Length: > 0 } library || !Directory.Exists(library))
            throw new ArgumentException("This server has no Disc Library to keep discs in. Run \"dvdx-server setup\" on it to choose one.");
        if (Find(fingerprint) is not null)
            throw new ArgumentException("The server's Disc Library has this disc already.");
        return Path.Combine(library, IncomingFolderName, fingerprint);
    }

    /// <summary>
    /// Puts a disc that's been sent whole into the Disc Library, named as it was sent (with a number added if another
    /// has that name), once it's checked to be the disc it's said to be.
    /// </summary>
    /// <exception cref="ArgumentException">It isn't that disc.</exception>
    public LocalDiscSource FinishForLibrary(string fingerprint, string name)
    {
        var incoming = LibraryIncomingFolder(fingerprint);
        var sent = Received(incoming, name);
        if (sent.Fingerprint != fingerprint)
        {
            DeleteIncoming(incoming);
            throw new ArgumentException("What was sent isn't the disc it was said to be.");
        }

        var destination = Path.Combine(config.DiscLibraryPath!, name);
        var isImage = File.Exists(sent.Path);
        for (int number = 2; File.Exists(destination) || Directory.Exists(destination); number++)
        {
            var numbered = $"{(isImage ? Path.GetFileNameWithoutExtension(name) : name)} ({number}){(isImage ? Path.GetExtension(name) : "")}";
            destination = Path.Combine(config.DiscLibraryPath!, numbered);
        }

        if (isImage)
            File.Move(sent.Path, destination);
        else
            Directory.Move(sent.Path, destination);
        DeleteIncoming(incoming);

        var source = Identify(destination);
        _services.Cache.Save();
        lock (_lock)
            _added[fingerprint] = source;
        return source;
    }

    // ---- Being sent a disc, to keep or for a job ----

    /// <summary>The files of a disc being sent into <paramref name="folder"/> that are there already, so they needn't be sent again.</summary>
    public static List<RemoteDiscFile> ListIncoming(string folder)
    {
        if (!Directory.Exists(folder))
            return [];
        return Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => !f.EndsWith(".partial", StringComparison.Ordinal))
            .Select(f => new RemoteDiscFile(Path.GetRelativePath(folder, f).Replace(Path.DirectorySeparatorChar, '/'), new FileInfo(f).Length))
            .ToList();
    }

    /// <summary>Saves one file of a disc being sent into <paramref name="folder"/>, at <paramref name="path"/> in the rip.</summary>
    /// <exception cref="ArgumentException">The path isn't one in a disc.</exception>
    public static async Task ReceiveFileAsync(string folder, string path, Stream body, CancellationToken cancellation)
    {
        if (!IsSafePath(path))
            throw new ArgumentException($"\"{path}\" isn't a path in a disc.");
        var file = Path.Combine(folder, path.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);

        // Only whole files count, so one stopped short is sent again
        var partial = file + ".partial";
        try
        {
            await using (var output = new FileStream(partial, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, useAsync: true))
                await body.CopyToAsync(output, cancellation);
            File.Move(partial, file, overwrite: true);
        }
        catch
        {
            File.Delete(partial);
            throw;
        }
    }

    /// <summary>The disc sent whole into <paramref name="folder"/> as <paramref name="name"/> (the rip's folder, or its image).</summary>
    /// <exception cref="ArgumentException">It wasn't sent, or isn't a disc (it's thrown away, to be sent again).</exception>
    public LocalDiscSource Received(string folder, string name)
    {
        if (!IsSafePath(name) || name.Contains('/'))
            throw new ArgumentException($"\"{name}\" isn't a disc's name.");
        var path = Path.Combine(folder, name);
        if (!File.Exists(path) && !Directory.Exists(path))
            throw new ArgumentException($"\"{name}\" hasn't been sent.");
        try
        {
            return Identify(path);
        }
        catch (DiscIdentificationException ex)
        {
            Directory.Delete(folder, recursive: true);
            throw new ArgumentException($"What was sent isn't a disc that can be read: {ex.Message}", ex);
        }
    }

    // The disc's folder, and the one they're all in if it's the last
    private static void DeleteIncoming(string incoming)
    {
        Directory.Delete(incoming, recursive: true);
        var parent = Path.GetDirectoryName(incoming)!;
        if (!Directory.EnumerateFileSystemEntries(parent).Any())
            Directory.Delete(parent);
    }

    /// <exception cref="ArgumentException">It isn't a fingerprint (so it can't be a folder's name either).</exception>
    public static void CheckFingerprint(string fingerprint)
    {
        if (!FingerprintPattern().IsMatch(fingerprint))
            throw new ArgumentException($"\"{fingerprint}\" isn't a disc fingerprint.");
    }

    /// <summary>Whether <paramref name="path"/> stays inside the folder it's put in.</summary>
    public static bool IsSafePath(string path) =>
        path.Length > 0 && !path.StartsWith('/') && !path.Contains('\\') && !path.Contains(':')
        && path.Split('/').All(part => part is not ("" or "." or ".."));

    [GeneratedRegex("^[0-9a-fA-F]{8,64}$")]
    private static partial Regex FingerprintPattern();
}
