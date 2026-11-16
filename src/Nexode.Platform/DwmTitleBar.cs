using System.Runtime.InteropServices;

namespace Nexode.Platform;

/// <summary>Windows 10/11 title bar in the app colors (dark mode flag; caption colors on Windows 11).</summary>
public static partial class DwmTitleBar
{
    private const int UseImmersiveDarkMode = 20;
    private const int CaptionColor = 35;
    private const int TextColor = 36;
    private const int BorderColor = 34;

    /// <param name="caption">0xRRGGBB</param>
    /// <param name="text">0xRRGGBB</param>
    public static void Apply(nint hwnd, int caption, int text, bool dark)
    {
        if (!OperatingSystem.IsWindows() || hwnd == 0) return;
        try
        {
            var flag = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, UseImmersiveDarkMode, ref flag, sizeof(int));
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
            var c = ToColorRef(caption);
            var t = ToColorRef(text);
            DwmSetWindowAttribute(hwnd, CaptionColor, ref c, sizeof(int));
            DwmSetWindowAttribute(hwnd, BorderColor, ref c, sizeof(int));
            DwmSetWindowAttribute(hwnd, TextColor, ref t, sizeof(int));
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            // Older Windows: default title bar.
        }
    }

    private static int ToColorRef(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
