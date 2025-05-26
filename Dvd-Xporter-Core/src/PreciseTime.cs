using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DvdXporter.Core;

/// <summary>
/// A time or length to the nanosecond, as the app works with it. Written as "h:mm:ss.f" (see <see cref="ToString"/>),
/// and read from that or "1h 2m 3.5s" (see <see cref="TryParse(string?, out PreciseTime)"/>).
/// </summary>
[JsonConverter(typeof(PreciseTimeJsonConverter))]
public readonly partial struct PreciseTime : IComparable, IComparable<PreciseTime>, IEquatable<PreciseTime>, IParsable<PreciseTime>
{
    private const Int64 NanosecondsPerHour = 3600000000000L;
    private const Int64 NanosecondsPerMinute = 60000000000L;
    private const Int64 NanosecondsPerSecond = 1000000000L;
    public readonly Int64 Nanoseconds;

    public static PreciseTime Zero => new PreciseTime();

    public PreciseTime()
    {
        Nanoseconds = 0;
    }

    public PreciseTime(Int64 nanoseconds)
    {
        this.Nanoseconds = nanoseconds;
    }

    public PreciseTime(Int64 hours, Int64 minutes, Int64 seconds, Int64 nanoseconds)
    {
        checked
        {
            this.Nanoseconds = (hours * NanosecondsPerHour)
                            + (minutes * NanosecondsPerMinute)
                            + (seconds * NanosecondsPerSecond)
                            + nanoseconds;
        }
    }

    public static PreciseTime FromTimespan(TimeSpan time)
    {
        return new PreciseTime(time.Ticks * TimeSpan.NanosecondsPerTick);
    }

    // Compares two values, returning an integer that indicates their relationship.
    public static int Compare(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds.CompareTo(t2.Nanoseconds);

    // Returns a value less than zero if this object
    public int CompareTo(object? value)
    {
        if (value is null)
        {
            return 1;
        }

        if (value is PreciseTime other)
        {
            return CompareTo(other);
        }

        throw new ArgumentException("Invalid object type.");
    }

    public int CompareTo(PreciseTime value) => Compare(this, value);

    public override bool Equals([NotNullWhen(true)] object? value) => (value is PreciseTime other) && Equals(other);

    public bool Equals(PreciseTime obj) => Equals(this, obj);

    public static bool Equals(PreciseTime t1, PreciseTime t2) => t1 == t2;

    public override int GetHashCode() => Nanoseconds.GetHashCode();

    /// <summary>The time as "h:mm:ss.f", with the fraction's trailing zeros trimmed (e.g. "1:02:03.5"), as it's saved.</summary>
    public override string ToString()
    {
        var time = SplitTime();
        string sign = time.IsNegative ? "-" : "";
        string fraction = $"{time.Nanoseconds:000000000}".TrimEnd('0').PadRight(1, '0');
        return $"{sign}{time.Hours}:{time.Minutes:00}:{time.Seconds:00}.{fraction}";
    }

    public TimeSpan AsTimeSpan()
    {
        var time = SplitTime();
        ulong nano = time.Nanoseconds;

        int milli = (int)(nano / 1000000);
        nano %= 1000000;

        int micro = (int)(nano / 1000);

        TimeSpan result = new TimeSpan(0, (int)time.Hours, (int)time.Minutes, (int)time.Seconds, milli, micro);
        if (time.IsNegative)
            return -result;
        else
            return result;
    }

    private (bool IsNegative, UInt64 Hours, UInt64 Minutes, UInt64 Seconds, UInt64 Nanoseconds) SplitTime()
    {
        bool isNegative;
        UInt64 nanoseconds;
        if (this.Nanoseconds < 0)
        {
            isNegative = true;
            if (this.Nanoseconds == Int64.MinValue) nanoseconds = (UInt64)Int64.MaxValue + 1; // Avoid off-by-one error
            else nanoseconds = (UInt64)(-this.Nanoseconds);
        }
        else
        {
            isNegative = false;
            nanoseconds = (UInt64)this.Nanoseconds;
        }

        UInt64 hours = nanoseconds / NanosecondsPerHour;
        nanoseconds %= NanosecondsPerHour;

        UInt64 minutes = nanoseconds / NanosecondsPerMinute;
        nanoseconds %= NanosecondsPerMinute;

        UInt64 seconds = nanoseconds / NanosecondsPerSecond;
        nanoseconds %= NanosecondsPerSecond;

        return (isNegative, hours, minutes, seconds, nanoseconds);
    }

    public static PreciseTime operator -(PreciseTime t)
    {
        checked
        {
            return new PreciseTime(-t.Nanoseconds);
        }
    }

    public static PreciseTime operator -(PreciseTime t1, PreciseTime t2)
    {
        checked
        {
            return new PreciseTime(t1.Nanoseconds - t2.Nanoseconds);
        }
    }

    public static PreciseTime operator +(PreciseTime t) => t;

    public static PreciseTime operator +(PreciseTime t1, PreciseTime t2)
    {
        checked
        {
            return new PreciseTime(t1.Nanoseconds + t2.Nanoseconds);
        }
    }

    public static PreciseTime operator *(PreciseTime time, Int64 factor)
    {
        checked
        {
            return new PreciseTime(time.Nanoseconds * factor);
        }
    }

    public static PreciseTime operator *(Int64 factor, PreciseTime time) => time * factor;

    public static PreciseTime operator /(PreciseTime time, Int64 factor)
    {
        checked
        {
            return new PreciseTime(time.Nanoseconds / factor);
        }
    }

    // Using floating-point arithmetic directly means that infinities can be returned, which is reasonable
    // if we consider PreciseTime.FromHours(1) / PreciseTime.Zero asks how many zero-second intervals there are in
    // an hour for which infinity is the mathematic correct answer. Having PreciseTime.Zero / PreciseTime.Zero return NaN
    // is perhaps less useful, but no less useful than an exception.
    /// <inheritdoc cref="IDivisionOperators{TSelf, TOther, TResult}.op_Division(TSelf, TOther)" />
    public static double operator /(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds / (double)t2.Nanoseconds;

    /// <inheritdoc cref="IEqualityOperators{TSelf, TOther, TResult}.op_Equality(TSelf, TOther)" />
    public static bool operator ==(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds == t2.Nanoseconds;

    /// <inheritdoc cref="IEqualityOperators{TSelf, TOther, TResult}.op_Inequality(TSelf, TOther)" />
    public static bool operator !=(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds != t2.Nanoseconds;

    /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_LessThan(TSelf, TOther)" />
    public static bool operator <(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds < t2.Nanoseconds;

    /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_LessThanOrEqual(TSelf, TOther)" />
    public static bool operator <=(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds <= t2.Nanoseconds;

    /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_GreaterThan(TSelf, TOther)" />
    public static bool operator >(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds > t2.Nanoseconds;

    /// <inheritdoc cref="IComparisonOperators{TSelf, TOther, TResult}.op_GreaterThanOrEqual(TSelf, TOther)" />
    public static bool operator >=(PreciseTime t1, PreciseTime t2) => t1.Nanoseconds >= t2.Nanoseconds;
}

public class PreciseTimeJsonConverter : JsonConverter<PreciseTime>
{
    public override PreciseTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return PreciseTime.Parse(reader.GetString());
    }

    public override void Write(Utf8JsonWriter writer, PreciseTime value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString());
    }
}
