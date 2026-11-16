using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Platform;
using Nexode.App.Localization;
using Nexode.App.ViewModels;
using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>
/// Icon in the notification area / menu bar: show Nexode, open favorites and profiles, new window, dashboard,
/// lock, quit. Optionally the last window closes to the tray instead of quitting.
/// </summary>
public static class TrayService
{
    /// <summary>One menu line (null text = separator).</summary>
    private sealed record TrayItem(string? Text, string? Icon = null, Action? Click = null, bool Enabled = true, IReadOnlyList<TrayItem>? Children = null);

    private static TrayIcon? _icon;
    private static Nexode.Platform.WinTrayIcon? _winIcon;
    private static Window? _menuHost;

    /// <summary>True while Nexode quits for real (tray "Quit"): windows must not hide into the tray.</summary>
    public static bool IsQuitting { get; set; }

    public static bool IsActive => _icon is not null || _winIcon is not null;

    public static void Apply(Application app)
    {
        if (!AppHost.Settings.TrayIcon)
        {
            Remove(app);
            return;
        }

        if (!IsActive)
        {
            try
            {
                if (OperatingSystem.IsWindows() && CreateWindowsIcon())
                {
                    // Own icon: the right-click menu is an app popup that follows the theme (the native menu cannot).
                }
                else
                {
                    _icon = new TrayIcon
                    {
                        Icon = new WindowIcon(AssetLoader.Open(new Uri("avares://Nexode/Assets/nexode.ico"))),
                        ToolTipText = AppHost.AppName,
                        IsVisible = true
                    };
                    _icon.Clicked += (_, _) => ShowMain();
                    TrayIcon.SetIcons(app, [_icon]);
                }

                AppHost.Workspace.Changed += Rebuild;
                AppHost.Workspace.SourceChanged += Rebuild;
                Loc.LanguageChanged += Rebuild;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("tray: " + ex.Message);
                _icon = null;
                return;
            }
        }

        Rebuild();
    }

    private static bool CreateWindowsIcon()
    {
        try
        {
            using var stream = AssetLoader.Open(new Uri("avares://Nexode/Assets/nexode.ico"));
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            _winIcon = new Nexode.Platform.WinTrayIcon(buffer.ToArray(), AppHost.AppName);
            _winIcon.Clicked += ShowMain;
            _winIcon.MenuRequested += ShowThemedMenu;
            return true;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            AppLog.Write("tray (windows): " + ex.Message);
            _winIcon = null;
            return false;
        }
    }

    private static void Remove(Application app)
    {
        if (!IsActive) return;
        AppHost.Workspace.Changed -= Rebuild;
        AppHost.Workspace.SourceChanged -= Rebuild;
        Loc.LanguageChanged -= Rebuild;
        if (_icon is not null)
        {
            _icon.IsVisible = false;
            _icon.Dispose();
            _icon = null;
            TrayIcon.SetIcons(app, []);
        }

        _winIcon?.Dispose();
        _winIcon = null;
    }

    private static MainWindowViewModel? Main => (AppHost.Windows.Active ?? AppHost.Windows.Windows.FirstOrDefault())?.DataContext as MainWindowViewModel;

    public static void ShowMain()
    {
        var window = AppHost.Windows.Active ?? AppHost.Windows.Windows.FirstOrDefault() ?? AppHost.Windows.Open(primary: true);
        if (!window.IsVisible) window.Show();
        if (window.WindowState == WindowState.Minimized) window.WindowState = window.RestoreState;
        window.Activate();
    }

    /// <summary>The tray menu (same content for the native and the themed menu).</summary>
    private static List<TrayItem> BuildItems()
    {
        var ws = AppHost.Workspace;
        var locked = ws.IsContentHidden;
        var favorites = ws.Tree.All.Where(e => e.IsFavorite && !e.IsContainer).OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance).Take(25)
            .Select(e => new TrayItem(e.Name, ws.Resolver.Resolve(e).Icon, () => Open(e))).ToList();
        var profiles = ws.Profiles.Take(25).Select(p => new TrayItem(p.Name, "layers", () =>
        {
            ShowMain();
            if (Main is { } vm) _ = vm.Actions.OpenProfileAsync(p);
        })).ToList();

        var items = new List<TrayItem>
        {
            new(Loc.T("Tray.Show"), "app-window", ShowMain),
            new(null),
            new(Loc.T("Rail.Favorites"), "star", null, !locked && favorites.Count > 0, favorites),
            new(Loc.T("Rail.Profiles"), "layers", null, !locked && profiles.Count > 0, profiles),
            new(null),
            new(Loc.T("Menu.NewWindow"), "app-window", () => AppHost.Windows.Open()),
            new(Loc.T("Tab.Dashboard"), "layout-dashboard", () => AppHost.Windows.OpenDashboard())
        };
        if (ws.HasPassword) items.Add(new(Loc.T("Vault.Lock"), "lock", ws.Lock, !ws.IsLocked));
        items.Add(new(null));
        items.Add(new(Loc.T("Tray.Quit"), "power", Quit));
        return items;
    }

    private static void Rebuild()
    {
        if (_icon is null) return;
        _icon.Menu = ToNative(BuildItems());
    }

    private static NativeMenu ToNative(IEnumerable<TrayItem> items)
    {
        var menu = new NativeMenu();
        foreach (var i in items)
        {
            if (i.Text is null)
            {
                menu.Add(new NativeMenuItemSeparator());
                continue;
            }

            var item = new NativeMenuItem(i.Text) { IsEnabled = i.Enabled };
            if (i.Children is not null) item.Menu = ToNative(i.Children);
            if (i.Click is { } click) item.Click += (_, _) => click();
            menu.Add(item);
        }

        return menu;
    }

    private static IEnumerable<Control> ToControls(IEnumerable<TrayItem> items)
    {
        foreach (var i in items)
        {
            if (i.Text is null)
            {
                yield return new Separator();
                continue;
            }

            var item = new MenuItem
            {
                Header = i.Text,
                IsEnabled = i.Enabled,
                Icon = i.Icon is null ? null : new Nexode.App.Controls.LineIcon { Icon = i.Icon, Size = 15 }
            };
            if (i.Children is not null)
            {
                foreach (var c in ToControls(i.Children)) item.Items.Add(c);
            }

            if (i.Click is { } click) item.Click += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(click);
            yield return item;
        }
    }

    /// <summary>Windows: the tray menu as an app popup (theme colors, accent selection) at the cursor.</summary>
    private static void ShowThemedMenu(int x, int y)
    {
        _menuHost?.Close();
        var host = new Window
        {
            WindowDecorations = WindowDecorations.None,
            ShowInTaskbar = false,
            Topmost = true,
            CanResize = false,
            Width = 1,
            Height = 1,
            Background = Avalonia.Media.Brushes.Transparent,
            TransparencyLevelHint = [WindowTransparencyLevel.Transparent],
            WindowStartupLocation = WindowStartupLocation.Manual,
            Position = new PixelPoint(x, y)
        };
        _menuHost = host;
        var menu = new ContextMenu { Placement = PlacementMode.TopEdgeAlignedLeft };
        foreach (var c in ToControls(BuildItems())) menu.Items.Add(c);
        menu.Closed += (_, _) =>
        {
            if (_menuHost == host) _menuHost = null;
            host.Close();
        };
        host.Opened += (_, _) =>
        {
            _winIcon?.Foreground();
            host.Activate();
            menu.Open(host);
        };
        host.Show();
    }

    private static void Open(Entry entry)
    {
        ShowMain();
        Main?.Actions.Open(entry);
    }

    public static void Quit()
    {
        IsQuitting = true;
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            foreach (var w in AppHost.Windows.Windows.ToList()) w.Close();
            desktop.Shutdown();
        }
    }
}
