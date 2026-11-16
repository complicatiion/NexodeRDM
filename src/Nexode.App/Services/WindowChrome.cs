using Avalonia.Controls;
using Nexode.Platform;

namespace Nexode.App.Services;

/// <summary>Native title bar in the theme colors (Windows).</summary>
public static class WindowChrome
{
    public static void Apply(Window window)
    {
        Color(window);
        void OnTheme() => Color(window);
        AppHost.Theme.Changed += OnTheme;
        window.Closed += (_, _) => AppHost.Theme.Changed -= OnTheme;
    }

    private static void Color(Window window)
    {
        if (!OperatingSystem.IsWindows() || window.TryGetPlatformHandle()?.Handle is not { } hwnd) return;
        var p = AppHost.Theme.Palette;
        var caption = p["BgWindow"];
        var text = p["TextPrimary"];
        DwmTitleBar.Apply(hwnd, (caption.R8 << 16) | (caption.G8 << 8) | caption.B8, (text.R8 << 16) | (text.G8 << 8) | text.B8, p.IsDark);
    }
}
