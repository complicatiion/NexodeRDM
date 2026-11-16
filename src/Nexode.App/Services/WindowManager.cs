using Avalonia.Controls;
using Nexode.App.ViewModels;
using Nexode.App.Views;

namespace Nexode.App.Services;

/// <summary>All main windows of the process; they share one workspace.</summary>
public sealed class WindowManager
{
    private readonly List<MainWindow> _windows = [];

    public IReadOnlyList<MainWindow> Windows => _windows;
    public MainWindow? Active { get; private set; }

    public MainWindow Open(TabViewModel? initialTab = null, bool primary = false)
    {
        var vm = new MainWindowViewModel(primary);
        var window = new MainWindow { DataContext = vm };
        vm.Attach(window);
        if (initialTab is not null) vm.Tabs.Add(initialTab, select: true);
        else vm.Tabs.OpenStart();

        _windows.Add(window);
        window.Activated += (_, _) => Active = window;
        window.Closed += (_, _) =>
        {
            _windows.Remove(window);
            if (Active == window) Active = _windows.LastOrDefault();
            vm.Tabs.CloseAll();
            vm.Detach();
        };

        if (primary) window.ApplySavedPlacement();
        else if (Active is { } previous)
        {
            window.Width = Math.Max(900, previous.Bounds.Width * 0.8);
            window.Height = Math.Max(600, previous.Bounds.Height * 0.8);
            window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        window.Show();
        Active = window;
        return window;
    }

    /// <summary>
    /// Puts a window on monitor <paramref name="monitor"/> (1-based, 0 = keep) and optionally into fullscreen.
    /// Screens are taken from an open window (a new one does not know them before it is shown).
    /// </summary>
    public static void Place(Window window, int monitor, bool fullscreen, Window? reference = null)
    {
        var screens = (reference ?? window).Screens;
        var wasMaximized = window.WindowState == WindowState.Maximized;
        if (monitor > 0 && screens.All.Count >= monitor)
        {
            var screen = screens.All.OrderBy(s => s.Bounds.X).ThenBy(s => s.Bounds.Y).ElementAt(monitor - 1);
            var area = screen.WorkingArea;
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            if (window.WindowState != WindowState.Normal) window.WindowState = WindowState.Normal;
            var scale = screen.Scaling;
            var w = Math.Min(window.Width is > 0 and not double.NaN ? window.Width : 1200, area.Width / scale - 40);
            var h = Math.Min(window.Height is > 0 and not double.NaN ? window.Height : 800, area.Height / scale - 40);
            window.Width = w;
            window.Height = h;
            window.Position = new Avalonia.PixelPoint(area.X + (int)((area.Width - w * scale) / 2), area.Y + (int)((area.Height - h * scale) / 2));
        }

        if (fullscreen) window.WindowState = WindowState.FullScreen;
        // Maximized on the chosen monitor (first window): moved in normal state, maximized again there.
        else if (wasMaximized && window.WindowState == WindowState.Normal) window.WindowState = WindowState.Maximized;
    }

    /// <summary>Number of monitors (1 when unknown).</summary>
    public int MonitorCount => Active?.Screens.All.Count ?? Windows.FirstOrDefault()?.Screens.All.Count ?? 1;

    /// <summary>Dashboard in its own window on the configured monitor (kiosk = fullscreen without tabs, rail and tree).</summary>
    public MainWindow OpenDashboard()
    {
        if (ViewModels.FirstOrDefault(v => v.IsKiosk && v.Tabs.Items.Count == 1 && v.Tabs.Items[0] is DashboardTabViewModel) is { Window: { } existing })
        {
            existing.Activate();
            return existing;
        }

        var reference = Active;
        var window = Open(new DashboardTabViewModel());
        var s = AppHost.Settings;
        Place(window, s.DashboardMonitor, false, reference);
        if (window.DataContext is MainWindowViewModel vm && s.DashboardFullscreen) vm.IsKiosk = true;
        return window;
    }

    /// <summary>Moves a session into its own fullscreen window on a monitor (RDP fullscreen).</summary>
    public MainWindow OpenFullscreen(TabViewModel tab, int monitor)
    {
        var reference = Active;
        var window = Open(tab);
        Place(window, monitor, false, reference);
        if (window.DataContext is MainWindowViewModel vm) vm.IsKiosk = true;
        return window;
    }

    public IEnumerable<MainWindowViewModel> ViewModels => _windows.Select(w => w.DataContext).OfType<MainWindowViewModel>();

    public void RefreshStartPages()
    {
        foreach (var vm in ViewModels)
        {
            foreach (var t in vm.Tabs.Items.OfType<StartTabViewModel>()) t.NewGreeting();
        }
    }

    public void RefreshSourceNames()
    {
        foreach (var vm in ViewModels) vm.RefreshSource();
    }

    public void Toast(string message, bool error = false)
    {
        if (Active?.DataContext is MainWindowViewModel vm) vm.ShowToast(message, error);
    }
}
