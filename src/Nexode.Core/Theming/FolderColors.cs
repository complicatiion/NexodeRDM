namespace Nexode.Core.Theming;

/// <summary>
/// Folder colors share lightness and chroma per theme; only the hue varies. New folders get the hue (golden angle
/// steps from the accent hue) farthest from their siblings, skipping the danger red and success green.
/// </summary>
public static class FolderColors
{
    public const double GoldenAngle = 137.50776405;
    private const double Excluded = 16;

    private static readonly double DangerHue = Oklch.FromRgb(Rgb.FromHex(Palette.Danger)).H;
    private static readonly double SuccessHue = Oklch.FromRgb(Rgb.FromHex(Palette.Success)).H;

    /// <summary>Swatch hues offered in the color menu.</summary>
    public static IReadOnlyList<double> Swatches { get; } = [255, 285, 310, 335, 55, 75, 95, 120, 185, 210, 230, 0];

    public static Rgb ToRgb(double hue, bool dark) => new Oklch(dark ? 0.74 : 0.56, dark ? 0.12 : 0.14, Normalize(hue)).ToRgb();

    public static double Next(IEnumerable<double> siblingHues, double accentHue)
    {
        var used = siblingHues.Select(Normalize).ToList();
        var best = Normalize(accentHue);
        var bestDistance = -1d;
        for (var i = 0; i < 24; i++)
        {
            var candidate = Normalize(accentHue + i * GoldenAngle);
            if (IsExcluded(candidate)) continue;
            var distance = used.Count == 0 ? 360 : used.Min(u => Distance(u, candidate));
            if (distance > bestDistance + 0.01)
            {
                best = candidate;
                bestDistance = distance;
            }

            if (used.Count == 0) break;
        }

        return Math.Round(best, 1);
    }

    public static bool IsExcluded(double hue) => Distance(hue, DangerHue) < Excluded || Distance(hue, SuccessHue) < Excluded;

    public static double Distance(double a, double b)
    {
        var d = Math.Abs(Normalize(a) - Normalize(b));
        return Math.Min(d, 360 - d);
    }

    public static double Normalize(double hue) => ((hue % 360) + 360) % 360;
}
