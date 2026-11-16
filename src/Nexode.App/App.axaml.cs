using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;

namespace Nexode.App;

public partial class App : Application
{
    private static DateTime _lastActivity = DateTime.UtcNow;
    private DispatcherTimer? _lockTimer;
    private DispatcherTimer? _sharedTimer;

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    /// <summary>UI-wide services that depend on the settings (also used by the UI test harness).</summary>
    public static void InitUi(Application app, AppSettings settings)
    {
        Controls.UiState.Instance.ShowHints = settings.ShowHints;
        foreach (var id in settings.DismissedHints) Controls.UiState.Instance.Dismissed.Add(id);
        Motion.Apply(app);
        Window.WindowOpenedEvent.AddClassHandler<Window>((w, _) =>
        {
            UiScaling.Apply(w);
            if (w is not MainWindow) Motion.AnimateWindow(w);
        });
        Nexode.Protocols.Terminal.SerialConnection.Localization.Texts = (key, port) => Loc.F("Serial." + key, port);
    }

    /// <summary>Input in any window resets the auto-lock timer.</summary>
    public static void NoteActivity() => _lastActivity = DateTime.UtcNow;

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            AppReset.RunPending();
            var settings = AppSettings.Load(AppPaths.SettingsFile);
            AppHost.Settings = settings;
            InitUi(this, settings);
            Loc.Apply(settings.Language ?? Loc.DefaultLanguage);
            AppHost.Theme = new ThemeService(settings);
            AppHost.Theme.Apply();
            AppHost.Workspace = new Workspace(settings, () => AppHost.Theme.AccentHue);

            desktop.Exit += (_, _) =>
            {
                Tunnels.CloseAll();
                _ = AppHost.Clipboard.ClearIfOursAsync();
                settings.Save();
                AppHost.Workspace.Dispose();
            };

            var info = DataSources.Active;
            // The sample source is never opened automatically: start with the last real data source.
            if (info is { IsSample: true })
            {
                info = settings.DataSources.FirstOrDefault(d => d.Id == settings.LastRealSource && File.Exists(d.Path))
                       ?? settings.DataSources.FirstOrDefault(d => !d.IsSample && (File.Exists(d.Path) || d.Shared))
                       ?? info;
            }

            var isNew = info is null;
            info ??= DataSources.CreateInfo("Default");

            if (isNew && AppPaths.IsPortable)
            {
                // Portable: the data key must not depend on this PC – ask for a master password first.
                var window = AppHost.Windows.Open(primary: true);
                desktop.MainWindow = window;
                Dispatcher.UIThread.Post(async () =>
                {
                    if (!await DataSources.OpenAsync(window, info, isNew: true))
                    {
                        desktop.Shutdown();
                        return;
                    }

                    AfterOpen(window);
                });
            }
            else
            {
                string? error = null;
                try
                {
                    AppHost.Workspace.Open(info);
                    if (!settings.DataSources.Contains(info)) settings.DataSources.Add(info);
                    settings.ActiveDataSource = info.Id;
                    if (!info.IsSample) settings.LastRealSource = info.Id;
                    settings.Save();
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
                {
                    error = ex.Message;
                }

                var window = AppHost.Windows.Open(primary: true);
                desktop.MainWindow = window;
                if (error is not null)
                {
                    Dispatcher.UIThread.Post(() => _ = Dialog.InfoAsync(window, Loc.T("Source.Open"), Loc.F("Source.OpenFailed", error), "circle-alert"));
                }

                AfterOpen(window);
            }

            if (AppHost.Instance is { } instance)
            {
                instance.ArgumentsReceived += args => Dispatcher.UIThread.Post(() => HandleArguments(args));
                instance.StartListening();
            }

            StartAutoLock();
            StartSharedSync();
        }

        base.OnFrameworkInitializationCompleted();
    }

    private static void AfterOpen(MainWindow window)
    {
        if (AppHost.Workspace.OpenError is not null)
        {
            Dispatcher.UIThread.Post(() => _ = Dialog.InfoAsync(window, Loc.T("Vault.KeyUnavailableTitle"), Loc.T("Vault.KeyUnavailableText"), "triangle-alert"));
        }

        Dispatcher.UIThread.Post(() => HandleArguments(Program.StartArgs), DispatcherPriority.Background);
        Dispatcher.UIThread.Post(() => Startup(window), DispatcherPriority.Background);

        // Fully encrypted data source: ask for the master password right away.
        if (AppHost.Workspace.IsContentHidden)
        {
            Dispatcher.UIThread.Post(() =>
            {
                if (window.DataContext is ViewModels.MainWindowViewModel vm) _ = vm.Actions.UnlockAsync();
            }, DispatcherPriority.Background);
        }
    }

    /// <summary>Start settings: monitor, start page, dashboard window, tray, live status, update check.</summary>
    private static void Startup(MainWindow window)
    {
        var s = AppHost.Settings;
        WebPanels.DeleteTemporaryProfiles();
        if (s.StartMonitor > 0) WindowManager.Place(window, s.StartMonitor, false);
        if (window.DataContext is ViewModels.MainWindowViewModel vm && !AppHost.Workspace.IsContentHidden)
        {
            var ws = AppHost.Workspace;
            var page = s.StartPage ?? "home";
            if (page == "dashboard")
            {
                vm.OpenDashboardCommand.Execute(null);
            }
            else if (page == "restore")
            {
                foreach (var key in s.LastTabs)
                {
                    if (key == "dashboard") vm.OpenDashboardCommand.Execute(null);
                    else if (key.StartsWith("entry:", StringComparison.Ordinal) && Guid.TryParse(key[6..], out var eid) && ws.Tree.Get(eid) is { } e1) vm.Actions.ShowDetails(e1, true);
                    else if (key.StartsWith("session:", StringComparison.Ordinal) && Guid.TryParse(key[8..], out var sid) && ws.Tree.Get(sid) is { } e2) vm.Actions.Open(e2, forceNewTab: true);
                }
            }
            else if (page.StartsWith("entry:", StringComparison.Ordinal) && Guid.TryParse(page[6..], out var id) && ws.Tree.Get(id) is { } entry)
            {
                vm.Actions.Open(entry);
            }
            else if (page.StartsWith("profile:", StringComparison.Ordinal) && ws.Profiles.FirstOrDefault(p => p.Id == page[8..]) is { } profile)
            {
                _ = vm.Actions.OpenProfileAsync(profile);
            }
        }

        if (s.DashboardOnStartup && Features.On(Features.Monitoring)) AppHost.Windows.OpenDashboard();
        if (Current is { } app) TrayService.Apply(app);
        LiveStatus.Start();
        Features.Changed += LiveStatus.Start;
        AppHost.Workspace.SourceChanged += LiveStatus.Reset;
        if (s.UpdateCheck)
        {
            _ = Task.Run(async () =>
            {
                if (await UpdateCheck.RunAsync() is { } release)
                {
                    Dispatcher.UIThread.Post(() => AppHost.Windows.Toast(Loc.F("Update.Available", release.Version)));
                }
            });
        }
    }

    /// <summary>Arguments of this start or of a second start: .nexode files, --new-window.</summary>
    private static void HandleArguments(IEnumerable<string> args)
    {
        var list = args.ToList();
        if (list.Contains("--new-window"))
        {
            AppHost.Windows.Open();
            return;
        }

        var window = AppHost.Windows.Active ?? AppHost.Windows.Windows.FirstOrDefault();
        if (window is null) return;
        if (window.WindowState == WindowState.Minimized) window.WindowState = window.RestoreState;
        window.Activate();

        foreach (var file in list.Where(a => a.EndsWith(".nexode", StringComparison.OrdinalIgnoreCase) && File.Exists(a)))
        {
            if (window.DataContext is ViewModels.MainWindowViewModel vm) _ = vm.Actions.ImportAsync(file);
            break;
        }
    }

    /// <summary>Team data sources: publish changes (editor, every 3 s) or pick up the editor's changes (readers, every 9 s).</summary>
    private void StartSharedSync()
    {
        var ticks = 0;
        _sharedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _sharedTimer.Tick += (_, _) =>
        {
            var ws = AppHost.Workspace;
            if (ws.Shared is null) return;
            ticks++;
            if (ws.Shared.IsEditor || ticks % 3 == 0) ws.SharedTick();
        };
        _sharedTimer.Start();
    }

    /// <summary>Locks the vault after inactivity, after sleep (large gap between timer ticks) and with the Windows screen lock.</summary>
    private void StartAutoLock()
    {
        var lastTick = DateTime.UtcNow;
        _lockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _lockTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var gap = now - lastTick;
            lastTick = now;
            var ws = AppHost.Workspace;
            var s = AppHost.Settings;
            if (!ws.HasPassword || ws.IsLocked) return;

            string? reason = null;
            if (s.LockOnSleep && gap > TimeSpan.FromMinutes(1)) reason = "Toast.LockedSleep";
            else if (s.LockOnSessionLock && Platform.SessionState.IsWorkstationLocked()) reason = "Toast.LockedScreen";
            else if (s.AutoLockMinutes > 0 && now - _lastActivity > TimeSpan.FromMinutes(s.AutoLockMinutes)) reason = "Toast.AutoLocked";
            if (reason is null) return;
            ws.Lock();
            AppHost.Windows.Toast(Loc.T(reason));
        };
        _lockTimer.Start();
    }
}
