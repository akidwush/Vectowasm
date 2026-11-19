using System.Buffers.Binary;
using Microsoft.JSInterop;
using Vecto.Cli;
using Vecto.Core;

namespace Vecto.ImageDecoding;

/// <summary>
/// PNG uses managed C# pixel decoding and JPEG the original Vecto decoder.
/// Unsupported PNG layouts, WebP, BMP and JPEG fallbacks use browser decode. Tracing, geometry and SVG generation remain C#.
/// </summary>
public static class RasterDecoder
{
    public const long MaxPixels = 8_000_000;

    public static async ValueTask<RasterImage> DecodeAsync(byte[] encoded, IJSRuntime js)
    {
        // Decode common PNGs byte-for-byte in C#, avoiding Canvas RGBA alpha
        // premultiplication and browser-specific color-space conversions.
        if (PngDecoder.TryDecode(encoded) is { } png)
        {
            Validate(png);
            return png;
        }

        if (JpegDecoder.IsJpeg(encoded) && JpegDecoder.TryDecode(encoded) is { } jpeg)
        {
            Validate(jpeg);
            return jpeg;
        }

        byte[] packet = await js.InvokeAsync<byte[]>("vecto.decodeRaster", encoded);
        if (packet.Length < 8)
            throw new InvalidDataException("Invalid browser image decode packet.");
        int width = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(0, 4));
        int height = BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4, 4));
        if (width <= 0 || height <= 0 || (long)width * height > MaxPixels)
            throw new InvalidDataException("Image exceeds 8 megapixels.");
        if (packet.Length != 8 + checked(width * height * 4))
            throw new InvalidDataException("Invalid RGBA buffer length.");
        return new RasterImage(width, height, packet[8..]);
    }

    private static void Validate(RasterImage image)
    {
        if ((long)image.Width * image.Height > MaxPixels)
            throw new InvalidDataException("Image exceeds 8 megapixels.");
    }
}
