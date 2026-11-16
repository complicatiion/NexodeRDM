using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Interactivity;
using Avalonia.Media;
using Nexode.Core.Theming;

namespace Nexode.App.Services;

/// <summary>
/// Mica backdrop for the Mica theme on Windows 11. Window, rail and panels become (semi-)transparent only on windows
/// where the system really applies Mica; everywhere else the solid Mica palette is used.
/// </summary>
public static class Backdrop
{
    private static readonly string[] Keys = ["B.BgWindow", "B.BgRail", "B.BgPanel", "B.BgContent"];
    private static bool _registered;

    /// <summary>Windows 11 (build 22000+) supports the Mica system backdrop.</summary>
    public static bool MicaSupported => OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000);

    public static void ApplyAll(ThemeService theme)
    {
        if (!_registered)
        {
            _registered = true;
            Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) => Apply(w, AppHost.Theme));
        }

        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var w in desktop.Windows) Apply(w, theme);
        }
    }

    public static void Apply(Window window, ThemeService? theme)
    {
        if (theme is null) return;
        var wanted = theme.Variant == PaletteVariant.Mica && MicaSupported;
        if (wanted)
        {
            window.TransparencyLevelHint = [WindowTransparencyLevel.Mica, WindowTransparencyLevel.None];
            if (!window.Classes.Contains("mica-watch"))
            {
                window.Classes.Add("mica-watch");
                window.PropertyChanged += (_, e) =>
                {
                    if (e.Property == TopLevel.ActualTransparencyLevelProperty) Overrides(window, AppHost.Theme);
                };
            }
        }
        else
        {
            window.TransparencyLevelHint = [];
        }

        Overrides(window, theme);
    }

    /// <summary>Local brush overrides on the window, so dialogs and popups elsewhere keep opaque surfaces.</summary>
    private static void Overrides(Window window, ThemeService theme)
    {
        var active = theme.Variant == PaletteVariant.Mica && window.ActualTransparencyLevel == WindowTransparencyLevel.Mica;
        if (!active)
        {
            foreach (var k in Keys) window.Resources.Remove(k);
            window.ClearValue(Window.BackgroundProperty);
            return;
        }

        var p = theme.Palette;
        window.Resources["B.BgWindow"] = Brushes.Transparent;
        window.Resources["B.BgRail"] = Brushes.Transparent;
        window.Resources["B.BgPanel"] = new SolidColorBrush(ThemeService.ToColor(p["BgPanel"], 0x60));
        window.Resources["B.BgContent"] = new SolidColorBrush(ThemeService.ToColor(p["BgContent"], 0xC8));
        window.Background = Brushes.Transparent;
    }
}
