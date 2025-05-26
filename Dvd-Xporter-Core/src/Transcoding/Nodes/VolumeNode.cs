using System.Globalization;

namespace DvdXporter.Core.Transcoding.Nodes;

/// <summary>A Volume node: turns the audio up or down.</summary>
public sealed class VolumeNode(CanvasNode node)
{
    public const string GainKey = "gain";

    public static readonly NodeType Type = new()
    {
        Id = NodeTypes.Volume,
        Title = "Volume",
        Category = NodeCategory.AudioFilters,
        PassKind = PortKind.Audio,
        Settings = [new(GainKey, "Gain (dB)", SettingKind.Number, "0", Min: -60, Max: 30)],
    };

    /// <summary>How much louder, in dB (negative = quieter).</summary>
    public double Gain => node.GetDouble(GainKey);

    public string Filter => $"volume={Gain.ToString("0.##", CultureInfo.InvariantCulture)}dB";
}
