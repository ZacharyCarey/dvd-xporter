
using System.Collections.ObjectModel;

namespace DiscDecoders.DvdDecoder;

/// <summary>
/// Type to store per-command data
/// </summary>
public class VmCommand
{
    public readonly ReadOnlyCollection<byte> Bytes;

    internal VmCommand(DvdStream file)
    {
        byte[] bytes = new byte[8];
        for (int i = 0; i < 8; i++)
            bytes[i] = file.Read<byte>();
        this.Bytes = bytes.AsReadOnly();
    }
}

/// <summary>
/// PGC Command Table <see cref="http://www.mpucoder.com/DVD/pgc.html#cmd"/>
/// </summary>
public class PgcCommandTable
{
    internal const int COMMAND_DATA_SIZE = 8;
    internal const int PGC_COMMAND_TBL_SIZE = 8;

    public readonly ReadOnlyCollection<VmCommand> PreCommands;
    public readonly ReadOnlyCollection<VmCommand> PostCommands;
    public readonly ReadOnlyCollection<VmCommand> CellCommands;

    internal PgcCommandTable(DvdStream file, uint offset)
    {
        file.Seek(offset);

        // Read data
        ushort nr_of_pre = file.Read<ushort>();
        ushort nr_of_post = file.Read<ushort>();
        ushort nr_of_cell = file.Read<ushort>();
        ushort last_byte = file.Read<ushort>();

        Util.Assert(nr_of_pre + nr_of_post + nr_of_cell <= 255, "Too many VM commands.");
        Util.Assert((nr_of_pre + nr_of_post + nr_of_cell) * COMMAND_DATA_SIZE + PGC_COMMAND_TBL_SIZE <= last_byte + 1, "Size of VM commands is too large.");

        // Read additional data
        if (nr_of_pre != 0)
        {
            VmCommand[] pre_cmds = new VmCommand[nr_of_pre];
            for (int i = 0; i < nr_of_pre; i++)
                pre_cmds[i] = new VmCommand(file);
            PreCommands = pre_cmds.AsReadOnly();
        }
        else PreCommands = Array.Empty<VmCommand>().AsReadOnly();

        if (nr_of_post != 0)
        {
            VmCommand[] post_cmds = new VmCommand[nr_of_post];
            for (int i = 0; i < nr_of_post; i++)
                post_cmds[i] = new VmCommand(file);
            PostCommands = post_cmds.AsReadOnly();
        }
        else PostCommands = Array.Empty<VmCommand>().AsReadOnly();

        if (nr_of_cell != 0)
        {
            VmCommand[] cell_cmds = new VmCommand[nr_of_cell];
            for (int i = 0; i < nr_of_cell; i++)
                cell_cmds[i] = new VmCommand(file);
            CellCommands = cell_cmds.AsReadOnly();
        }
        else CellCommands = Array.Empty<VmCommand>().AsReadOnly();
    }
}