using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>
/// A Blu-ray's navigation: its index, playlists, clip info and disc library metadata, read from its BDMV folder
/// <see href="https://github.com/lw/BluRay/wiki"/>. Only decrypted discs can be read (e.g. MakeMKV backups).
/// </summary>
public class Bluray
{
    /// <summary>The folder holding BDMV, or the image.</summary>
    public readonly string Path;

    /// <summary>The image's UDF volume label, or null for a folder.</summary>
    public readonly string? VolumeId;

    /// <summary>index.bdmv, or null if it's missing or unreadable (e.g. an incomplete copy of the disc).</summary>
    public readonly IndexFile? Index;

    /// <summary>CERTIFICATE/id.bdmv, or null if the disc hasn't one.</summary>
    public readonly DiscIdentity? Identity;

    /// <summary>The disc library metadata, one per language; see <see cref="GetMetadata"/>.</summary>
    public readonly ReadOnlyCollection<DiscLibraryMetadata> Metadata;

    /// <summary>The playlists that could be read, in the order the PLAYLIST folder lists them.</summary>
    public readonly ReadOnlyCollection<Playlist> Playlists;

    /// <summary>The clip info of every clip the playlists play, by clip ID (e.g. "00012"); clips whose info is missing or unreadable aren't here.</summary>
    public readonly ReadOnlyDictionary<string, ClipInformation> Clips;

    private Bluray(string path, IDiscFileSystem files, Action<string>? onWarning)
    {
        Path = path;
        VolumeId = files.VolumeLabel;
        CheckNotEncrypted(files);

        Index = Read(files, "BDMV/index.bdmv", "BDMV/BACKUP/index.bdmv", (reader, warn) => new IndexFile(reader, warn), onWarning);
        Identity = Read(files, "CERTIFICATE/id.bdmv", "CERTIFICATE/BACKUP/id.bdmv", (reader, _) => new DiscIdentity(reader), null);

        var playlists = new List<Playlist>();
        foreach (var name in files.ListFiles("BDMV/PLAYLIST"))
        {
            if (name.StartsWith('.') || !IsPlaylistFile(name, out uint number))
                continue;

            var playlist = Read(files, $"BDMV/PLAYLIST/{name}", $"BDMV/BACKUP/PLAYLIST/{name}", (reader, warn) => new Playlist(reader, name, number, warn), onWarning);
            if (playlist is not null)
                playlists.Add(playlist);
        }
        Playlists = playlists.AsReadOnly();

        if (Index is null)
        {
            if (Playlists.Count == 0)
                throw new BlurayException($"\"{path}\" isn't a Blu-ray: it has no index.bdmv or playlists that can be read.");
            onWarning?.Invoke("index.bdmv is missing, so this may be an incomplete copy of the disc; its playlists are read without it.");
        }

        var clips = new Dictionary<string, ClipInformation>();
        foreach (var clip in Playlists.SelectMany(p => p.PlayItems).SelectMany(i => i.Clips).Select(c => c.ClipId).Distinct())
        {
            var info = ReadClipInfo(files, clip, onWarning);
            if (info is not null)
                clips[clip] = info;
        }
        Clips = clips.AsReadOnly();

        var metadata = new List<DiscLibraryMetadata>();
        foreach (var name in files.ListFiles("BDMV/META/DL").Where(DiscLibraryMetadata.IsMetadataFile))
        {
            byte[]? data;
            try
            {
                data = files.ReadFile($"BDMV/META/DL/{name}");
            }
            catch (IOException ex)
            {
                onWarning?.Invoke($"{name} couldn't be read: {ex.Message}");
                data = null;
            }
            metadata.Add(new DiscLibraryMetadata(name, data, onWarning));
        }
        Metadata = metadata.AsReadOnly();
    }

    /// <summary>Reads a Blu-ray from a folder holding BDMV, or from an image.</summary>
    /// <param name="onWarning">Receives non-fatal problems found while reading, such as a playlist that couldn't be read
    /// and was left out (the disc is still usable).</param>
    /// <exception cref="BlurayException">The path doesn't exist, isn't a readable Blu-ray, or its video is encrypted. The
    /// message says why.</exception>
    public static Bluray Parse(string path, Action<string>? onWarning = null)
    {
        try
        {
            using var files = BlurayFiles.Open(path);
            return new Bluray(path, files, onWarning);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            throw new BlurayException($"\"{path}\" couldn't be read as a Blu-ray: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Just one clip's info, without reading the rest of the disc, or null if the clip has none that can be read.
    /// </summary>
    /// <param name="clipId">The clip's number, e.g. "00012" for 00012.m2ts.</param>
    /// <exception cref="BlurayException">The disc couldn't be opened.</exception>
    public static ClipInformation? ReadClipInfo(string path, string clipId, Action<string>? onWarning = null)
    {
        using var files = BlurayFiles.Open(path);
        return ReadClipInfo(files, clipId, onWarning);
    }

    /// <summary>The disc library metadata in <paramref name="language"/> (ISO 639-2, e.g. "fra"), else English, else
    /// the first there is; null if the disc has none.</summary>
    public DiscLibraryMetadata? GetMetadata(string? language = null) =>
        Metadata.FirstOrDefault(m => m.LanguageCode == language)
        ?? Metadata.FirstOrDefault(m => m.LanguageCode == "eng")
        ?? Metadata.FirstOrDefault();

    /// <summary>A playlist played at one of its angles (from 0; one it hasn't plays the first).</summary>
    public Title GetTitle(Playlist playlist, int angle = 0) => new(playlist, angle, Clips);

    private static ClipInformation? ReadClipInfo(IDiscFileSystem files, string clipId, Action<string>? onWarning) =>
        Read(files, $"BDMV/CLIPINF/{clipId}.clpi", $"BDMV/BACKUP/CLIPINF/{clipId}.clpi", (reader, warn) => new ClipInformation(reader, clipId, warn),
            onWarning, warnIfMissing: true);

    /// <summary>
    /// Parses a navigation file, or its backup copy if it's missing or damaged. Null if neither can be read; problems go
    /// to <paramref name="onWarning"/>, under the file's name.
    /// </summary>
    private static T? Read<T>(IDiscFileSystem files, string path, string backup, Func<BitReader, Action<string>?, T> parse,
        Action<string>? onWarning, bool warnIfMissing = false) where T : class
    {
        var name = path[(path.LastIndexOf('/') + 1)..];
        Action<string>? warn = onWarning is null ? null : message => onWarning($"{name}: {message}");
        string? problem = null;
        foreach (var file in new[] { path, backup })
        {
            try
            {
                var data = files.ReadFile(file);
                if (data is null)
                    continue;
                var result = parse(new BitReader(data), warn);
                if (problem is not null)
                    warn?.Invoke($"It's damaged, so its backup was used. {problem}");
                return result;
            }
            catch (Exception ex) when (ex is BlurayException or IOException or UnauthorizedAccessException)
            {
                problem ??= ex.Message;
            }
        }

        if (problem is not null)
            warn?.Invoke($"It couldn't be read: {problem}");
        else if (warnIfMissing)
            warn?.Invoke("It's missing.");
        return null;
    }

    private static bool IsPlaylistFile(string name, out uint number)
    {
        number = 0;
        var extension = System.IO.Path.GetExtension(name);
        return (extension.Equals(".mpls", StringComparison.OrdinalIgnoreCase) || extension.Equals(".mpl", StringComparison.OrdinalIgnoreCase))
            && uint.TryParse(System.IO.Path.GetFileNameWithoutExtension(name), out number);
    }

    /// <summary>
    /// Throws if the disc's video is still encrypted (AACS). Decrypted backups often keep the AACS folder, so the first
    /// aligned unit of each clip is checked instead.
    /// </summary>
    private static void CheckNotEncrypted(IDiscFileSystem files)
    {
        var unit = new byte[AlignedUnitSize];
        foreach (var clip in files.ListFiles("BDMV/STREAM"))
        {
            if (!clip.EndsWith(".m2ts", StringComparison.OrdinalIgnoreCase) && !clip.EndsWith(".mts", StringComparison.OrdinalIgnoreCase))
                continue;

            int read;
            try
            {
                using var stream = files.TryOpen($"BDMV/STREAM/{clip}");
                if (stream is null)
                    continue;
                read = stream.ReadAtLeast(unit, unit.Length, throwOnEndOfStream: false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // It can't be checked, and won't play either; that's found out when it's read
                continue;
            }

            if (read == unit.Length && IsEncrypted(unit))
                throw new BlurayException($"The disc's video is encrypted ({clip} is protected by AACS). Decrypt the disc first, for example by backing it up with MakeMKV.");
        }
    }

    // An aligned unit is 32 source packets of 192 bytes
    private const int AlignedUnitSize = 32 * ClipInformation.SourcePacketSize;

    /// <summary>
    /// Each source packet is a 4-byte header, then a 188-byte transport packet that starts with the 0x47 sync byte.
    /// Encrypted units have the copy permission bits (top two bits of the first byte) set, and only their first 16
    /// bytes are in the clear, so the later packets' sync bytes are scrambled. Both must hold: decrypted units sometimes
    /// keep the copy permission bits set.
    /// </summary>
    private static bool IsEncrypted(ReadOnlySpan<byte> unit)
    {
        if ((unit[0] & 0xc0) == 0)
            return false;

        for (int i = ClipInformation.SourcePacketSize; i < unit.Length; i += ClipInformation.SourcePacketSize)
        {
            if (unit[i + 4] != 0x47)
                return true;
        }
        return false;
    }
}
