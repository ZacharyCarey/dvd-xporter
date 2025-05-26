
using DiscDecoders.Common;

namespace DiscDecoders.DvdDecoder;

public class VobStream : Stream
{
    int currentVBO = -1;
    Stream? currentFile;
    IDiscFileSystem FileReader;
    long totalLength;
    long position = 0;
    bool IsMenu;
    IfoFile ifo;

    // debug tools
    public string? CurrentFile => (currentVBO < 0 || currentVBO >= 10) ? null : ifo.VobFiles[currentVBO].FileName;
    public long RelativeOffset => currentFile?.Position ?? 0;

    public VobStream(IDiscFileSystem fileReader, IfoFile title, bool menus)
    {
        this.ifo = title;
        this.FileReader = fileReader;
        this.IsMenu = menus;

        if (title.TitleNumber == 0)
        {
            IsMenu = true;
        }

        this.totalLength = (IsMenu ? ifo.VobFiles.Take(1) : ifo.VobFiles.Skip(1)).Sum(x => x.Size);
        OpenVOB(IsMenu ? 0 : 1);
    }

    protected override void Dispose(bool disposing)
    {
        currentFile?.Dispose();
    }

    public override bool CanRead => currentFile?.CanRead ?? false;

    public override bool CanSeek => currentFile?.CanSeek ?? false;

    public override bool CanWrite => false;

    public override long Length => totalLength;

    public override long Position
    {
        get => position;
        set => Seek(value, SeekOrigin.Begin);
    }

    public override void Flush()
    {
        currentFile?.Flush();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        int totalRead = 0;
        while (count > 0 && currentFile != null)
        {
            int read = currentFile.Read(buffer, offset, count);
            if (read == 0)
            {
                OpenNextVOB();
            }
            offset += read;
            count -= read;
            totalRead += read;
        }

        position += totalRead;
        return totalRead;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        if (origin == SeekOrigin.Current)
        {
            offset = position + offset;
        }
        else if (origin == SeekOrigin.End)
        {
            offset = this.Length + offset; // offset will be a negative number here
        }
        if (offset < 0) throw new IOException("Offset went before the start of the stream.");

        if (offset >= this.Length)
        {
            this.position = offset;
            OpenVOB(10); // Set stream to EOF
            return this.position;
        }

        if (IsMenu)
        {
            // We are in menu mode, there is only one file to seek
            OpenVOB(0);
            if (currentFile != null)
            {
                this.position = currentFile.Seek(offset, SeekOrigin.Begin);
                return this.position;
            }
        }
        else
        {
            long pos = offset;
            for (int i = 1; i < 10; i++)
            {
                long size = ifo.VobFiles[i].Size;
                if (pos < size)
                {
                    OpenVOB(i);
                    if (currentFile == null) break;
                    currentFile.Seek(pos, SeekOrigin.Begin);
                    position = offset;
                    return offset;
                }
                else
                {
                    pos -= size;
                }
            }
        }

        // EOF was reached
        position = this.Length;
        OpenVOB(10);
        return position;
    }

    public override void SetLength(long value)
    {
        throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        throw new NotSupportedException();
    }

    /// <summary>
    /// Will find the next valid VOB and attempt to open it
    /// </summary>
    private void OpenNextVOB()
    {
        if (currentVBO > 9)
        {
            return;
        }

        if (IsMenu)
        {
            // Opened in menu mode, there are no VOB's remaining
            OpenVOB(10); // Open a non-existent VOB to set the public state. VOB=10 will never exist
            return;
        }

        for (int i = currentVBO + 1; i <= 9; i++)
        {
            OpenVOB(i);
            if (currentFile != null)
            {
                return;
            }
        }

        OpenVOB(10); // Open a non-existent VOB to set the public state. VOB=10 will never exist
    }

    private void OpenVOB(int id)
    {
        if (currentVBO == id && currentFile != null)
        {
            currentFile.Seek(0, SeekOrigin.Begin);
            return;
        }

        if (currentFile != null)
        {
            currentFile.Close();
            currentFile.Dispose();
        }

        currentVBO = id;
        if ((currentVBO < 0 || currentVBO > 9) || (IsMenu && currentVBO != 0))
        {
            currentFile = null;
            return;
        }

        if (ifo.VobFiles[currentVBO].Size <= 0)
        {
            currentFile = null;
            return;
        }

        try
        {
            currentFile = FileReader.OpenFile(ifo.VobFiles[currentVBO].FileName);
        }
        catch (Exception ex)
        {
            throw new IOException($"Failed to open next VOB file.", ex);
        }
    }
}