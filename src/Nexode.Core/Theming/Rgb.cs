using System.Globalization;

namespace Nexode.Core.Theming;

/// <summary>sRGB color, channels 0..1.</summary>
public readonly record struct Rgb(double R, double G, double B)
{
    public static Rgb FromHex(string hex)
    {
        var s = hex.Trim().TrimStart('#');
        if (s.Length == 8) s = s[2..];
        if (s.Length == 3) s = string.Concat(s.Select(c => new string(c, 2)));
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
        {
            throw new FormatException($"Invalid color '{hex}'.");
        }

        return new Rgb(((v >> 16) & 0xFF) / 255d, ((v >> 8) & 0xFF) / 255d, (v & 0xFF) / 255d);
    }

    public static bool TryParse(string? hex, out Rgb rgb)
    {
        try
        {
            rgb = FromHex(hex ?? "");
            return true;
        }
        catch (FormatException)
        {
            rgb = default;
            return false;
        }
    }

    public byte R8 => To8(R);
    public byte G8 => To8(G);
    public byte B8 => To8(B);

    public string ToHex() => $"#{R8:X2}{G8:X2}{B8:X2}";

    public uint ToArgb(byte alpha = 255) => (uint)(alpha << 24 | R8 << 16 | G8 << 8 | B8);

    public double Luminance => 0.2126 * Lin(R) + 0.7152 * Lin(G) + 0.0722 * Lin(B);

    public static double Contrast(Rgb a, Rgb b)
    {
        var la = a.Luminance;
        var lb = b.Luminance;
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    public Rgb Mix(Rgb other, double amount) =>
        new(R + (other.R - R) * amount, G + (other.G - G) * amount, B + (other.B - B) * amount);

    internal static double Lin(double c) => c <= 0.04045 ? c / 12.92 : Math.Pow((c + 0.055) / 1.055, 2.4);
    internal static double Gam(double c) => c <= 0.0031308 ? 12.92 * c : 1.055 * Math.Pow(c, 1 / 2.4) - 0.055;

    private static byte To8(double c) => (byte)Math.Clamp(Math.Round(c * 255), 0, 255);

    public override string ToString() => ToHex();
}

/// <summary>OKLCH (perceptual lightness, chroma, hue in degrees).</summary>
public readonly record struct Oklch(double L, double C, double H)
{
    public static Oklch FromRgb(Rgb rgb)
    {
        double r = Rgb.Lin(rgb.R), g = Rgb.Lin(rgb.G), b = Rgb.Lin(rgb.B);
        var l = Math.Cbrt(0.4122214708 * r + 0.5363325363 * g + 0.0514459929 * b);
        var m = Math.Cbrt(0.2119034982 * r + 0.6806995451 * g + 0.1073969566 * b);
        var s = Math.Cbrt(0.0883024619 * r + 0.2817188376 * g + 0.6299787005 * b);
        var L = 0.2104542553 * l + 0.7936177850 * m - 0.0040720468 * s;
        var A = 1.9779984951 * l - 2.4285922050 * m + 0.4505937099 * s;
        var B = 0.0259040371 * l + 0.7827717662 * m - 0.8086757660 * s;
        var h = Math.Atan2(B, A) * 180 / Math.PI;
        return new Oklch(L, Math.Sqrt(A * A + B * B), h < 0 ? h + 360 : h);
    }

    /// <summary>Converts to sRGB, reducing chroma until the color fits the gamut.</summary>
    public Rgb ToRgb()
    {
        var c = C;
        for (var i = 0; i < 40; i++)
        {
            if (TryToRgb(L, c, H, out var rgb)) return rgb;
            c *= 0.92;
        }

        TryToRgb(L, 0, H, out var grey);
        return grey;
    }

    private static bool TryToRgb(double L, double C, double H, out Rgb rgb)
    {
        var hr = H * Math.PI / 180;
        var a = C * Math.Cos(hr);
        var b = C * Math.Sin(hr);
        var l = Math.Pow(L + 0.3963377774 * a + 0.2158037573 * b, 3);
        var m = Math.Pow(L - 0.1055613458 * a - 0.0638541728 * b, 3);
        var s = Math.Pow(L - 0.0894841775 * a - 1.2914855480 * b, 3);
        var r = 4.0767416621 * l - 3.3077115913 * m + 0.2309699292 * s;
        var g = -1.2684380046 * l + 2.6097574011 * m - 0.3413193965 * s;
        var bl = -0.0041960863 * l - 0.7034186147 * m + 1.7076147010 * s;
        const double eps = 0.0005;
        var ok = r is >= -eps and <= 1 + eps && g is >= -eps and <= 1 + eps && bl is >= -eps and <= 1 + eps;
        rgb = new Rgb(Math.Clamp(Rgb.Gam(Math.Max(0, r)), 0, 1), Math.Clamp(Rgb.Gam(Math.Max(0, g)), 0, 1), Math.Clamp(Rgb.Gam(Math.Max(0, bl)), 0, 1));
        return ok;
    }

    public Oklch WithL(double l) => this with { L = Math.Clamp(l, 0, 1) };
}
