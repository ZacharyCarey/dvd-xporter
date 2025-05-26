using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>
/// A disc's structure: which kind of disc it is, and the files that describe its layout, hashed into a fingerprint
/// that identifies the disc wherever it's found (a folder rip or an image, on any computer).
/// </summary>
/// <remarks>
/// DVD: every <c>VIDEO_TS/*.IFO</c>.
/// Blu-ray: <c>BDMV/index.bdmv</c>, <c>BDMV/MovieObject.bdmv</c>, <c>BDMV/PLAYLIST/*.mpls</c> and <c>BDMV/CLIPINF/*.clpi</c>.
/// Names are matched case-insensitively and hashed upper-cased in sorted order, so the fingerprint
/// doesn't depend on how a ripper or file system cased or ordered them.
/// </remarks>
public static class DiscStructure
{
    public const string DvdFolderName = "VIDEO_TS";
    public const string BlurayFolderName = "BDMV";

    // Bump if the hashed content ever changes; old cached fingerprints then stop matching.
    private const string FingerprintVersion = "dvdx-fp-1";

    public static (DiscFormat Format, List<string> Files) FindStructureFiles(IDiscFileSystem fs)
    {
        var videoTs = FindChild(fs.GetDirectoryNames(""), DvdFolderName);
        if (videoTs is not null)
        {
            var ifoFiles = FilesWithExtension(fs, videoTs, ".IFO");
            if (ifoFiles.Count == 0)
                throw new DiscException("The VIDEO_TS folder doesn't contain any .IFO files.");
            return (DiscFormat.Dvd, ifoFiles);
        }

        var bdmv = FindChild(fs.GetDirectoryNames(""), BlurayFolderName);
        if (bdmv is not null)
        {
            var bdmvFiles = fs.GetFileNames(bdmv).ToList();
            var index = FindChild(bdmvFiles, "index.bdmv")
                ?? throw new DiscException("The BDMV folder doesn't contain index.bdmv.");

            var files = new List<string> { $"{bdmv}/{index}" };
            var movieObject = FindChild(bdmvFiles, "MovieObject.bdmv");
            if (movieObject is not null)
                files.Add($"{bdmv}/{movieObject}");

            var bdmvDirectories = fs.GetDirectoryNames(bdmv).ToList();
            var playlist = FindChild(bdmvDirectories, "PLAYLIST");
            if (playlist is not null)
                files.AddRange(FilesWithExtension(fs, $"{bdmv}/{playlist}", ".mpls"));
            var clipInfo = FindChild(bdmvDirectories, "CLIPINF");
            if (clipInfo is not null)
                files.AddRange(FilesWithExtension(fs, $"{bdmv}/{clipInfo}", ".clpi"));

            return (DiscFormat.Bluray, files);
        }

        throw new DiscException(
            "This isn't a DVD or Blu-ray rip: no VIDEO_TS or BDMV folder was found. Only full 1:1 disc rips are supported.");
    }

    /// <summary>The kind of disc whose root holds these folders, or null if it's neither.</summary>
    public static DiscFormat? FindFormat(IEnumerable<string> rootFolderNames)
    {
        var names = rootFolderNames.ToList();
        if (FindChild(names, DvdFolderName) is not null)
            return DiscFormat.Dvd;
        if (FindChild(names, BlurayFolderName) is not null)
            return DiscFormat.Bluray;
        return null;
    }

    public static string ComputeFingerprint(IDiscFileSystem fs, DiscFormat format, IEnumerable<string> files)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        hash.AppendData(Encoding.UTF8.GetBytes($"{FingerprintVersion}\0{format}\0"));

        Span<byte> lengthBytes = stackalloc byte[sizeof(long)];
        var buffer = new byte[81920];
        foreach (var file in files.OrderBy(f => f.ToUpperInvariant(), StringComparer.Ordinal))
        {
            hash.AppendData(Encoding.UTF8.GetBytes(file.ToUpperInvariant() + "\0"));
            BinaryPrimitives.WriteInt64LittleEndian(lengthBytes, fs.GetFileLength(file));
            hash.AppendData(lengthBytes);

            using var stream = fs.OpenRead(file);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
        }

        return Convert.ToHexStringLower(hash.GetHashAndReset())[..32];
    }

    /// <summary>
    /// True if <paramref name="folderPath"/> looks like the root of a disc rip on disk.
    /// </summary>
    public static bool IsDiscRootFolder(string folderPath)
    {
        try
        {
            var names = Directory.EnumerateDirectories(folderPath).Select(Path.GetFileName);
            return names.Any(n => IsStructureFolderName(n!));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    public static bool IsStructureFolderName(string name) =>
        name.Equals(DvdFolderName, StringComparison.OrdinalIgnoreCase)
        || name.Equals(BlurayFolderName, StringComparison.OrdinalIgnoreCase);

    private static List<string> FilesWithExtension(IDiscFileSystem fs, string directory, string extension) =>
        fs.GetFileNames(directory)
            .Where(name => name.EndsWith(extension, StringComparison.OrdinalIgnoreCase))
            .Select(name => $"{directory}/{name}")
            .ToList();

    private static string? FindChild(IEnumerable<string> names, string wanted) =>
        names.FirstOrDefault(name => name.Equals(wanted, StringComparison.OrdinalIgnoreCase));
}
