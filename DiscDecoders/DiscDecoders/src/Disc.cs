using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>A clip opened for a player.</summary>
/// <param name="Stream">The clip's data. The caller owns it.</param>
/// <param name="SeekOffset">The byte offset in <paramref name="Stream"/> for a time from the clip's start, for exact
/// seeking, if the disc says.</param>
/// <param name="TracksInOrder">Whether the stream announces its tracks up front in the order asked for (see
/// <see cref="Disc.OpenForPlayback"/>), so a player's n-th audio or subtitle track is the n-th asked for.</param>
public sealed record ClipPlayback(Stream Stream, Func<TimeSpan, long>? SeekOffset, bool TracksInOrder);

/// <summary>How far <see cref="Disc.ReadInfo"/> has got: clips read through so far, of how many, and the one it's on.</summary>
public sealed record DiscReadProgress(int ClipsRead, int Clips, string? Clip);

/// <summary>
/// A DVD or Blu-ray, as a folder rip or an ISO image, and the operations for reading it. Open one with
/// <see cref="Open"/>. Clips are named as <see cref="ReadInfo"/> names them.
///
/// Nothing is kept open between calls, so a disc can be held onto for as long as it's useful; streams it hands out
/// keep their own files open until they're disposed. Methods can be called from several threads at once.
///
/// Errors: operations that fail throw <see cref="DiscException"/>. Problems that don't stop an operation
/// (a clip that couldn't be converted, damaged sectors that were skipped) are reported through
/// <see cref="MessageLogged"/>, along with progress information. Nothing is written to the console.
/// </summary>
public abstract class Disc
{
    public abstract DiscFormat Format { get; }

    /// <summary>The disc's root folder (holding VIDEO_TS or BDMV), or its ISO image.</summary>
    public string RootPath { get; }

    /// <summary>Whether <see cref="RootPath"/> is an ISO image rather than a folder.</summary>
    public bool IsImage { get; }

    /// <summary>
    /// Raised for progress information and non-fatal warnings. It may be raised from a background thread
    /// if the operation runs on one.
    /// </summary>
    public event Action<DiscMessage>? MessageLogged;

    protected Disc(string rootPath, bool isImage)
    {
        RootPath = rootPath;
        IsImage = isImage;
    }

    protected void Log(DiscMessageLevel level, string text) => MessageLogged?.Invoke(new DiscMessage(level, text));

    /// <summary>
    /// The DVD or Blu-ray at <paramref name="path"/>: a folder holding VIDEO_TS or BDMV (or that folder itself), or an
    /// ISO image.
    /// </summary>
    /// <exception cref="DiscException">It doesn't exist, or isn't a DVD or Blu-ray.</exception>
    public static Disc Open(string path)
    {
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        bool isImage = File.Exists(root);
        if (!isImage && !Directory.Exists(root))
            throw new DiscException($"\"{root}\" doesn't exist.");

        // Picking the VIDEO_TS or BDMV folder itself means the disc is its parent
        var name = Path.GetFileName(root);
        var parent = Path.GetDirectoryName(root);
        if (!isImage && DiscStructure.IsStructureFolderName(name) && parent is not null)
        {
            root = parent;
        }

        List<string> folders;
        try
        {
            using var fs = OpenFileSystem(root);
            folders = fs.GetDirectoryNames("").ToList();
        }
        catch (Exception ex) when (ex is not DiscException)
        {
            throw new DiscException($"\"{root}\" couldn't be read: {ex.Message}", ex);
        }

        return DiscStructure.FindFormat(folders) switch
        {
            DiscFormat.Dvd => new DvdDisc(root, isImage),
            DiscFormat.Bluray => new BlurayDisc(root, isImage),
            _ => throw new DiscException(
                $"\"{root}\" isn't a DVD or Blu-ray: it has no {DiscStructure.DvdFolderName} or {DiscStructure.BlurayFolderName} folder."),
        };
    }

    /// <summary>
    /// Everything about the disc's playlists and clips: what its navigation files say, and what reading each clip
    /// through finds that they don't (see <see cref="ClipInfo.Scan"/>), such as whether a DVD's video is interlaced.
    /// Takes as long as reading the whole disc (a minute or so for a DVD image on an SSD; far longer from a disc
    /// drive), so run it off the UI thread. A clip that can't be read is warned about and left without a scan.
    /// </summary>
    /// <param name="progress">Told as each clip is read.</param>
    /// <exception cref="DiscException">The disc couldn't be read.</exception>
    /// <exception cref="OperationCanceledException">It was cancelled.</exception>
    public DiscInfo ReadInfo(IProgress<DiscReadProgress>? progress = null, CancellationToken cancellation = default)
    {
        var info = ReadNavigation();
        ClipChapters.Fill(info);

        for (int i = 0; i < info.Clips.Count; i++)
        {
            var clip = info.Clips[i];
            progress?.Report(new DiscReadProgress(i, info.Clips.Count, clip.Filename));
            cancellation.ThrowIfCancellationRequested();
            try
            {
                clip.Scan = ScanClip(clip.Filename, cancellation);
            }
            catch (Exception ex) when (ex is DiscException or IOException)
            {
                Log(DiscMessageLevel.Warning, $"{clip.Filename} couldn't be read through: {ex.Message}");
                continue;
            }

            if (clip.Scan.Interlaced is not null)
            {
                foreach (var video in clip.Streams.OfType<VideoStreamInfo>())
                    video.SetInterlaced(clip.Scan.Interlaced.Value);
            }
        }

        // A playlist's video is interlaced if any of its clips' is
        var clips = info.Clips.ToDictionary(c => c.Filename, StringComparer.OrdinalIgnoreCase);
        foreach (var playlist in info.Playlists)
        {
            var scans = playlist.Clips.Select(c => clips.GetValueOrDefault(c.Filename)?.Scan?.Interlaced).Where(s => s is not null).ToList();
            if (scans.Count > 0)
            {
                foreach (var video in playlist.VideoStreams)
                    video.SetInterlaced(scans.Any(s => s == true));
            }
        }

        progress?.Report(new DiscReadProgress(info.Clips.Count, info.Clips.Count, null));
        return info;
    }

    /// <summary>
    /// The disc's playlists and clips as its navigation files give them; <see cref="ReadInfo"/> then works out what
    /// follows from them for both kinds of disc (each clip's chapter marks).
    /// </summary>
    /// <exception cref="DiscException">The disc couldn't be read.</exception>
    protected abstract DiscInfo ReadNavigation();

    /// <summary>
    /// A clip's data, read straight from the disc (out of the image, or the VOBs for a DVD cell), in
    /// <see cref="Container"/>. Seekable, with a known length. The caller owns it.
    /// </summary>
    /// <exception cref="DiscException">The clip isn't on the disc, or the disc couldn't be opened.</exception>
    public abstract Stream OpenClip(string clip);

    /// <summary>
    /// The clip as an ordinary file that other programs can open (a Blu-ray folder's .m2ts), or null if it's only
    /// inside an image or VOBs. Use <see cref="OpenClip"/> to read it either way.
    /// </summary>
    public virtual string? GetClipFilePath(string clip) => null;

    /// <summary>
    /// The clip opened for a player, with exact seeking where the disc allows it.
    /// </summary>
    /// <param name="trackOrder">Streams to announce at the start of the stream, in this order, for a container that has
    /// no list of its streams (a DVD's program stream): players then number their tracks in this
    /// order. DVD streams are given by MPEG stream ID (their "MPEG Stream ID" metadata, e.g. 0x80 for the first AC-3
    /// audio, 0x20 for the first subtitles). Ignored where the container lists its own streams.</param>
    /// <exception cref="DiscException">The clip isn't on the disc, or the disc couldn't be opened.</exception>
    public virtual ClipPlayback OpenForPlayback(string clip, IReadOnlyList<int>? trackOrder = null) =>
        new(OpenClip(clip), GetSeekIndex(clip), false);

    /// <summary>
    /// The byte offset in <see cref="OpenClip"/>'s stream for a time from the clip's start (for seeking without
    /// searching), or null if the disc has no index for it.
    /// </summary>
    public abstract Func<TimeSpan, long>? GetSeekIndex(string clip);

    /// <summary>
    /// Reads a clip through once for what its navigation files don't say: see <see cref="ClipScan"/>. Takes about as long
    /// as reading the clip (a second per few hundred MB from a hard drive). <see cref="ReadInfo"/> scans every clip.
    /// </summary>
    /// <exception cref="DiscException">The clip isn't on the disc, or the disc couldn't be opened.</exception>
    /// <exception cref="IOException">The clip couldn't be read.</exception>
    protected abstract ClipScan ScanClip(string clip, CancellationToken cancellation);

    /// <summary>
    /// A disc's clips grouped into runs a player reads as one stream, as they're played one after another (see
    /// <see cref="OpenClips"/>), by their names and scans alone, so the disc needn't be here; a clip without a scan
    /// starts a run of its own. Only a DVD's cells run on; a Blu-ray's clips are each read on their own.
    /// </summary>
    /// <returns>Each run's indexes into <paramref name="clips"/>, in order.</returns>
    public static List<List<int>> GroupSeamless(DiscFormat format, IReadOnlyList<string> clips, IReadOnlyList<ClipScan?> scans) =>
        format == DiscFormat.Dvd ? DvdDisc.GroupCells(clips, scans) : clips.Select((_, i) => new List<int> { i }).ToList();

    /// <summary>
    /// Every caption of a picture-based subtitle stream (DVD or Blu-ray PGS), drawn, from a clip's data as
    /// <see cref="OpenClip"/> gives it, so the disc itself needn't be here. Reads the whole clip.
    /// </summary>
    /// <param name="streamId">The subtitle stream's ID (see <see cref="StreamInfo.StreamId"/>).</param>
    /// <param name="palette">A DVD clip's subtitle colours (see <see cref="ClipInfo.SubtitlePalette"/>); Blu-ray
    /// subtitles carry their own.</param>
    /// <exception cref="IOException">The clip couldn't be read.</exception>
    public static List<CaptionPicture> ReadCaptions(DiscFormat format, Stream clip, int streamId, IReadOnlyList<Rgb>? palette,
        CancellationToken cancellation = default) =>
        format == DiscFormat.Dvd
            ? DvdCaptions.Read(clip, streamId, palette, cancellation)
            : PgsCaptions.Read(clip, streamId, cancellation);

    /// <summary>
    /// A run of clips (see <see cref="GroupSeamless"/>) read one after another as one stream, in <see cref="Container"/>.
    /// The caller owns it.
    /// </summary>
    /// <param name="blank">Bytes of the stream to turn into padding as they're read, e.g. the first clip's
    /// <see cref="ClipScan.LeadIn"/>.</param>
    /// <exception cref="DiscException">A clip isn't on the disc, or the clips can't be read as one stream.</exception>
    public virtual Stream OpenClips(IReadOnlyList<string> clips, IReadOnlyList<ByteRange>? blank = null)
    {
        if (clips.Count != 1 || blank is { Count: > 0 })
            throw new DiscException("This disc's clips can only be read one at a time, as they are.");
        return OpenClip(clips[0]);
    }

    /// <summary>
    /// The disc's files outside its video structure (DVD-ROM content: PDFs, pictures, a PC program or web pages, ...),
    /// by path. See <see cref="DiscFiles"/>.
    /// </summary>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    /// <exception cref="IOException">It couldn't be read.</exception>
    public List<DiscFile> ListOtherFiles()
    {
        using var fs = OpenFileSystem(RootPath);
        return DiscFiles.List(fs);
    }

    /// <summary>
    /// Copies one of the disc's other files to <paramref name="destination"/>; or a folder's files (see
    /// <see cref="DiscFiles.IsFolder"/>) into the folder <paramref name="destination"/>, in the folders they're in.
    /// </summary>
    /// <param name="progress">Called with how far it's got, 0–1.</param>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    /// <exception cref="IOException">A file couldn't be read or written, or there are none.</exception>
    public async Task CopyOtherFilesAsync(string path, string destination, Action<double>? progress, CancellationToken cancellation)
    {
        using var fs = OpenFileSystem(RootPath);
        await DiscFiles.CopyAsync(fs, path, destination, progress, cancellation);
    }

    /// <summary>One of the disc's other files (see <see cref="ListOtherFiles"/>), for reading. The caller owns it.</summary>
    /// <exception cref="DiscException">The disc couldn't be opened.</exception>
    /// <exception cref="FileNotFoundException">It isn't one of the disc's other files.</exception>
    public Stream OpenOtherFile(string path)
    {
        var fs = OpenFileSystem(RootPath);
        try
        {
            if (!DiscFiles.List(fs).Any(f => f.Path == path))
                throw new FileNotFoundException($"\"{path}\" isn't one of the disc's other files.");
            return new OwningStream(fs.OpenRead(path), fs);
        }
        catch
        {
            fs.Dispose();
            throw;
        }
    }

    /// <summary>The disc's files: <paramref name="path"/> is its root folder or its image.</summary>
    /// <exception cref="DiscException">The image isn't a readable disc image.</exception>
    private protected static IDiscFileSystem OpenFileSystem(string path)
    {
        try
        {
            return DiscFileSystem.Open(path);
        }
        catch (InvalidDataException ex)
        {
            throw new DiscException(ex.Message, ex);
        }
    }
}
