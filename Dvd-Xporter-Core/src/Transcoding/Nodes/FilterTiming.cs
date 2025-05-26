using FFmpeg;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>When a filter that's only on for part of the video (Draw Text, Overlay) is on.</summary>
internal static class FilterTiming
{
    /// <summary>":enable='between(t,from,until)'", or nothing if it's on throughout.</summary>
    public static string EnableOption(PreciseTime? start, PreciseTime? end)
    {
        if (start is null && end is null)
            return "";
        var from = (start ?? PreciseTime.Zero).ToFFmpegTime();
        return end is not null
            ? $":enable={FFmpegFilters.EscapeValue($"between(t,{from},{end.Value.ToFFmpegTime()})")}"
            : $":enable={FFmpegFilters.EscapeValue($"gte(t,{from})")}";
    }
}
