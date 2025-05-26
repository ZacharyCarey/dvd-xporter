using DiscFile = DiscDecoders.DiscFile;

namespace DvdXporter.Core.Transcoding;

// Lengths, sizes and file counts as nodes and their choices show them.
public static partial class NodeTypes
{
    public static string FormatDuration(PreciseTime time)
    {
        var span = time.AsTimeSpan();
        return span.TotalHours >= 1 ? span.ToString(@"h\:mm\:ss") : span.ToString(@"m\:ss");
    }

    /// <summary>E.g. "12 files, 4.5 MB".</summary>
    public static string DescribeFiles(IReadOnlyCollection<DiscFile> files) => DescribeFiles(files.Count, files.Sum(f => f.Length));

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} bytes",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.##} GB",
    };

    /// <summary>E.g. "12 files, 4.5 MB", from how many files there are and their size in bytes.</summary>
    public static string DescribeFiles(int count, long bytes) => $"{(count == 1 ? "1 file" : $"{count} files")}, {FormatSize(bytes)}";
}
