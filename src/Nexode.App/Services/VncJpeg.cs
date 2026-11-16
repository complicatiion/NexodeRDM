using System.Runtime.InteropServices;
using SkiaSharp;

namespace Nexode.App.Services;

/// <summary>JPEG rectangles of the VNC Tight encoding, decoded with SkiaSharp (part of Avalonia's renderer).</summary>
public static class VncJpeg
{
    public static uint[]? Decode(byte[] data, int width, int height)
    {
        using var decoded = SKBitmap.Decode(data);
        if (decoded is null) return null;
        using var converted = decoded.ColorType == SKColorType.Bgra8888 ? null : decoded.Copy(SKColorType.Bgra8888);
        var bmp = converted ?? decoded;
        var result = new uint[width * height];
        var src = MemoryMarshal.Cast<byte, uint>(bmp.GetPixelSpan());
        var rows = Math.Min(height, bmp.Height);
        var cols = Math.Min(width, bmp.Width);
        for (var y = 0; y < rows; y++)
        {
            var line = src.Slice(y * (bmp.RowBytes / 4), cols);
            for (var x = 0; x < cols; x++) result[y * width + x] = line[x] | 0xFF000000u;
        }

        return result;
    }
}
