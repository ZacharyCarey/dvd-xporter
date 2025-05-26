namespace FFmpeg;

/// <summary>Writing values into ffmpeg filtergraphs.</summary>
public static class FFmpegFilters
{
    /// <summary>
    /// A value (e.g. a path) for a filter option inside a filtergraph, escaped for both levels: the option's own
    /// (\ ' :) and the graph's (\ ' [ ] , ;).
    /// </summary>
    public static string EscapeValue(string value)
    {
        var option = string.Concat(value.Select(c => c is '\\' or '\'' or ':' ? $"\\{c}" : c.ToString()));
        return string.Concat(option.Select(c => c is '\\' or '\'' or '[' or ']' or ',' or ';' ? $"\\{c}" : c.ToString()));
    }
}
