using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;

namespace DvdXporter.Core;

// Reading a time from text: the standard format it's saved in ("1:02:03.5", "-0:00:01.0") or the generic one
// people type ("1h 2m 3.5s").
public readonly partial struct PreciseTime
{
    private static readonly Regex StandardRegex = new(@"^-?(?:(\d+):)?(?:(\d+):)?(\d+)?(?:\.(\d+))?$", RegexOptions.Compiled);
    private static readonly Regex GenericRegex = new(@"^(\d+h)?\s*(\d+m)?\s*((\d+)(\.\d+)?s)?$", RegexOptions.Compiled);

    /// <summary>
    /// Parse from a standard-time format (hh:mm:ss.ns)
    /// or a generic-time format (5h 6m 26.5s)
    /// </summary>
    /// <exception cref="FormatException">It isn't a time in either format.</exception>
    public static PreciseTime Parse(string? s) =>
        TryParse(s, out var result) ? result : throw new FormatException("Failed to parse PreciseTime.");

    /// <inheritdoc cref="Parse(string?)"/>
    public static PreciseTime Parse(string? input, IFormatProvider? formatProvider) => Parse(input);

    /// <inheritdoc cref="Parse(string?)"/>
    public static bool TryParse([NotNullWhen(true)] string? s, out PreciseTime result)
    {
        if (!string.IsNullOrWhiteSpace(s) && (TryParseStandard(s.Trim(), out result) || TryParseGeneric(s.Trim(), out result)))
            return true;
        result = Zero;
        return false;
    }

    /// <inheritdoc cref="Parse(string?)"/>
    public static bool TryParse([NotNullWhen(true)] string? input, IFormatProvider? formatProvider, out PreciseTime result) =>
        TryParse(input, out result);

    /// <summary>The standard-time format (hh:mm:ss.ns): hours and minutes are optional, minutes and seconds under 60.</summary>
    private static bool TryParseStandard(string s, out PreciseTime result)
    {
        result = Zero;
        var match = StandardRegex.Match(s);
        if (!match.Success)
            return false;

        Int64 nanoseconds = 0;
        if (match.Groups[4].Success)
        {
            // Nanoseconds, so to 9 digits at most
            string fraction = match.Groups[4].Value;
            if (fraction.Length > 9)
                fraction = fraction[..9];
            nanoseconds = Int64.Parse(fraction.PadRight(9, '0'));
        }

        Int64 seconds = 0;
        if (match.Groups[3].Success)
        {
            if (!Int64.TryParse(match.Groups[3].Value, out seconds))
                return false;
        }
        else if (match.Groups[1].Success || match.Groups[2].Success)
        {
            // Hours and minutes need the seconds after them
            return false;
        }

        // One of the two optional groups is the minutes; with both, the first is the hours
        Int64[] values = [0, 0]; // hours, minutes
        for (int i = 2, j = 1; i >= 1; i--)
        {
            if (match.Groups[i].Success)
            {
                if (!Int64.TryParse(match.Groups[i].Value, out values[j]))
                    return false;
                j--;
            }
        }

        if (values[1] >= 60 || seconds >= 60)
            return false;

        try
        {
            var time = new PreciseTime(values[0], values[1], seconds, nanoseconds);
            result = s.StartsWith('-') ? -time : time;
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }

    /// <summary>
    /// The generic-time format (5h 6m 26.5s), to the microsecond. Its highest value may be over 60 (e.g. "90s"), but no
    /// other.
    /// </summary>
    private static bool TryParseGeneric(string s, out PreciseTime result)
    {
        result = Zero;
        var match = GenericRegex.Match(s);
        if (!match.Success)
            return false;

        int hours = 0, minutes = 0, seconds = 0;
        if ((match.Groups[1].Success && !int.TryParse(match.Groups[1].Value[..^1], out hours))
            || (match.Groups[2].Success && !int.TryParse(match.Groups[2].Value[..^1], out minutes))
            || (match.Groups[4].Success && !int.TryParse(match.Groups[4].Value, out seconds)))
        {
            return false;
        }

        bool overflowed = hours > 0;
        foreach (var value in new[] { minutes, seconds })
        {
            if (value >= 60)
            {
                if (overflowed)
                    return false;
                overflowed = true;
            }
        }

        // Microseconds, so to 6 digits at most
        string fraction = match.Groups[5].Success ? match.Groups[5].Value[1..] : "";
        if (fraction.Length > 6)
            fraction = fraction[..6];
        int microseconds = int.Parse(fraction.PadRight(6, '0'));

        try
        {
            result = new PreciseTime(hours, minutes, seconds, microseconds * 1000L);
            return true;
        }
        catch (OverflowException)
        {
            return false;
        }
    }
}
