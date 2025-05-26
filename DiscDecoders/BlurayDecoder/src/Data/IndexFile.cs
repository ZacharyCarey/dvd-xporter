using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

/// <summary>What runs a title: a movie object (HDMV) or a Java program (BD-J).</summary>
public enum TitleObjectType : byte
{
    Hdmv = 1,
    Bdj = 2,
}

public enum TitlePlaybackType : byte
{
    HdmvMovie = 0,
    /// <summary>A menu or game: its length and position shouldn't be shown.</summary>
    HdmvInteractive = 1,
    BdjMovie = 2,
    /// <summary>A menu or game: its length and position shouldn't be shown.</summary>
    BdjInteractive = 3,
}

/// <summary>Whether a player may jump to a title, and show its number.</summary>
[Flags]
public enum TitleAccess : byte
{
    Permitted = 0,
    /// <summary>Jumping to the title isn't allowed.</summary>
    Prohibited = 0x01,
    /// <summary>The title's number shouldn't be shown.</summary>
    Hidden = 0x02,
}

/// <summary>The disc's application info from index.bdmv: the format it's authored for.</summary>
public class IndexAppInfo
{
    /// <summary>Whether the disc prefers to start in 3D.</summary>
    public readonly bool Prefers3D;

    /// <summary>Whether the disc has 3D content.</summary>
    public readonly bool Has3DContent;

    public readonly DynamicRange InitialDynamicRange;

    /// <summary>The video format, if the disc states one.</summary>
    public readonly VideoFormat VideoFormat;

    /// <summary>The frame rate, if the disc states one.</summary>
    public readonly VideoRate FrameRate;

    /// <summary>The content provider's data.</summary>
    public readonly string ProviderData;

    internal IndexAppInfo(BitReader reader)
    {
        reader.Seek(40);
        // The length (34), which isn't checked
        reader.Skip(32);
        reader.Skip(1);
        Prefers3D = reader.ReadBit();
        Has3DContent = reader.ReadBit();
        reader.Skip(1);
        InitialDynamicRange = (DynamicRange)reader.ReadBits<byte>(4);
        VideoFormat = (VideoFormat)reader.ReadBits<byte>(4);
        FrameRate = (VideoRate)reader.ReadBits<byte>(4);
        ProviderData = reader.ReadString(32);
    }
}

/// <summary>A title in index.bdmv (or its first play or top menu): what it runs, and whether it can be jumped to.</summary>
public class IndexTitle
{
    public readonly TitleObjectType ObjectType;

    public readonly TitlePlaybackType PlaybackType;

    /// <summary>Whether the player may jump to it and show its number. The first play and top menu are always permitted.</summary>
    public readonly TitleAccess Access;

    /// <summary>An HDMV title's movie object number.</summary>
    public readonly ushort MovieObject;

    /// <summary>A BD-J title's .bdjo file name, e.g. "00001".</summary>
    public readonly string BdjObject = "";

    public bool IsInteractive => PlaybackType is TitlePlaybackType.HdmvInteractive or TitlePlaybackType.BdjInteractive;

    /// <summary>Whether it can be played: a BD-J title, or an HDMV one with a movie object (0xFFFF means none).</summary>
    internal bool Exists => ObjectType == TitleObjectType.Bdj || MovieObject != 0xffff;

    internal IndexTitle(BitReader reader, bool isTitle, Action<string>? onWarning)
    {
        ObjectType = (TitleObjectType)reader.ReadBits<byte>(2);
        if (isTitle)
        {
            Access = (TitleAccess)reader.ReadBits<byte>(2);
            reader.Skip(28);
        }
        else
        {
            reader.Skip(30);
        }

        PlaybackType = (TitlePlaybackType)reader.ReadBits<byte>(2);
        reader.Skip(14);
        switch (ObjectType)
        {
            case TitleObjectType.Hdmv:
                MovieObject = reader.ReadUInt16();
                reader.Skip(32);
                if (PlaybackType is not (TitlePlaybackType.HdmvMovie or TitlePlaybackType.HdmvInteractive))
                    onWarning?.Invoke($"index.bdmv: invalid HDMV playback type {(int)PlaybackType}.");
                break;
            case TitleObjectType.Bdj:
                BdjObject = reader.ReadString(5);
                reader.Skip(8);
                if (PlaybackType is not (TitlePlaybackType.BdjMovie or TitlePlaybackType.BdjInteractive))
                    onWarning?.Invoke($"index.bdmv: invalid BD-J playback type {(int)PlaybackType}.");
                break;
            default:
                throw new BlurayException($"Unknown title object type {(int)ObjectType}.");
        }
    }
}

/// <summary>
/// index.bdmv: the disc's titles, the first play (what runs when the disc is inserted) and the top menu
/// <see href="https://github.com/lw/BluRay/wiki/index.bdmv"/>.
/// </summary>
public class IndexFile
{
    /// <summary>The file's format version, e.g. "0200" (0300 for UHD).</summary>
    public readonly string Version;

    public readonly IndexAppInfo AppInfo;

    /// <summary>What runs when the disc is inserted, or null if nothing does.</summary>
    public readonly IndexTitle? FirstPlay;

    /// <summary>The top menu, or null if there isn't one.</summary>
    public readonly IndexTitle? TopMenu;

    /// <summary>The titles, by number from 1 (Titles[0] is title 1).</summary>
    public readonly ReadOnlyCollection<IndexTitle> Titles;

    /// <summary>The UHD extension's disc type, 0 if it hasn't one.</summary>
    public readonly byte UhdDiscType;

    /// <summary>UHD: whether the disc has 4K video.</summary>
    public readonly bool Has4K;

    /// <summary>UHD: which HDR formats the disc has (raw flags).</summary>
    public readonly byte HdrFlags;

    /// <summary>UHD: whether the disc has HDR10+ video.</summary>
    public readonly bool HasHdrPlus;

    /// <summary>UHD: whether the disc has Dolby Vision video.</summary>
    public readonly bool HasDolbyVision;

    /// <summary>Whether a title runs BD-J (Java), which players that don't support it can't run.</summary>
    public bool UsesBdj => Titles.Append(FirstPlay).Append(TopMenu).Any(t => t?.ObjectType == TitleObjectType.Bdj);

    internal IndexFile(BitReader reader, Action<string>? onWarning)
    {
        Version = NavigationFile.ReadHeader(reader, "INDX");
        uint indexStart = reader.ReadUInt32();
        uint extensionStart = reader.ReadUInt32();
        AppInfo = new IndexAppInfo(reader);

        reader.Seek(indexStart);
        uint length = reader.ReadUInt32();
        reader.Require(length, "index");
        var firstPlay = new IndexTitle(reader, false, onWarning);
        var topMenu = new IndexTitle(reader, false, onWarning);
        FirstPlay = firstPlay.Exists ? firstPlay : null;
        TopMenu = topMenu.Exists ? topMenu : null;

        int count = reader.ReadUInt16();
        if (count == 0 && FirstPlay is null && TopMenu is null)
            throw new BlurayException("The index has no titles.");
        reader.Require(12L * count, "title list");
        var titles = new IndexTitle[count];
        for (int i = 0; i < count; i++)
            titles[i] = new IndexTitle(reader, true, onWarning);
        Titles = titles.AsReadOnly();

        (byte, bool, bool, bool, byte)? uhd = null;
        NavigationFile.ReadExtensions(reader, extensionStart, (id1, id2) =>
        {
            if (id1 == 3 && id2 == 1)
                uhd = ReadUhdExtension(reader);
        });
        if (uhd is not null)
            (UhdDiscType, Has4K, HasHdrPlus, HasDolbyVision, HdrFlags) = uhd.Value;
    }

    private static (byte DiscType, bool Has4K, bool HasHdrPlus, bool HasDolbyVision, byte HdrFlags)? ReadUhdExtension(BitReader reader)
    {
        uint length = reader.ReadUInt32();
        if (length < 8)
            return null;
        byte discType = reader.ReadBits<byte>(4);
        reader.Skip(3);
        bool has4K = reader.ReadBit();
        reader.Skip(8 + 3);
        bool hasHdrPlus = reader.ReadBit();
        reader.Skip(1);
        bool hasDolbyVision = reader.ReadBit();
        byte hdrFlags = reader.ReadBits<byte>(2);
        return (discType, has4K, hasHdrPlus, hasDolbyVision, hdrFlags);
    }
}
