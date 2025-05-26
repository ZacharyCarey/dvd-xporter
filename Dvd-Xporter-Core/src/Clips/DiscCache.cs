using DiscDecoders;
using DiscDecoders.Common;
using DvdXporter.Core.Discs;
using DvdXporter.Core.Projects;
using DvdXporter.Core.Storage;

namespace DvdXporter.Core.Clips;

/// <summary>
/// What's been learnt from one of a project's discs. Its <see cref="DiscContents"/>, every clip's scan included, is
/// read once when the disc is added (<see cref="ReadContentsAsync"/>) and saved in the project, under
/// <see cref="ProjectFolder.DiscInfoFolderName"/>, so it's shared with it. This user's picture analyses and generated
/// files are kept apart, under <c>Video Files/&lt;fingerprint&gt;/</c>, like <c>cache.json</c>.
/// </summary>
/// <remarks>
/// The disc itself is read through a <see cref="DiscSource"/>, whether it's here or on the render server; this keeps
/// what's learnt from it. There's one for each disc, shared by everything that uses it (see
/// <see cref="ProjectFolder.GetDiscCache"/>), so what's read is seen everywhere at once.
/// </remarks>
public sealed class DiscCache
{
    private readonly Lock _contentsLock = new();
    // Once found: until then the file is looked for each time, as another reader (or another user) may save it
    private DiscContents? _contents;

    internal DiscCache(ProjectFolder folder, ProjectDisc disc)
    {
        Folder = folder;
        Disc = disc;
    }

    public ProjectDisc Disc { get; }

    internal ProjectFolder Folder { get; }

    public string FolderPath => Path.Combine(Folder.FolderPath, ProjectFolder.VideoFilesFolderName, Disc.Fingerprint);

    private string ContentsPath => Path.Combine(Folder.FolderPath, ProjectFolder.DiscInfoFolderName, Disc.Fingerprint + ".json");

    /// <summary>The saved contents, or null if the disc hasn't been read (or was read by an older version).</summary>
    public DiscContents? LoadContents()
    {
        lock (_contentsLock)
        {
            if (_contents is null && File.Exists(ContentsPath))
                _contents = JsonFile.TryRead<DiscContents>(ContentsPath);

            return _contents;
        }
    }

    /// <summary>
    /// Throws away what was read from the disc and made from it (its Disc Info file and its <see cref="FolderPath"/>),
    /// so it's read again.
    /// </summary>
    internal void Forget()
    {
        lock (_contentsLock)
        {
            _contents = null;
            if (File.Exists(ContentsPath))
                File.Delete(ContentsPath);
        }

        try
        {
            if (Directory.Exists(FolderPath))
                Directory.Delete(FolderPath, recursive: true);
        }
        catch (IOException)
        {
            // Something in it is open; what's made from the disc is made again where it's needed
        }
    }

    /// <summary>
    /// Whether the disc has to be read: it hasn't been, or it's to have every frame checked and wasn't read that way.
    /// </summary>
    public bool NeedsReading
    {
        get
        {
            var contents = LoadContents();
            return (contents is null) || (Disc.CheckEveryFrame && !contents.EveryFrameChecked);
        }
    }

    /// <summary>
    /// Reads everything about the disc from <paramref name="source"/> (see <see cref="DiscSource.ReadInfoAsync"/>) and
    /// saves it in the project.
    /// </summary>
    /// <param name="progress">Told as each clip is read, then as each is checked.</param>
    /// <param name="onWarning">Called with problems that didn't stop it (e.g. a damaged cell), possibly from another
    /// thread.</param>
    /// <exception cref="DiscException">The disc couldn't be read.</exception>
    /// <exception cref="Remote.RenderServerException">It's on the render server, which couldn't read it or be reached.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled; nothing is saved.</exception>
    public async Task<DiscContents> ReadContentsAsync(DiscSource source, IProgress<DiscContentsProgress>? progress = null,
        Action<string>? onWarning = null, CancellationToken cancellation = default)
    {
        try
        {
            var contents = await source.ReadInfoAsync(this, progress, onWarning, cancellation);
            contents.EveryFrameChecked = Disc.CheckEveryFrame;
            lock (_contentsLock)
            {
                JsonFile.Write(ContentsPath, contents);
                _contents = contents;
            }

            return contents;
        }
        catch
        {
            Hold(null);
            throw;
        }
    }

    /// <summary>Uses <paramref name="contents"/> as the disc's until they're saved, e.g. while its pictures are checked.</summary>
    internal void Hold(DiscContents? contents)
    {
        lock (_contentsLock)
            _contents = contents;
    }

    // ---- Scans ----

    /// <summary>What reading a clip through found when the disc was read (see <see cref="ClipSummary.Scan"/>), if it could be.</summary>
    public ClipScan? GetScan(string clip) => LoadContents()?.FindClip(clip)?.Scan;

    /// <summary>
    /// The clips grouped into runs that are read as one stream (see <see cref="DiscDecoders.Disc.GroupSeamless"/>), by
    /// their names and scans: the disc needn't be here.
    /// </summary>
    /// <returns>Each run's indexes into <paramref name="clips"/>, in order.</returns>
    public List<List<int>> GroupSeamless(IReadOnlyList<string> clips) =>
        DiscDecoders.Disc.GroupSeamless(Disc.Format, clips, clips.Select(GetScan).ToList());

    /// <summary>What's blanked out of a run of clips as it's read (see <see cref="DiscSource.OpenClips"/>): its first clip's lead-in.</summary>
    public IReadOnlyList<ByteRange>? LeadInOf(IReadOnlyList<string> clips) => GetScan(clips[0])?.LeadIn;

    /// <summary>Where generated files for this disc go, e.g. forced-only subtitles.</summary>
    public string GeneratedFolder => Path.Combine(FolderPath, "generated");
}
