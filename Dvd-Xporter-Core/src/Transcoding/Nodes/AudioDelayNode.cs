using System.Globalization;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>An Audio Delay node: shifts audio later (positive) or earlier (negative), to fix sync.</summary>
public sealed class AudioDelayNode(CanvasNode node)
{
    public const string DelayKey = "ms";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.AudioDelay,
        Title = "Audio Delay",
        Category = NodeCategory.AudioFilters,
        Description = "Shifts audio later (positive) or earlier (negative) to fix sync.",
        PassKind = PortKind.Audio,
        Settings = [new(DelayKey, "Delay (ms)", SettingKind.Integer, "0", Min: -60_000, Max: 60_000)],
    };

    /// <summary>How much later, in milliseconds (negative = earlier).</summary>
    public int Milliseconds => node.GetInt(DelayKey);

    /// <summary>The filter: silence added at the start, or the start cut off.</summary>
    public string Filter => Milliseconds switch
    {
        0 => "anull",
        > 0 and var ms => $"adelay=delays={ms}:all=1",
        var ms => $"atrim=start={(-ms / 1000.0).ToString("0.###", CultureInfo.InvariantCulture)},asetpts=PTS-STARTPTS",
    };

    /// <summary>How long audio of <paramref name="duration"/> is once delayed.</summary>
    public PreciseTime? Delayed(PreciseTime? duration) => duration is not null
        ? Milliseconds >= 0 ? duration.Value + new PreciseTime(Milliseconds * 1_000_000L) : duration.Value - new PreciseTime(-Milliseconds * 1_000_000L)
        : null;
}
