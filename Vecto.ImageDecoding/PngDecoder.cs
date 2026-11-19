using System.Buffers.Binary;
using System.IO.Compression;
using Vecto.Core;

namespace Vecto.ImageDecoding;

/// <summary>
/// Managed, non-interlaced PNG decoder for lossless RGBA input on WebAssembly.
/// No canvas, color-management transform or alpha premultiplication.
/// Other PNG types fall back to the browser decoder.
/// </summary>
public static class PngDecoder
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static bool IsPng(ReadOnlySpan<byte> data) =>
        data.Length >= 8 && data[..8].SequenceEqual(Signature);

    public static RasterImage? TryDecode(byte[] data)
    {
        if (!IsPng(data)) return null;
        int width = 0, height = 0, depth = 0, kind = -1, interlace = -1;
        byte[]? palette = null, transparency = null;
        using var compressed = new MemoryStream();
        int position = 8;
        while (position <= data.Length - 12)
        {
            uint len = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(position, 4));
            if (len > int.MaxValue || (long)position + len + 12 > data.Length)
                throw new InvalidDataException("Truncated PNG chunk.");
            var name = data.AsSpan(position + 4, 4);
            var chunk = data.AsSpan(position + 8, (int)len);
            if (name.SequenceEqual("IHDR"u8))
            {
                if (chunk.Length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = BinaryPrimitives.ReadInt32BigEndian(chunk[..4]);
                height = BinaryPrimitives.ReadInt32BigEndian(chunk.Slice(4, 4));
                depth = chunk[8];
                kind = chunk[9];
                if (chunk[10] != 0 || chunk[11] != 0) return null;
                interlace = chunk[12];
            }
            else if (name.SequenceEqual("PLTE"u8))
                palette = chunk.ToArray();
            else if (name.SequenceEqual("tRNS"u8))
                transparency = chunk.ToArray();
            else if (name.SequenceEqual("IDAT"u8))
                compressed.Write(chunk);
            else if (name.SequenceEqual("IEND"u8))
                break;
            position += checked((int)len + 12);
        }

        if (width <= 0 || height <= 0 || (long)width * height > RasterDecoder.MaxPixels)
            throw new InvalidDataException("PNG exceeds 8 megapixels or has invalid dimensions.");
        if (interlace != 0) return null; // Adam7: browser fallback
        int channels = kind switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, 6 => 4, _ => 0 };
        if (channels == 0) return null;
        if (kind == 3 ? depth is not (1 or 2 or 4 or 8)
            : kind == 0 ? depth is not (1 or 2 or 4 or 8)
            : depth != 8) return null; // 16-bit and uncommon layouts: browser fallback
        if (kind == 3 && (palette is null || palette.Length % 3 != 0)) return null;
        if (compressed.Length == 0) throw new InvalidDataException("PNG is missing IDAT data.");

        int stride = checked((int)(((long)width * channels * depth + 7) / 8));
        int bpp = Math.Max(1, (channels * depth + 7) / 8);
        var current = new byte[stride];
        var previous = new byte[stride];
        var output = new byte[checked(width * height * 4)];
        compressed.Position = 0;
        using var inflator = new ZLibStream(compressed, CompressionMode.Decompress, leaveOpen: false);
        for (int y = 0; y < height; y++)
        {
            int filter = inflator.ReadByte();
            if (filter is < 0 or > 4) throw new InvalidDataException("Invalid PNG filter.");
            inflator.ReadExactly(current);
            for (int i = 0; i < stride; i++)
            {
                int a = i >= bpp ? current[i - bpp] : 0;
                int b = previous[i];
                int c = i >= bpp ? previous[i - bpp] : 0;
                int prediction = filter switch
                {
                    0 => 0,
                    1 => a,
                    2 => b,
                    3 => (a + b) / 2,
                    _ => Paeth(a, b, c)
                };
                current[i] = unchecked((byte)(current[i] + prediction));
            }

            for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                int offset = x * channels;
                byte r, g, b, alpha = 255;
                if (kind == 6)
                {
                    offset *= 1;
                    r = current[offset]; g = current[offset + 1];
                    b = current[offset + 2]; alpha = current[offset + 3];
                }
                else if (kind == 2)
                {
                    r = current[offset]; g = current[offset + 1]; b = current[offset + 2];
                    if (transparency?.Length >= 6 &&
                        r == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(0, 2)) &&
                        g == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(2, 2)) &&
                        b == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(4, 2)))
                        alpha = 0;
                }
                else if (kind == 4)
                {
                    r = g = b = current[offset]; alpha = current[offset + 1];
                }
                else if (kind == 0)
                {
                    int sample = depth == 8 ? current[x] : Packed(current, x, depth);
                    r = g = b = (byte)(sample * 255 / ((1 << depth) - 1));
                    if (transparency?.Length >= 2 &&
                        sample == BinaryPrimitives.ReadUInt16BigEndian(transparency.AsSpan(0, 2)))
                        alpha = 0;
                }
                else
                {
                    int index = depth == 8 ? current[x] : Packed(current, x, depth);
                    if (palette is null || index >= palette.Length / 3)
                        throw new InvalidDataException("Invalid PNG palette index.");
                    r = palette[index * 3]; g = palette[index * 3 + 1]; b = palette[index * 3 + 2];
                    if (transparency is not null && index < transparency.Length)
                        alpha = transparency[index];
                }
                output[p] = r;
                output[p + 1] = g;
                output[p + 2] = b;
                output[p + 3] = alpha;
            }
            (current, previous) = (previous, current);
        }
        return new RasterImage(width, height, output);
    }

    private static int Packed(byte[] row, int x, int bits)
    {
        int bit = x * bits;
        return (row[bit / 8] >> (8 - bits - bit % 8)) & ((1 << bits) - 1);
    }

    private static int Paeth(int a, int b, int c)
    {
        int p = a + b - c, da = Math.Abs(p - a), db = Math.Abs(p - b), dc = Math.Abs(p - c);
        return da <= db && da <= dc ? a : db <= dc ? b : c;
    }
}
