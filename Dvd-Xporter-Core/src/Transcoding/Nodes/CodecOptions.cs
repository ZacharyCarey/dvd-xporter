using FFmpeg;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>What the codec nodes share: the options a user adds for a track.</summary>
internal static class CodecOptions
{
    /// <summary>The user's extra options, aimed at one track: "-x264-params a=b" becomes "-x264-params:0 a=b".</summary>
    public static IEnumerable<string> Extra(string extra, int track) => FFmpegArguments.Split(extra).Select(arg =>
        arg.Length > 1 && arg[0] == '-' && !char.IsAsciiDigit(arg[1]) && !arg.Contains(':') ? $"{arg}:{track}" : arg);
}
