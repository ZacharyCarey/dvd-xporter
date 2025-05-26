
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

[Flags]
public enum UserOperationFlag : uint
{
    None = 0,
    TimePlayOrSearch = 0x00000001,
    PttPlayOrSearch = 0x00000002,
    TitlePlay = 0x00000004,
    Stop = 0x00000008,
    GoUp = 0x00000010,
    TimeOrPttSearch = 0x00000020,
    TopPgOrPrevPgSearch = 0x00000040,
    NextPgSearch = 0x00000080,
    ForwardScan = 0x00000100,
    BackwardScan = 0x00000200,
    MenuCallTitle = 0x00000400,
    MenuCallRoot = 0x00000800,
    MenuCallSubpicture = 0x00001000,
    MenuCallAudio = 0x00002000,
    MenuCallAngle = 0x00004000,
    MenuCallPtt = 0x00008000,
    Resume = 0x00010000,
    ButtonSelectOrActivate = 0x00020000,
    StillOff = 0x00040000,
    PauseOn = 0x00080000,
    AudioStreamChange = 0x00100000,
    SubpictureStreamChange = 0x00200000,
    AngleChange = 0x00400000,
    KaraokeAudioMixChange = 0x00800000,
    VideoPresentationModeChange = 0x01000000
}

/// <summary>
/// User operations
/// <see cref="http://www.mpucoder.com/DVD/uops.html"/>
/// </summary>
public class UserOperations
{
    public readonly UserOperationFlag Flags;

    public bool TimePlayOrSearch => Flags.HasFlag(UserOperationFlag.TimePlayOrSearch);
    public bool PttPlayOrSearch => Flags.HasFlag(UserOperationFlag.PttPlayOrSearch);
    public bool TitlePlay => Flags.HasFlag(UserOperationFlag.TitlePlay);
    public bool Stop => Flags.HasFlag(UserOperationFlag.Stop);
    public bool GoUp => Flags.HasFlag(UserOperationFlag.GoUp);
    public bool TimeOrPttSearch => Flags.HasFlag(UserOperationFlag.TimeOrPttSearch);
    public bool TopPgOrPrevPgSearch => Flags.HasFlag(UserOperationFlag.TopPgOrPrevPgSearch);
    public bool NextPgSearch => Flags.HasFlag(UserOperationFlag.NextPgSearch);
    public bool ForwardScan => Flags.HasFlag(UserOperationFlag.ForwardScan);
    public bool BackwardScan => Flags.HasFlag(UserOperationFlag.BackwardScan);
    public bool MenuCallTitle => Flags.HasFlag(UserOperationFlag.MenuCallTitle);
    public bool MenuCallRoot => Flags.HasFlag(UserOperationFlag.MenuCallRoot);
    public bool MenuCallSubpicture => Flags.HasFlag(UserOperationFlag.MenuCallSubpicture);
    public bool MenuCallAudio => Flags.HasFlag(UserOperationFlag.MenuCallAudio);
    public bool MenuCallAngle => Flags.HasFlag(UserOperationFlag.MenuCallAngle);
    public bool MenuCallPtt => Flags.HasFlag(UserOperationFlag.MenuCallPtt);
    public bool Resume => Flags.HasFlag(UserOperationFlag.Resume);
    public bool ButtonSelectOrActivate => Flags.HasFlag(UserOperationFlag.ButtonSelectOrActivate);
    public bool StillOff => Flags.HasFlag(UserOperationFlag.StillOff);
    public bool PauseOn => Flags.HasFlag(UserOperationFlag.PauseOn);
    public bool AudioStreamChange => Flags.HasFlag(UserOperationFlag.AudioStreamChange);
    public bool SubpictureStreamChange => Flags.HasFlag(UserOperationFlag.SubpictureStreamChange);
    public bool AngleChange => Flags.HasFlag(UserOperationFlag.AngleChange);
    public bool KaraokeAudioMixChange => Flags.HasFlag(UserOperationFlag.KaraokeAudioMixChange);
    public bool VideoPresentationModeChange => Flags.HasFlag (UserOperationFlag.VideoPresentationModeChange);

    internal UserOperations(DvdStream file)
    {
        uint flags = file.Read<uint>();
        Util.AssertZero(flags & 0xFE000000);

        Flags = (UserOperationFlag)flags;

        //BitReader bits = new BitReader(file.Stream);
    }
}