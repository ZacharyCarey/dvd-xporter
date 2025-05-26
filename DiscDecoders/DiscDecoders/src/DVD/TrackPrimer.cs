namespace DiscDecoders;

/// <summary>
/// Puts one extra sector in front of a DVD cell's program stream, holding an empty packet for the video and
/// for each wanted audio and subtitle stream, in the order asked for.
/// </summary>
/// <remarks>
/// A program stream carries no list of its streams, so players such as VLC number tracks in the order they
/// first meet them, which for a subtitle can be minutes in, and differs after a seek. With every wanted stream
/// announced up front, a player creates its tracks in this order before reading any real data, so its n-th audio
/// (or subtitle) track is always the clip's n-th. The packets carry no data, so nothing is decoded from them.
/// </remarks>
internal static class TrackPrimer
{
    private const int SectorSize = 2048;
    private const int PackHeaderSize = 14;

    /// <summary>The cell stream with the primer sector in front. The result owns <paramref name="cell"/>.</summary>
    /// <param name="streamIds">MPEG stream IDs: 0xC0-0xDF for MPEG audio, private stream 1 substreams by their own ID
    /// (0x20-0x3F subtitles, 0x80 AC-3, 0x88 DTS, 0xA0 LPCM, ...).</param>
    public static Stream Wrap(Stream cell, IReadOnlyList<int> streamIds)
    {
        var sector = new byte[SectorSize];
        var pack = new byte[PackHeaderSize];
        cell.Position = 0;
        cell.ReadExactly(pack);
        cell.Position = 0;

        // Reuse the cell's first pack header, so the primer's clock reference matches the cell's start
        int at = 0;
        Write(sector, ref at, pack);
        WritePes(sector, ref at, 0xE0, []);
        foreach (var id in streamIds)
        {
            if (id is >= 0xC0 and <= 0xDF)
                WritePes(sector, ref at, (byte)id, []);
            else if (id is >= 0x20 and <= 0x3F)
                // Subtitles: substream ID
                WritePes(sector, ref at, 0xBD, [(byte)id]);
            else if (id is >= 0xA0 and <= 0xA7)
                // LPCM: substream ID, frame count, first access unit, then its 3-byte audio header
                WritePes(sector, ref at, 0xBD, [(byte)id, 0, 0, 0, 0, 0, 0]);
            else
                // AC-3, DTS, SDDS: substream ID, frame count, first access unit
                WritePes(sector, ref at, 0xBD, [(byte)id, 0, 0, 0]);
        }

        // Pad the rest of the sector with a padding packet
        int padding = SectorSize - at - 6;
        Write(sector, ref at, [0x00, 0x00, 0x01, 0xBE, (byte)(padding >> 8), (byte)padding]);
        Array.Fill(sector, (byte)0xFF, at, padding);

        return new PrefixedStream(sector, cell);
    }

    /// <summary>An MPEG-2 PES packet with no timestamps.</summary>
    private static void WritePes(byte[] sector, ref int at, byte streamId, byte[] payload)
    {
        int length = 3 + payload.Length; // flags (2 bytes) + header data length (1 byte) + payload
        Write(sector, ref at, [0x00, 0x00, 0x01, streamId, (byte)(length >> 8), (byte)length, 0x81, 0x00, 0x00]);
        Write(sector, ref at, payload);
    }

    private static void Write(byte[] sector, ref int at, byte[] bytes)
    {
        Array.Copy(bytes, 0, sector, at, bytes.Length);
        at += bytes.Length;
    }

    /// <summary>A few bytes followed by another stream, read and seeked as one.</summary>
    private sealed class PrefixedStream(byte[] prefix, Stream inner) : Stream
    {
        private long _position;

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => prefix.Length + inner.Length;
        public override long Position { get => _position; set => Seek(value, SeekOrigin.Begin); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position < prefix.Length)
            {
                int n = (int)Math.Min(count, prefix.Length - _position);
                Array.Copy(prefix, _position, buffer, offset, n);
                _position += n;
                return n;
            }

            inner.Position = _position - prefix.Length;
            int read = inner.Read(buffer, offset, count);
            _position += read;
            return read;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Current => _position + offset,
                SeekOrigin.End => Length + offset,
                _ => offset,
            };
            if (_position < 0)
                throw new IOException("Offset went before the start of the stream.");
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
