using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Nexode.App.Services;

namespace Nexode.App.Views;

/// <summary>
/// Window placement: the first window starts maximized (or as it was left), and a window in normal state always fits
/// into the work area of its screen – on compact displays the title bar with minimize, maximize and close never ends
/// up above the top edge of the screen.
/// </summary>
public partial class MainWindow
{
    private const double DefaultMinWidth = 900;
    private const double DefaultMinHeight = 560;
    private WindowState _restoreState = WindowState.Maximized;

    /// <summary>State to go back to after minimized (maximized or normal).</summary>
    public WindowState RestoreState => _restoreState;

    /// <summary>Size and state of the first window from the settings (before it is shown).</summary>
    public void ApplySavedPlacement()
    {
        var b = AppHost.Settings.Window;
        if (b.Width >= 400) Width = b.Width;
        if (b.Height >= 300) Height = b.Height;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        // Maximized unless the user left Nexode in a normal window last time.
        if (!b.Saved || b.Maximized) WindowState = WindowState.Maximized;
    }

    private void InitPlacement()
    {
        Opened += (_, _) => FitToScreen();
        PropertyChanged += (_, e) =>
        {
            if (e.Property != WindowStateProperty) return;
            if (WindowState is WindowState.Normal or WindowState.Maximized) _restoreState = WindowState;
            // Back from maximized: the normal size can be larger than this screen (other monitor, lower resolution).
            if (WindowState == WindowState.Normal) Dispatcher.UIThread.Post(FitToScreen, DispatcherPriority.Background);
        };
    }

    /// <summary>Shrinks and moves a normal window so that it lies completely inside the work area of its screen.</summary>
    public void FitToScreen()
    {
        if (WindowState != WindowState.Normal) return;
        var screen = Screens.ScreenFromWindow(this) ?? Screens.Primary;
        if (screen is null) return;
        var area = screen.WorkingArea;
        var scale = screen.Scaling > 0 ? screen.Scaling : 1;
        // Frame (title bar, borders) on top of the client size.
        var frameW = FrameSize is { } fs && fs.Width > Width ? fs.Width - Width : 16;
        var frameH = FrameSize is { } fh && fh.Height > Height ? fh.Height - Height : 40;
        var maxW = Math.Max(320, area.Width / scale - frameW - 8);
        var maxH = Math.Max(240, area.Height / scale - frameH - 8);
        MinWidth = Math.Min(DefaultMinWidth, maxW);
        MinHeight = Math.Min(DefaultMinHeight, maxH);

        var resized = false;
        if (Width > maxW)
        {
            Width = maxW;
            resized = true;
        }

        if (Height > maxH)
        {
            Height = maxH;
            resized = true;
        }

        var w = (int)Math.Ceiling((Width + frameW) * scale);
        var h = (int)Math.Ceiling((Height + frameH) * scale);
        var pos = Position;
        int x, y;
        if (resized)
        {
            x = area.X + Math.Max(0, (area.Width - w) / 2);
            y = area.Y + Math.Max(0, (area.Height - h) / 2);
        }
        else
        {
            x = Math.Clamp(pos.X, area.X, Math.Max(area.X, area.Right - w));
            y = Math.Clamp(pos.Y, area.Y, Math.Max(area.Y, area.Bottom - h));
        }

        if (x != pos.X || y != pos.Y) Position = new PixelPoint(x, y);
    }

    /// <summary>Remembers size and state for the next start (called when the last window closes).</summary>
    private void RememberPlacement()
    {
        var b = AppHost.Settings.Window;
        var state = WindowState == WindowState.Minimized ? _restoreState : WindowState;
        if (state == WindowState.FullScreen) return;
        b.Saved = true;
        b.Maximized = state == WindowState.Maximized;
        if (state == WindowState.Normal && Width > 0 && Height > 0)
        {
            b.Width = Math.Round(Width);
            b.Height = Math.Round(Height);
        }

        AppHost.Settings.Save();
    }
}
