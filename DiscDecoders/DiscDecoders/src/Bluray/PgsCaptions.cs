using System.Buffers.Binary;
using DiscDecoders.Common;

namespace DiscDecoders;

/// <summary>
/// Draws a Blu-ray subtitle stream's (PGS) captions. A display set's composition says which objects (run-length
/// coded pictures) show where, coloured from a palette; objects and palettes last for the epoch, so later sets can
/// reuse them. A set showing nothing takes the caption before it off the screen.
/// </summary>
internal sealed class PgsCaptions
{
    private const byte PaletteSegment = 0x14;
    private const byte ObjectSegment = 0x15;
    private const byte CompositionSegment = 0x16;
    private const byte EndSegment = 0x80;
    private const byte EpochStart = 0x80;
    private const byte ForcedFlag = 0x40;
    private const byte CroppedFlag = 0x80;
    private const byte FirstFragment = 0x80;

    private readonly List<CaptionPicture> _captions = [];
    private readonly Dictionary<int, Rgba[]> _palettes = [];
    private readonly Dictionary<int, PgsObject> _objects = [];
    private PgsSegment? _composition;
    // The caption on screen, which the next set ends
    private int _showing = -1;

    private readonly record struct Rgba(byte R, byte G, byte B, byte A);

    private sealed class PgsObject
    {
        public int Width;
        public int Height;
        public List<byte> Data = [];
    }

    /// <summary>Reads a whole .m2ts for the captions of the subtitle stream with <paramref name="pid"/>, in order.</summary>
    public static List<CaptionPicture> Read(Stream m2ts, int pid, CancellationToken cancellation)
    {
        var reader = new PgsCaptions();
        PgsScanner.ReadSegments(m2ts, pid, reader.Add, cancellation);
        return reader._captions;
    }

    private void Add(PgsSegment segment)
    {
        var data = segment.Data;
        switch (segment.Type)
        {
            case CompositionSegment when data.Length >= 11:
                if (data[7] == EpochStart)
                {
                    _palettes.Clear();
                    _objects.Clear();
                }

                _composition = segment;
                break;
            case PaletteSegment when data.Length >= 2:
                var palette = _palettes.TryGetValue(data[0], out var existing) ? existing : new Rgba[256];
                // Entries are 5 bytes: index, Y, Cr, Cb, alpha
                for (int p = 2; p + 5 <= data.Length; p += 5)
                    palette[data[p]] = ToRgba(data[p + 1], data[p + 3], data[p + 2], data[p + 4]);
                _palettes[data[0]] = palette;
                break;
            case ObjectSegment when data.Length >= 4:
                int id = BinaryPrimitives.ReadUInt16BigEndian(data);
                if ((data[3] & FirstFragment) != 0 && data.Length >= 11)
                {
                    // Then a 3-byte length, the size, and the start of the coded picture
                    _objects[id] = new PgsObject
                    {
                        Width = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(7)),
                        Height = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(9)),
                        Data = [.. data.AsSpan(11)],
                    };
                }
                else if (_objects.TryGetValue(id, out var continued))
                {
                    continued.Data.AddRange(data.AsSpan(4));
                }

                break;
            case EndSegment:
                if (_composition is not null)
                    Show(_composition);
                _composition = null;
                break;
        }
    }

    /// <summary>A whole display set: ends the caption on screen, and draws the new one if it shows anything.</summary>
    private void Show(PgsSegment composition)
    {
        var start = new DiscTime((long)composition.Pts);
        var data = composition.Data;
        var palette = _palettes.GetValueOrDefault(data[9]);

        // Each object's picture and place, cropped if the composition says
        var parts = new List<(int X, int Y, int Width, int Height, byte[] Pixels, bool Forced)>();
        int p = 11;
        for (int i = 0; i < data[10] && p + 8 <= data.Length; i++)
        {
            int id = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p));
            byte flags = data[p + 3];
            int x = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 4));
            int y = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 6));
            bool cropped = (flags & CroppedFlag) != 0 && p + 16 <= data.Length;
            if (_objects.TryGetValue(id, out var picture) && picture.Width > 0 && picture.Height > 0)
            {
                var pixels = Decode(picture);
                var (cropX, cropY, width, height) = cropped
                    ? (BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 8)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 10)),
                        BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 12)), BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 14)))
                    : (0, 0, picture.Width, picture.Height);
                width = Math.Min(width, picture.Width - cropX);
                height = Math.Min(height, picture.Height - cropY);
                if (width > 0 && height > 0)
                    parts.Add((x, y, width, height, Crop(pixels, picture.Width, cropX, cropY, width, height), (flags & ForcedFlag) != 0));
            }

            p += cropped ? 16 : 8;
        }

        // A palette update repaints the caption on screen (e.g. fading it): the same picture goes on showing
        var caption = parts.Count > 0 && palette is not null ? Draw(start, parts, palette) : null;
        if (caption is not null && _showing >= 0 && Same(_captions[_showing], caption))
            return;

        if (_showing >= 0)
            _captions[_showing] = _captions[_showing] with { End = start };
        _showing = -1;
        if (caption is not null)
        {
            _captions.Add(caption);
            _showing = _captions.Count - 1;
        }
    }

    private static bool Same(CaptionPicture a, CaptionPicture b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height && a.Rgba.AsSpan().SequenceEqual(b.Rgba);

    /// <summary>The objects drawn together, in the rectangle around them all.</summary>
    private static CaptionPicture Draw(DiscTime start, List<(int X, int Y, int Width, int Height, byte[] Pixels, bool Forced)> parts,
        Rgba[] palette)
    {
        int left = parts.Min(o => o.X), top = parts.Min(o => o.Y);
        int width = parts.Max(o => o.X + o.Width) - left, height = parts.Max(o => o.Y + o.Height) - top;
        var rgba = new byte[width * height * 4];
        foreach (var part in parts)
        {
            for (int row = 0; row < part.Height; row++)
            {
                for (int column = 0; column < part.Width; column++)
                {
                    var colour = palette[part.Pixels[row * part.Width + column]];
                    if (colour.A == 0)
                        continue;
                    int at = ((part.Y - top + row) * width + part.X - left + column) * 4;
                    rgba[at] = colour.R;
                    rgba[at + 1] = colour.G;
                    rgba[at + 2] = colour.B;
                    rgba[at + 3] = colour.A;
                }
            }
        }

        return new CaptionPicture(start, null, parts.Any(o => o.Forced), left, top, width, height, rgba);
    }

    private static byte[] Crop(byte[] pixels, int stride, int x, int y, int width, int height)
    {
        if (x == 0 && y == 0 && width == stride && pixels.Length == width * height)
            return pixels;
        var cropped = new byte[width * height];
        for (int row = 0; row < height; row++)
            Array.Copy(pixels, (y + row) * stride + x, cropped, row * width, width);
        return cropped;
    }

    /// <summary>
    /// An object's palette indexes, row by row. Its run-length coding: a non-zero byte is one pixel of that colour; a
    /// zero starts a run (its next byte's top bits say whether the length takes one byte or two, and whether a colour
    /// follows, else it's colour 0), or with another zero ends the line.
    /// </summary>
    private static byte[] Decode(PgsObject picture)
    {
        var pixels = new byte[picture.Width * picture.Height];
        var data = picture.Data;
        int at = 0, row = 0, column = 0;
        while (at < data.Count && row < picture.Height)
        {
            byte b = data[at++];
            int length = 1;
            byte colour = b;
            if (b == 0)
            {
                if (at >= data.Count)
                    break;
                byte flags = data[at++];
                if (flags == 0)
                {
                    row++;
                    column = 0;
                    continue;
                }

                length = flags & 0x3F;
                if ((flags & 0x40) != 0 && at < data.Count)
                    length = length << 8 | data[at++];
                colour = (flags & 0x80) != 0 && at < data.Count ? data[at++] : (byte)0;
            }

            int end = Math.Min(column + length, picture.Width);
            if (colour != 0)
                pixels.AsSpan(row * picture.Width + column, end - column).Fill(colour);
            column = end;
        }

        return pixels;
    }

    // Blu-ray subtitles are coloured in BT.709
    private static Rgba ToRgba(byte y, byte cb, byte cr, byte alpha)
    {
        double luma = 1.164 * (y - 16), blue = cb - 128, red = cr - 128;
        return new Rgba(Clamp(luma + 1.793 * red), Clamp(luma - 0.213 * blue - 0.533 * red), Clamp(luma + 2.112 * blue), alpha);
    }

    private static byte Clamp(double value) => (byte)Math.Clamp(Math.Round(value), 0, 255);
}
