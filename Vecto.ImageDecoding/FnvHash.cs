using System.Text;

namespace Vecto.ImageDecoding;

public static class FnvHash
{
    public static string Hex(ReadOnlySpan<byte> bytes)
    {
        ulong h = 14695981039346656037UL;
        foreach (byte b in bytes)
        {
            h ^= b;
            h = unchecked(h * 1099511628211UL);
        }
        return h.ToString("x16", System.Globalization.CultureInfo.InvariantCulture);
    }

    public static string Text(string value) => Hex(Encoding.UTF8.GetBytes(value));
}
