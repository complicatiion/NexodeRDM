using Avalonia.Controls;
using Avalonia.Media;

namespace Nexode.App.Services;

/// <summary>Optional UI scale on top of the system scaling (Settings → Appearance → Display &amp; scaling).</summary>
public static class UiScaling
{
    /// <summary>Presets: (setting value, text key). 0 = automatic (system scaling only).</summary>
    public static readonly (double Value, string Key)[] Presets =
    [
        (0, "Scale.Auto"), (0.8, "Scale.XSmall"), (0.9, "Scale.Small"), (1.0, "Scale.Medium"), (1.15, "Scale.Large"), (1.3, "Scale.XLarge"),
        (1.6, "Scale.Huge")
    ];

    public static double Factor => AppHost.Settings.UiScale is > 0 and var s ? Math.Clamp(s, 0.75, 2.0) : 1.0;

    /// <summary>Wraps the window content once and sets the scale (no wrapper while the factor is 1).</summary>
    public static void Apply(Window window)
    {
        var factor = Factor;
        if (window.Content is LayoutTransformControl wrapper)
        {
            wrapper.LayoutTransform = Math.Abs(factor - 1) < 0.001 ? null : new ScaleTransform(factor, factor);
            return;
        }

        if (Math.Abs(factor - 1) < 0.001 || window.Content is not Control content) return;
        window.Content = null;
        window.Content = new LayoutTransformControl { Child = content, LayoutTransform = new ScaleTransform(factor, factor) };
    }
}
