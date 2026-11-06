namespace DiscDecoders.Common;

/// <summary>
/// Measures streams' bitrates from their packets: the bytes each carries between two timestamps a few seconds apart, at
/// its start. Works for any codec, constant or variable bitrate (which gives its average over that stretch).
/// </summary>
public sealed class BitrateMeter
{
    // Long enough to average over a few hundred audio frames
    private const long SpanTicks = 3 * DiscTime.TicksPerSecond;

    private readonly Dictionary<int, (uint First, long Bytes)> _counting = [];
    private readonly Dictionary<int, int> _measured = [];

    /// <summary>Whether every stream of <paramref name="streams"/> has been measured.</summary>
    public bool HasMeasured(IEnumerable<int> streams) => streams.All(_measured.ContainsKey);

    /// <summary>
    /// A packet of <paramref name="stream"/> carrying <paramref name="bytes"/> of its data, with its timestamp (90 kHz)
    /// if it has one. Counting starts at its first timestamp.
    /// </summary>
    public void Add(int stream, int bytes, uint? pts)
    {
        if (_measured.ContainsKey(stream))
            return;
        if (!_counting.TryGetValue(stream, out var counting))
        {
            if (pts is not null)
                _counting[stream] = (pts.Value, bytes);
            return;
        }

        // The bytes before this packet's are what played from the first timestamp to this one
        if (pts is not null && pts.Value > counting.First && pts.Value - counting.First >= SpanTicks)
        {
            var seconds = (double)(pts.Value - counting.First) / DiscTime.TicksPerSecond;
            _measured[stream] = (int)Math.Round(counting.Bytes * 8 / seconds / 1000);
            _counting.Remove(stream);
            return;
        }

        _counting[stream] = (counting.First, counting.Bytes + bytes);
    }

    /// <summary>Each measured stream's bitrate in kbps.</summary>
    public IReadOnlyList<AudioBitrate> Measured => _measured.OrderBy(m => m.Key).Select(m => new AudioBitrate(m.Key, m.Value)).ToList();
}
