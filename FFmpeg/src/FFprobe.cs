using System.Diagnostics;
using System.Globalization;
using System.Text.Json;

namespace FFmpeg;

/// <summary>A file's length, size, and how many bytes each of its streams takes.</summary>
/// <param name="StreamBytes">Each stream's packets' total size, by stream index.</param>
public sealed record PacketSizes(FFmpegTime Duration, long Size, IReadOnlyDictionary<int, long> StreamBytes);

/// <summary>Reads facts about media files with ffprobe.</summary>
public static class FFprobe
{
    /// <summary>A file's length and size, and its streams' sizes from their packets.</summary>
    /// <exception cref="System.ComponentModel.Win32Exception">ffprobe couldn't be started.</exception>
    /// <exception cref="JsonException">ffprobe's output couldn't be read (e.g. the file isn't media).</exception>
    public static async Task<PacketSizes> ReadPacketSizesAsync(string file, CancellationToken cancellation)
    {
        var info = new ProcessStartInfo("ffprobe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in new[] { "-v", "error", "-show_entries", "format=duration,size:stream=index:packet=stream_index,size", "-of", "json", file })
            info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!;
        var json = await process.StandardOutput.ReadToEndAsync(cancellation);
        await process.WaitForExitAsync(cancellation);

        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var format = root.GetProperty("format");
        FFmpegTime.TryParse(format.GetProperty("duration").GetString(), out var duration);
        var size = long.Parse(format.GetProperty("size").GetString() ?? "0", CultureInfo.InvariantCulture);
        var bytes = new Dictionary<int, long>();
        foreach (var packet in root.GetProperty("packets").EnumerateArray())
        {
            var index = packet.GetProperty("stream_index").GetInt32();
            bytes[index] = bytes.GetValueOrDefault(index) + long.Parse(packet.GetProperty("size").GetString() ?? "0", CultureInfo.InvariantCulture);
        }

        return new PacketSizes(duration, size, bytes);
    }
}
