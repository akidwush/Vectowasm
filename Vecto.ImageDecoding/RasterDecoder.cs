using SixLabors.ImageSharp;
using Vecto.Cli;
using Vecto.Core;
using Pixel = SixLabors.ImageSharp.PixelFormats.Rgba32;

namespace Vecto.ImageDecoding;

public static class RasterDecoder
{
    public const long MaxPixels = 8_000_000;

    public static RasterImage Decode(byte[] encoded)
    {
        var info = Image.Identify(encoded) ?? throw new InvalidDataException("Unknown image format.");
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels)
            throw new InvalidDataException($"Image exceeds {MaxPixels:N0} pixels: {info.Width}x{info.Height}.");
        if (JpegDecoder.IsJpeg(encoded) && JpegDecoder.TryDecode(encoded) is { } jpeg)
            return jpeg;
        using var img = Image.Load<Pixel>(encoded);
        var pixels = new byte[checked(img.Width * img.Height * 4)];
        img.CopyPixelDataTo(pixels);
        return new RasterImage(img.Width, img.Height, pixels);
    }
}
