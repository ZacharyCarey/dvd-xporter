
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DiscDecoders.Common;

/// <summary>
/// Most discs store time in 90khz ticks as it is compatible
/// with the most common framerates.
/// </summary>
[JsonConverter(typeof(DiscTimeJsonConverter))]
public record struct DiscTime : IComparable, IComparable<DiscTime>, IEquatable<DiscTime>
{
    /// <summary>
    /// Raw 90khz ticks
    /// </summary>
    public readonly Int64 Ticks;

    public TimeSpan Value => TimeSpan.FromMilliseconds(this.TotalMilliseconds);
    public double TotalNanoseconds => Ticks * 1000000.0 / (double)TicksPerMillisecond;
    public double TotalMilliseconds => Ticks / (double)TicksPerMillisecond;
    public double TotalSeconds => Ticks / (double)TicksPerSecond;
    public double TotalMinutes => Ticks / (double)TicksPerMinute;
    public double TotalHours => Ticks / (double)TicksPerHour;
    public static DiscTime Zero => new DiscTime(0);

    // Constants for converting 90khz clock ticks
    public const Int64 TicksPerMillisecond = 90;
    public const Int64 TicksPerSecond = TicksPerMillisecond * 1000;
    public const Int64 TicksPerMinute = TicksPerSecond * 60;
    public const Int64 TicksPerHour = TicksPerMinute * 60;

    public DiscTime(Int64 ticks = 0)
    {
        Ticks = ticks;
    }

    public DiscTime(UInt64 ticks = 0)
    {
        checked
        {
            Ticks = (Int64)ticks;
        }
    }

    public static DiscTime FromSeconds(Int32 seconds)
    {
        return new DiscTime(seconds * TicksPerSecond);
    }

    public static bool operator <(DiscTime left, DiscTime right)
    {
        return left.Ticks < right.Ticks;
    }
    public static bool operator >(DiscTime left, DiscTime right)
    {
        return left.Ticks > right.Ticks;
    }

    public static bool operator <=(DiscTime left, DiscTime right)
    {
        return left.Ticks <= right.Ticks;
    }
    public static bool operator >=(DiscTime left, DiscTime right)
    {
        return left.Ticks >= right.Ticks;
    }

    public static DiscTime operator +(DiscTime left, DiscTime right)
    {
        return new DiscTime(left.Ticks + right.Ticks);
    }

    public static DiscTime operator -(DiscTime left, DiscTime right)
    {
        return new DiscTime(left.Ticks - right.Ticks);
    }

    public static DiscTime operator *(DiscTime left, Int64 right)
    {
        return new DiscTime(left.Ticks * right);
    }
    public static DiscTime operator *(Int64 left, DiscTime right)
    {
        return new DiscTime(left * right.Ticks);
    }

    public static DiscTime operator /(DiscTime left, Int64 right)
    {
        return new DiscTime(left.Ticks / right);
    }

    public override string ToString()
    {
        long ticks = this.Ticks;
        string isNeg = (ticks < 0) ? "-" : "";
        ticks = Math.Abs(ticks);
        
        int hours = (int)(ticks / TicksPerHour);
        ticks %= TicksPerHour;

        int minutes = (int)(ticks / TicksPerMinute);
        ticks %= TicksPerMinute;

        int seconds = (int)(ticks / TicksPerSecond);
        ticks %= TicksPerSecond;

        int ms = (int)(ticks / TicksPerMillisecond);
        ticks %= TicksPerMillisecond;

        double nano = (double)ticks * 100000 / 9;

        return $"{isNeg}{hours}:{minutes:00}:{seconds:00}.{ms:000}[{nano:000000.00}ns]";
    }

    // Compares two values, returning an integer that indicates their relationship.
    public static int Compare(DiscTime t1, DiscTime t2) => t1.Ticks.CompareTo(t2.Ticks);

    // Returns a value less than zero if this object
    public int CompareTo(object? value)
    {
        if (value is null)
        {
            return 1;
        }

        if (value is DiscTime other)
        {
            return CompareTo(other);
        }

        throw new ArgumentException("Invalid object type.");
    }

    public int CompareTo(DiscTime value) => Compare(this, value);
}

public class DiscTimeJsonConverter : JsonConverter<DiscTime>
{
    public override DiscTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new DiscTime(reader.GetInt64());
    }

    public override void Write(Utf8JsonWriter writer, DiscTime value, JsonSerializerOptions options)
    {
        writer.WriteNumberValue(value.Ticks);
    }
}