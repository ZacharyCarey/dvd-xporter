using System.Collections.ObjectModel;
using DiscDecoders.Common;

namespace DiscDecoders.BlurayDecoder;

public enum StillMode : byte
{
    /// <summary>Playback carries on.</summary>
    None = 0,
    /// <summary>The last picture stays for the still time.</summary>
    Time = 1,
    /// <summary>The last picture stays until the viewer moves on.</summary>
    Infinite = 2,
}

/// <summary>How a play item follows the one before it.</summary>
public enum ConnectionCondition : byte
{
    NotSeamless = 1,
    /// <summary>Seamless, though the clip's timestamps restart.</summary>
    SeamlessCleanBreak = 5,
    Seamless = 6,
}

public enum MarkType : byte
{
    /// <summary>A chapter.</summary>
    Entry = 1,
    /// <summary>A point other playlists can link to.</summary>
    Link = 2,
}

public enum SubPathType : byte
{
    BrowsableSlideshowAudio = 2,
    InteractiveGraphicsMenu = 3,
    TextSubtitle = 4,
    OutOfMuxSynchronous = 5,
    AsyncPictureInPicture = 6,
    SyncPictureInPicture = 7,
    StereoscopicVideo = 8,
    DolbyVisionEnhancementLayer = 10,
}

/// <summary>The playlist's application info: how it plays.</summary>
public class PlaylistAppInfo
{
    /// <summary>1 for sequential playback, 2 for random, 3 for shuffle.</summary>
    public readonly byte PlaybackType;

    /// <summary>How many play items random or shuffle playback plays.</summary>
    public readonly ushort PlaybackCount;

    /// <summary>Whether the viewer can't jump around in it (e.g. a warning screen).</summary>
    public readonly bool RandomAccessProhibited;

    public readonly bool AudioMix;
    public readonly bool LosslessBypass;

    /// <summary>For 3D video: whether the base view is the right eye's rather than the left's.</summary>
    public readonly bool MvcBaseViewIsRight;

    public readonly bool SdrConversionNotification;

    internal PlaylistAppInfo(BitReader reader)
    {
        uint length = reader.ReadUInt32();
        // One that runs past the end of the file is left at its defaults
        if (reader.AvailableBytes < length)
            return;

        reader.Skip(8);
        PlaybackType = reader.ReadByte();
        PlaybackCount = PlaybackType is 2 or 3 ? reader.ReadUInt16() : SkipUInt16(reader);
        reader.Skip(64); // UO mask (menus only)
        RandomAccessProhibited = reader.ReadBit();
        AudioMix = reader.ReadBit();
        LosslessBypass = reader.ReadBit();
        MvcBaseViewIsRight = reader.ReadBit();
        SdrConversionNotification = reader.ReadBit();
    }

    private static ushort SkipUInt16(BitReader reader)
    {
        reader.Skip(16);
        return 0;
    }
}

/// <summary>A clip a play item or sub play item plays: for a multi-angle play item, one angle's.</summary>
public class PlayItemClip
{
    /// <summary>The clip's number, e.g. "00012" for 00012.m2ts and 00012.clpi.</summary>
    public readonly string ClipId;

    /// <summary>"M2TS" (or "FMTS").</summary>
    public readonly string CodecId;

    /// <summary>Which of the clip's STC sequences (timestamp runs) the in and out times are in.</summary>
    public readonly byte StcId;

    internal PlayItemClip(string clipId, string codecId, byte stcId, Action<string>? onWarning)
    {
        ClipId = clipId;
        CodecId = codecId;
        StcId = stcId;
        if (codecId is not ("M2TS" or "FMTS"))
            onWarning?.Invoke($"Clip {clipId} has an unexpected codec identifier \"{codecId}\".");
    }

    internal PlayItemClip(BitReader reader, Action<string>? onWarning)
        : this(reader.ReadString(5), reader.ReadString(4), reader.ReadByte(), onWarning)
    {
    }
}

/// <summary>One part of a playlist: a stretch of a clip (or, for a multi-angle part, of one clip per angle).</summary>
public class PlayItem
{
    /// <summary>The clip each angle plays: one, unless <see cref="IsMultiAngle"/>.</summary>
    public readonly ReadOnlyCollection<PlayItemClip> Clips;

    public readonly bool IsMultiAngle;

    /// <summary>Whether the angles have different audio.</summary>
    public readonly bool IsDifferentAudio;

    /// <summary>Whether switching angle is seamless.</summary>
    public readonly bool IsSeamlessAngle;

    public readonly ConnectionCondition ConnectionCondition;

    public readonly bool RandomAccessProhibited;

    public readonly StillMode StillMode;

    /// <summary>How long the last picture stays, for <see cref="StillMode.Time"/>.</summary>
    public readonly DiscTime StillTime;

    /// <summary>The streams the viewer can choose while it plays.</summary>
    public readonly StreamTable Streams;

    // The clip timestamps it starts and ends at, 45 kHz
    internal readonly uint InTime45k;
    internal readonly uint OutTime45k;

    /// <summary>Where in the clip it starts, on the clip's timeline (see <see cref="ClipInformation.PresentationStart"/>).</summary>
    public DiscTime InTime => NavigationFile.ToDiscTime(InTime45k);

    /// <summary>Where in the clip it ends, on the clip's timeline.</summary>
    public DiscTime OutTime => NavigationFile.ToDiscTime(OutTime45k);

    public int AngleCount => Clips.Count;

    internal PlayItem(BitReader reader, Action<string>? onWarning)
    {
        int length = reader.ReadUInt16();
        long start = reader.Position;
        if (length < 18)
            throw new BlurayException($"Invalid play item length {length}.");
        reader.Require(length, "play item");

        string clipId = reader.ReadString(5);
        string codecId = reader.ReadString(4);
        reader.Skip(11);
        IsMultiAngle = reader.ReadBit();
        ConnectionCondition = (ConnectionCondition)reader.ReadBits<byte>(4);
        if (ConnectionCondition is not (ConnectionCondition.NotSeamless or ConnectionCondition.SeamlessCleanBreak or ConnectionCondition.Seamless))
            onWarning?.Invoke($"Clip {clipId} has an unexpected connection condition {(int)ConnectionCondition}.");
        byte stcId = reader.ReadByte();
        InTime45k = reader.ReadUInt32();
        OutTime45k = reader.ReadUInt32();
        reader.Skip(64); // UO mask (menus only)
        RandomAccessProhibited = reader.ReadBit();
        reader.Skip(7);
        StillMode = (StillMode)reader.ReadByte();
        if (StillMode == StillMode.Time)
            StillTime = DiscTime.FromSeconds(reader.ReadUInt16());
        else
            reader.Skip(16);

        var clips = new List<PlayItemClip> { new(clipId, codecId, stcId, onWarning) };
        if (IsMultiAngle)
        {
            int angles = Math.Max(1, (int)reader.ReadByte());
            reader.Skip(6);
            IsDifferentAudio = reader.ReadBit();
            IsSeamlessAngle = reader.ReadBit();
            for (int i = 1; i < angles; i++)
                clips.Add(new PlayItemClip(reader, onWarning));
        }
        Clips = clips.AsReadOnly();

        Streams = new StreamTable(reader, onWarning);
        reader.Seek(start + length);
    }
}

/// <summary>A playlist mark: a chapter, or a point other playlists link to.</summary>
public class PlaylistMark
{
    public readonly MarkType Type;

    /// <summary>The play item it's in (an index into <see cref="Playlist.PlayItems"/>).</summary>
    public readonly ushort PlayItemIndex;

    /// <summary>The PID of the stream it's an entry point of.</summary>
    public readonly ushort EntryPid;

    internal readonly uint Time45k;
    internal readonly uint Duration45k;

    /// <summary>Where it is, on the play item's clip's timeline.</summary>
    public DiscTime Time => NavigationFile.ToDiscTime(Time45k);

    /// <summary>How long it lasts, if the disc says; usually zero.</summary>
    public DiscTime Duration => NavigationFile.ToDiscTime(Duration45k);

    internal PlaylistMark(BitReader reader)
    {
        reader.Skip(8);
        Type = (MarkType)reader.ReadByte();
        PlayItemIndex = reader.ReadUInt16();
        Time45k = reader.ReadUInt32();
        EntryPid = reader.ReadUInt16();
        Duration45k = reader.ReadUInt32();
    }
}

/// <summary>A stretch of a sub path's clip, played alongside a play item.</summary>
public class SubPlayItem
{
    /// <summary>The clips it can play: one, unless it's multi-clip.</summary>
    public readonly ReadOnlyCollection<PlayItemClip> Clips;

    public readonly ConnectionCondition ConnectionCondition;

    /// <summary>The play item it plays alongside (an index into <see cref="Playlist.PlayItems"/>).</summary>
    public readonly ushort SyncPlayItemIndex;

    internal readonly uint InTime45k;
    internal readonly uint OutTime45k;
    internal readonly uint SyncTime45k;

    public DiscTime InTime => NavigationFile.ToDiscTime(InTime45k);
    public DiscTime OutTime => NavigationFile.ToDiscTime(OutTime45k);

    /// <summary>When it starts, on the play item's clip's timeline.</summary>
    public DiscTime SyncTime => NavigationFile.ToDiscTime(SyncTime45k);

    internal SubPlayItem(BitReader reader, Action<string>? onWarning)
    {
        int length = reader.ReadUInt16();
        long start = reader.Position;
        if (length < 24)
            throw new BlurayException($"Invalid sub play item length {length}.");
        reader.Require(length, "sub play item");

        string clipId = reader.ReadString(5);
        string codecId = reader.ReadString(4);
        reader.Skip(27);
        ConnectionCondition = (ConnectionCondition)reader.ReadBits<byte>(4);
        if (ConnectionCondition is not (ConnectionCondition.NotSeamless or ConnectionCondition.SeamlessCleanBreak or ConnectionCondition.Seamless))
            onWarning?.Invoke($"Clip {clipId} has an unexpected connection condition {(int)ConnectionCondition}.");
        bool isMultiClip = reader.ReadBit();
        byte stcId = reader.ReadByte();
        InTime45k = reader.ReadUInt32();
        OutTime45k = reader.ReadUInt32();
        SyncPlayItemIndex = reader.ReadUInt16();
        SyncTime45k = reader.ReadUInt32();

        var clips = new List<PlayItemClip> { new(clipId, codecId, stcId, onWarning) };
        if (isMultiClip)
        {
            int count = Math.Max(1, (int)reader.ReadByte());
            for (int i = 1; i < count; i++)
                clips.Add(new PlayItemClip(reader, onWarning));
        }
        Clips = clips.AsReadOnly();
        reader.Seek(start + length);
    }
}

/// <summary>Clips played alongside the playlist's own, e.g. text subtitles, picture-in-picture video or a menu.</summary>
public class SubPath
{
    public readonly SubPathType Type;

    public readonly bool IsRepeat;

    public readonly ReadOnlyCollection<SubPlayItem> Items;

    internal SubPath(BitReader reader, Action<string>? onWarning)
    {
        uint length = reader.ReadUInt32();
        long start = reader.Position;
        reader.Skip(8);
        Type = (SubPathType)reader.ReadByte();
        reader.Skip(15);
        IsRepeat = reader.ReadBit();
        reader.Skip(8);
        int count = reader.ReadByte();
        var items = new SubPlayItem[count];
        for (int i = 0; i < count; i++)
            items[i] = new SubPlayItem(reader, onWarning);
        Items = items.AsReadOnly();
        reader.Seek(start + length);
    }
}

/// <summary>
/// A playlist (.mpls file): the clips a title plays, in order, with its chapters and the streams to choose from
/// <see href="https://github.com/lw/BluRay/wiki/MPLS"/>.
/// </summary>
public class Playlist
{
    /// <summary>The playlist's file name, e.g. "00001.mpls".</summary>
    public readonly string FileName;

    /// <summary>The playlist's number, from its file name.</summary>
    public readonly uint Number;

    /// <summary>The file's format version, e.g. "0200" (0300 for UHD).</summary>
    public readonly string Version;

    public readonly PlaylistAppInfo AppInfo;

    public readonly ReadOnlyCollection<PlayItem> PlayItems;

    public readonly ReadOnlyCollection<SubPath> SubPaths;

    /// <summary>More sub paths, from the extension data (3D discs').</summary>
    public readonly ReadOnlyCollection<SubPath> ExtensionSubPaths = ReadOnlyCollection<SubPath>.Empty;

    /// <summary>The chapters and link points, in order.</summary>
    public readonly ReadOnlyCollection<PlaylistMark> Marks;

    /// <summary>Where picture-in-picture (secondary) video is drawn; empty if the playlist has none.</summary>
    public readonly ReadOnlyCollection<PipMetadata> PictureInPicture = ReadOnlyCollection<PipMetadata>.Empty;

    /// <summary>The HDR mastering display and light levels (UHD only); empty if the playlist has none.</summary>
    public readonly ReadOnlyCollection<HdrStaticMetadata> HdrMetadata = ReadOnlyCollection<HdrStaticMetadata>.Empty;

    /// <summary>How long it plays: its play items' lengths together.</summary>
    public DiscTime Duration => NavigationFile.ToDiscTime(Duration45k);

    /// <summary>The most angles any of its play items has; 0 if it has none.</summary>
    public int AngleCount => PlayItems.Count > 0 ? PlayItems.Max(p => p.AngleCount) : 0;

    public IEnumerable<PlaylistMark> Chapters => Marks.Where(m => m.Type == MarkType.Entry);

    internal uint Duration45k
    {
        get
        {
            uint duration = 0;
            foreach (var item in PlayItems)
                duration += item.OutTime45k - item.InTime45k;
            return duration;
        }
    }

    internal Playlist(BitReader reader, string fileName, uint number, Action<string>? onWarning)
    {
        FileName = fileName;
        Number = number;
        Version = NavigationFile.ReadHeader(reader, "MPLS");
        reader.Require(40, "header");
        uint listStart = reader.ReadUInt32();
        uint markStart = reader.ReadUInt32();
        uint extensionStart = reader.ReadUInt32();
        reader.Skip(160);
        AppInfo = new PlaylistAppInfo(reader);

        // Play items and sub paths
        reader.Seek(listStart);
        uint length = reader.ReadUInt32();
        reader.Require(length, "play item list");
        reader.Skip(16);
        int itemCount = reader.ReadUInt16();
        int subPathCount = reader.ReadUInt16();
        var items = new PlayItem[itemCount];
        for (int i = 0; i < itemCount; i++)
            items[i] = new PlayItem(reader, onWarning);
        PlayItems = items.AsReadOnly();
        SubPaths = ReadSubPaths(reader, subPathCount, onWarning);

        // Marks
        reader.Seek(markStart);
        length = reader.ReadUInt32();
        reader.Require(length, "mark list");
        int markCount = reader.ReadUInt16();
        reader.Require(14L * markCount, "mark list");
        var marks = new PlaylistMark[markCount];
        for (int i = 0; i < markCount; i++)
            marks[i] = new PlaylistMark(reader);
        Marks = marks.AsReadOnly();

        ReadOnlyCollection<PipMetadata>? pip = null;
        ReadOnlyCollection<SubPath>? extensionSubPaths = null;
        ReadOnlyCollection<HdrStaticMetadata>? hdr = null;
        NavigationFile.ReadExtensions(reader, extensionStart, (id1, id2) =>
        {
            switch ((id1, id2))
            {
                case (1, 1):
                    pip = ReadPictureInPicture(reader);
                    break;
                case (2, 2):
                    extensionSubPaths = ReadExtensionSubPaths(reader, onWarning);
                    break;
                case (3, 5):
                    hdr = ReadHdrStaticMetadata(reader);
                    break;
            }
        });
        PictureInPicture = pip ?? PictureInPicture;
        ExtensionSubPaths = extensionSubPaths ?? ExtensionSubPaths;
        HdrMetadata = hdr ?? HdrMetadata;
    }

    private static ReadOnlyCollection<SubPath> ReadSubPaths(BitReader reader, int count, Action<string>? onWarning)
    {
        var subPaths = new SubPath[count];
        for (int i = 0; i < count; i++)
            subPaths[i] = new SubPath(reader, onWarning);
        return subPaths.AsReadOnly();
    }

    private static ReadOnlyCollection<SubPath>? ReadExtensionSubPaths(BitReader reader, Action<string>? onWarning)
    {
        uint length = reader.ReadUInt32();
        int count = reader.ReadUInt16();
        return length < 1 || count < 1 ? null : ReadSubPaths(reader, count, onWarning);
    }

    private static ReadOnlyCollection<PipMetadata>? ReadPictureInPicture(BitReader reader)
    {
        long start = reader.Position;
        uint length = reader.ReadUInt32();
        int count = reader.ReadUInt16();
        if (length < 1 || count < 1)
            return null;

        var blocks = new PipMetadata[count];
        for (int i = 0; i < count; i++)
            blocks[i] = new PipMetadata(reader, start);
        return blocks.AsReadOnly();
    }

    private static ReadOnlyCollection<HdrStaticMetadata>? ReadHdrStaticMetadata(BitReader reader)
    {
        uint length = reader.ReadUInt32();
        // At least one entry
        if (length < 32)
            return null;
        reader.Require(length, "HDR static metadata");
        int count = reader.ReadByte();
        if (count < 1)
            return null;
        reader.Skip(24);

        var entries = new HdrStaticMetadata[count];
        for (int i = 0; i < count; i++)
            entries[i] = new HdrStaticMetadata(reader);
        return entries.AsReadOnly();
    }
}
