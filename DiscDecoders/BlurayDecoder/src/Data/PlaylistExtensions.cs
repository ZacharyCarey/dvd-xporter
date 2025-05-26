using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>How picture-in-picture video is scaled (players can let the viewer override it).</summary>
public enum PipScaling : byte
{
    None = 1,
    /// <summary>Half size (1:2).</summary>
    Half = 2,
    /// <summary>Quarter size (1:4).</summary>
    Quarter = 3,
    /// <summary>One and a half times the size (3:2).</summary>
    OneAndAHalf = 4,
    /// <summary>Scaled to the main video's size.</summary>
    Fullscreen = 5,
}

/// <summary>Which timeline a picture-in-picture video's positions are on.</summary>
public enum PipTimeline : byte
{
    /// <summary>Shown in sync with the main video; times are the play item's clip times.</summary>
    SyncMainPath = 1,
    /// <summary>Started by the viewer; times are on the secondary video's own sub path.</summary>
    AsyncSubPath = 2,
    /// <summary>Started by the viewer; times are the play item's clip times.</summary>
    AsyncMainPath = 3,
}

/// <summary>Where a picture-in-picture video is drawn, from <see cref="Time"/> until the next position.</summary>
public class PipPosition
{
    /// <summary>When it starts, on the block's timeline (see <see cref="PipMetadata.Timeline"/>).</summary>
    public readonly DiscTime Time;

    /// <summary>The left edge, in main video pixels.</summary>
    public readonly ushort X;

    /// <summary>The top edge, in main video pixels.</summary>
    public readonly ushort Y;

    public readonly PipScaling Scaling;

    internal PipPosition(BitReader reader)
    {
        Time = NavigationFile.ToDiscTime(reader.ReadUInt32());
        X = reader.ReadBits<ushort>(12);
        Y = reader.ReadBits<ushort>(12);
        Scaling = (PipScaling)reader.ReadBits<byte>(4);
        reader.Skip(4);
    }
}

/// <summary>Where one secondary video stream of one play item is drawn over the main video.</summary>
public class PipMetadata
{
    /// <summary>The play item whose stream table lists the secondary video (an index into <see cref="Playlist.PlayItems"/>).</summary>
    public readonly ushort PlayItemIndex;

    /// <summary>The secondary video stream (an index into that play item's <see cref="StreamTable.SecondaryVideo"/>).</summary>
    public readonly byte SecondaryVideoIndex;

    public readonly PipTimeline Timeline;

    /// <summary>Whether dark secondary video pixels are transparent (see <see cref="UpperLimitLumaKey"/>).</summary>
    public readonly bool LumaKey;

    /// <summary>With <see cref="LumaKey"/>: secondary video pixels with luma (Y) at or below this are transparent.</summary>
    public readonly byte UpperLimitLumaKey;

    /// <summary>Whether synchronous picture-in-picture is also shown while fast forwarding or rewinding.</summary>
    public readonly bool TrickPlay;

    /// <summary>Where it's drawn, in time order.</summary>
    public readonly ReadOnlyCollection<PipPosition> Positions;

    internal PipMetadata(BitReader reader, long extensionStart)
    {
        PlayItemIndex = reader.ReadUInt16();
        SecondaryVideoIndex = reader.ReadByte();
        reader.Skip(8);
        Timeline = (PipTimeline)reader.ReadBits<byte>(4);
        LumaKey = reader.ReadBit();
        TrickPlay = reader.ReadBit();
        reader.Skip(10);
        if (LumaKey)
        {
            reader.Skip(8);
            UpperLimitLumaKey = reader.ReadByte();
        }
        else
        {
            reader.Skip(16);
        }
        reader.Skip(16);
        uint dataStart = reader.ReadUInt32();

        // The positions are kept apart from the blocks
        long next = reader.Position;
        reader.Seek(extensionStart + dataStart);
        int count = reader.ReadUInt16();
        var positions = new PipPosition[count];
        for (int i = 0; i < count; i++)
            positions[i] = new PipPosition(reader);
        Positions = positions.AsReadOnly();
        reader.Seek(next);
    }
}

/// <summary>
/// HDR static metadata (UHD only): the mastering display's colour volume and the content's light levels, as HDR10 video
/// carries in its SEI messages.
/// </summary>
public class HdrStaticMetadata
{
    public readonly DynamicRange DynamicRange;

    /// <summary>The mastering display's primaries in red, green, blue order (in 0.00002 units).</summary>
    public readonly ReadOnlyCollection<(ushort X, ushort Y)> DisplayPrimaries;

    /// <summary>The white point (in 0.00002 units).</summary>
    public readonly (ushort X, ushort Y) WhitePoint;

    /// <summary>The mastering display's maximum luminance, in cd/m².</summary>
    public readonly ushort MaxDisplayMasteringLuminance;

    /// <summary>The mastering display's minimum luminance, in 0.0001 cd/m².</summary>
    public readonly ushort MinDisplayMasteringLuminance;

    /// <summary>Maximum content light level, in cd/m².</summary>
    public readonly ushort MaxCll;

    /// <summary>Maximum frame-average light level, in cd/m².</summary>
    public readonly ushort MaxFall;

    internal HdrStaticMetadata(BitReader reader)
    {
        reader.Require(28, "HDR static metadata");
        DynamicRange = (DynamicRange)reader.ReadBits<byte>(4);
        reader.Skip(4 + 24);
        // Stored green, blue, red
        var primaries = new (ushort X, ushort Y)[3];
        for (int i = 0; i < 3; i++)
            primaries[i] = (reader.ReadUInt16(), reader.ReadUInt16());
        DisplayPrimaries = new[] { primaries[2], primaries[0], primaries[1] }.AsReadOnly();
        WhitePoint = (reader.ReadUInt16(), reader.ReadUInt16());
        MaxDisplayMasteringLuminance = reader.ReadUInt16();
        MinDisplayMasteringLuminance = reader.ReadUInt16();
        MaxCll = reader.ReadUInt16();
        MaxFall = reader.ReadUInt16();
    }
}
