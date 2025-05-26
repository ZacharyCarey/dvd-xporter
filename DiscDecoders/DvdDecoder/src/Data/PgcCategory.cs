
namespace DiscDecoders.DvdDecoder;

public enum MenuType
{
    None = 0,
    Title = 2,
    Root = 3,
    Subpicture = 4,
    Audio = 5,
    Angle = 6,
    Chapter = 7
}

public class PgcCategory
{
    public readonly bool IsEntryPGC;

    /// <summary>
    /// Ony for VTS PGCI's, otherwise -1
    /// </summary>
    public readonly int TitleNumber;

    /// <summary>
    /// Only for VGM PGCI's, otherwise MenuType.None
    /// </summary>
    public readonly MenuType MenuType;

    public readonly byte BlockType;
    public readonly byte BlockMode;

    public readonly ushort ParentalManagementMask;

    private PgcCategory(bool isEntry, int title, MenuType menu, byte blockType, byte blockMode, ushort parental)
    {
        this.IsEntryPGC = isEntry;
        this.TitleNumber = title;
        this.MenuType = menu;
        this.BlockType = blockType;
        this.BlockMode = blockMode;
        this.ParentalManagementMask = parental;
    }

    internal static PgcCategory Parse(DvdStream file, bool isVMG)
    {
        return isVMG ? FromVmgPGCI(file.Read<uint>()) : FromVtsPGCI(file.Read<uint>());
    }

    internal static PgcCategory FromVtsPGCI(uint data)
    {
        bool isEntry = (data >> 31) != 0;
        int title = (int)((data >> 24) & 0x7F);
        ushort parental = (ushort)(data & 0xFFFF);
        byte blockType = (byte)((data >> 22) & 0b11);
        byte blockMode = (byte)((data >> 20) & 0b11);
        Util.AssertZero((data >> 16) & 0x0F);
        return new PgcCategory(isEntry, title, MenuType.None, blockType, blockMode, parental);
    }

    internal static PgcCategory FromVmgPGCI(uint data)
    {
        bool isEntry = (data >> 31) != 0;
        int menu = (int)((data >> 24) & 0x0F);
        ushort parental = (ushort)(data & 0xFFFF);
        if (!isEntry) Util.AssertZero(menu);
        byte blockType = (byte)((data >> 22) & 0b11);
        byte blockMode = (byte)((data >> 20) & 0b11);
        Util.AssertZero((data >> 16) & 0x0F);
        return new PgcCategory(isEntry, -1, (MenuType)menu, blockType, blockMode, parental);
    }
}