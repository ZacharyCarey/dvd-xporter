using System.Globalization;

namespace FFmpeg;

/// <summary>
/// A time as ffmpeg counts it: whole microseconds (its AV_TIME_BASE). Exact for every time ffmpeg prints or accepts,
/// which are decimal seconds to the microsecond.
/// </summary>
public readonly record struct FFmpegTime(long Microseconds) : IComparable<FFmpegTime>
{
    public static FFmpegTime Zero => default;

    public double TotalSeconds => Microseconds / 1_000_000.0;

    /// <summary>A time from seconds, to the nearest microsecond.</summary>
    public static FFmpegTime FromSeconds(double seconds) => new((long)Math.Round(seconds * 1_000_000));

    /// <summary>
    /// Reads decimal seconds as ffmpeg prints them (e.g. "12.345678", "-0.5"), exactly to the microsecond; false for
    /// anything else, such as "N/A".
    /// </summary>
    public static bool TryParse(string? text, out FFmpegTime time)
    {
        time = Zero;
        if (!decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
            return false;
        time = new FFmpegTime((long)Math.Round(seconds * 1_000_000m));
        return true;
    }

    /// <summary>Decimal seconds as ffmpeg's options take them, e.g. "12.345678" (no more digits than needed).</summary>
    public override string ToString() =>
        (Microseconds / 1_000_000m).ToString("0.######", CultureInfo.InvariantCulture);

    public int CompareTo(FFmpegTime other) => Microseconds.CompareTo(other.Microseconds);

    public static FFmpegTime operator +(FFmpegTime a, FFmpegTime b) => new(a.Microseconds + b.Microseconds);
    public static FFmpegTime operator -(FFmpegTime a, FFmpegTime b) => new(a.Microseconds - b.Microseconds);
    public static bool operator <(FFmpegTime a, FFmpegTime b) => a.Microseconds < b.Microseconds;
    public static bool operator >(FFmpegTime a, FFmpegTime b) => a.Microseconds > b.Microseconds;
    public static bool operator <=(FFmpegTime a, FFmpegTime b) => a.Microseconds <= b.Microseconds;
    public static bool operator >=(FFmpegTime a, FFmpegTime b) => a.Microseconds >= b.Microseconds;
}
