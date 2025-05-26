using DiscDecoders.Common;
using FFmpeg;

namespace DvdXporter.Core;

/// <summary>
/// Every conversion between the time types: <see cref="DiscTime"/> (the disc library's 90 kHz ticks),
/// <see cref="FFmpegTime"/> (the ffmpeg library's microseconds) and <see cref="PreciseTime"/> (the app's nanoseconds).
/// Kept here, and explicit, so a value is converted once where it crosses from one to another, and how it's rounded
/// is decided in one place.
/// </summary>
public static class TimeConversions
{
    /// <summary>A disc time in nanoseconds (to under a nanosecond: a tick isn't a whole number of them).</summary>
    public static PreciseTime ToPreciseTime(this DiscTime time)
    {
        checked
        {
            return new PreciseTime(time.Ticks * 100_000L / 9);
        }
    }

    public static PreciseTime? ToPreciseTime(this DiscTime? time) => time is not null ? time.Value.ToPreciseTime() : null;

    /// <summary>An ffmpeg time in nanoseconds (exact).</summary>
    public static PreciseTime ToPreciseTime(this FFmpegTime time)
    {
        checked
        {
            return new PreciseTime(time.Microseconds * 1000);
        }
    }

    public static PreciseTime? ToPreciseTime(this FFmpegTime? time) => time is not null ? time.Value.ToPreciseTime() : null;

    /// <summary>A time to the nearest microsecond, as ffmpeg takes it (far below a frame, so a cut can't move).</summary>
    public static FFmpegTime ToFFmpegTime(this PreciseTime time) =>
        new((long)Math.Round(time.Nanoseconds / 1000m, MidpointRounding.AwayFromZero));

    public static FFmpegTime? ToFFmpegTime(this PreciseTime? time) => time is not null ? time.Value.ToFFmpegTime() : null;

    /// <summary>A disc time to the nearest microsecond (a tick is 11.1 µs, so this is never halfway).</summary>
    public static FFmpegTime ToFFmpegTime(this DiscTime time) =>
        new((long)Math.Round(time.Ticks * 100m / 9, MidpointRounding.AwayFromZero));

    public static FFmpegTime? ToFFmpegTime(this DiscTime? time) => time is not null ? time.Value.ToFFmpegTime() : null;
}
