using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Core.Model;
using Nexode.Platform;

namespace Nexode.App.ViewModels;

/// <summary>Section of the home page or card of the dashboard: shown or hidden, movable.</summary>
public sealed partial class LayoutItem(string id, string titleKey, string icon) : ObservableObject
{
    public string Id { get; } = id;
    public string Icon { get; } = icon;
    public string Title => Loc.T(titleKey);

    [ObservableProperty]
    private bool _isShown;

    public Action? Changed { get; set; }
    partial void OnIsShownChanged(bool value) => Changed?.Invoke();
    public void Relocalize() => OnPropertyChanged(nameof(Title));
}

/// <summary>Keyboard shortcut row: action left, keys as pills right.</summary>
public sealed record ShortcutItem(string Action, IReadOnlyList<string> Keys)
{
    /// <summary>The whole combination in one pill ("Ctrl + Shift + T").</summary>
    public string Combo => string.Join(" + ", Keys);
}

/// <summary>v0.0.4 settings: start, home page, dashboard, live status, web panels, tray, updates, history, sample cleanup.</summary>
public sealed partial class SettingsTabViewModel
{
    public static int PageOf(string? page) => page switch
    {
        "behavior" => 1,
        "startup" => 2,
        "home" => 3,
        "features" => 4,
        "dashboard" => 5,
        "security" => 6,
        "data" => 7,
        "categories" => 8,
        "environments" => 9,
        "profiles" => 10,
        "integration" => 11,
        "diagnostics" => 12,
        "about" => 13,
        _ => 0
    };

    /// <summary>About page (last settings page).</summary>
    public AboutTabViewModel About { get; } = new();

    public ObservableCollection<LayoutItem> HomeItems { get; } = [];
    public ObservableCollection<LayoutItem> DashboardItems { get; } = [];
    public ObservableCollection<OptionItem> StartPages { get; } = [];
    public ObservableCollection<OptionItem> Monitors { get; } = [];
    public ObservableCollection<OptionItem> DashboardMonitors { get; } = [];
    private readonly List<string> _startPageKeys = [];

    [ObservableProperty] private bool _treeAnimation;
    [ObservableProperty] private bool _railColors;
    [ObservableProperty] private bool _openWebExternal;
    [ObservableProperty] private bool _autostart;
    [ObservableProperty] private int _startPageIndex;
    [ObservableProperty] private int _startMonitorIndex;
    [ObservableProperty] private bool _trayIcon;
    [ObservableProperty] private bool _closeToTray;
    [ObservableProperty] private bool _updateCheck;
    [ObservableProperty] private string _updateText = "";
    [ObservableProperty] private decimal _dashboardListCount;
    [ObservableProperty] private int _dashboardMonitorIndex;
    [ObservableProperty] private bool _dashboardFullscreen;
    [ObservableProperty] private bool _dashboardOnStartup;
    [ObservableProperty] private bool _liveStatusOn;
    [ObservableProperty] private decimal _liveStatusSeconds;
    [ObservableProperty] private bool _connectionHistory;

    public bool CanAutostart => Platform.Autostart.IsSupported && Environment.ProcessPath is not null && !AppPaths.IsPortable;
    public string WebEngineText => WebPanels.UnavailableReason is { } reason
        ? Loc.F("Web.EngineMissing", reason)
        : Loc.F("Web.EngineOk", WebPanels.Info?.Type.ToString() ?? "", WebPanels.Info?.Version ?? "");
    public bool WebEngineOk => WebPanels.UnavailableReason is null;
    public bool HasUpdateText => UpdateText.Length > 0;
    public bool UpdateAvailable => UpdateCheck_Available is not null;
    private static Core.Updates.ReleaseInfo? UpdateCheck_Available => Services.UpdateCheck.Available;
    public bool CanCleanupSample => !IsSampleActive;

    public IReadOnlyList<ShortcutItem> Shortcuts { get; private set; } = [];

    partial void OnUpdateTextChanged(string value) => OnPropertyChanged(nameof(HasUpdateText));

    private void LoadV4()
    {
        TreeAnimation = S.TreeAnimation;
        RailColors = S.RailColors;
        OpenWebExternal = S.OpenWebExternal;
        Autostart = CanAutostart && Platform.Autostart.IsEnabled();
        TrayIcon = S.TrayIcon;
        CloseToTray = S.CloseToTray;
        UpdateCheck = S.UpdateCheck;
        DashboardListCount = S.DashboardListCount;
        DashboardFullscreen = S.DashboardFullscreen;
        DashboardOnStartup = S.DashboardOnStartup;
        LiveStatusOn = S.LiveStatus;
        LiveStatusSeconds = S.LiveStatusSeconds;
        ConnectionHistory = S.ConnectionHistory;
        BuildMonitors();
        StartMonitorIndex = Math.Clamp(S.StartMonitor, 0, Monitors.Count - 1);
        DashboardMonitorIndex = Math.Clamp(S.DashboardMonitor, 0, DashboardMonitors.Count - 1);
        BuildStartPages();
        BuildLayoutItems();
        BuildShortcuts();
        if (Services.UpdateCheck.Available is { } r) UpdateText = Loc.F("Update.Available", r.Version);
    }

    private void BuildMonitors()
    {
        var count = Math.Max(1, AppHost.Windows.MonitorCount);
        Monitors.Clear();
        Monitors.Add(new OptionItem("Start.MonitorLast", "history"));
        DashboardMonitors.Clear();
        DashboardMonitors.Add(new OptionItem("Dash.MonitorSame", "app-window"));
        for (var i = 1; i <= count; i++)
        {
            Monitors.Add(new OptionItem("=" + Loc.F("Rdp.MonitorN", i), "monitor"));
            DashboardMonitors.Add(new OptionItem("=" + Loc.F("Rdp.MonitorN", i), "monitor"));
        }
    }

    /// <summary>Home, dashboard, restore last tabs, then favorites and profiles as fixed start targets.</summary>
    private void BuildStartPages()
    {
        StartPages.Clear();
        _startPageKeys.Clear();
        void Add(string key, OptionItem item)
        {
            _startPageKeys.Add(key);
            StartPages.Add(item);
        }

        Add("home", new OptionItem("Start.PageHome", "house"));
        Add("dashboard", new OptionItem("Start.PageDashboard", "layout-dashboard"));
        Add("restore", new OptionItem("Start.PageRestore", "history"));
        foreach (var p in Ws.Profiles) Add("profile:" + p.Id, new OptionItem("=" + Loc.F("Start.PageProfile", p.Name), "layers"));
        foreach (var e in Ws.Tree.All.Where(e => e.IsFavorite && EntryKinds.IsConnection(e.Kind)).OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance))
        {
            Add("entry:" + e.Id, new OptionItem("=" + Loc.F("Start.PageEntry", e.Name), EntryKinds.Icon(e.Kind)));
        }

        // A configured entry that is no longer a favorite stays selectable.
        if (!_startPageKeys.Contains(S.StartPage) && S.StartPage.StartsWith("entry:", StringComparison.Ordinal) && Guid.TryParse(S.StartPage[6..], out var id) && Ws.Tree.Get(id) is { } other)
        {
            Add(S.StartPage, new OptionItem("=" + Loc.F("Start.PageEntry", other.Name), EntryKinds.Icon(other.Kind)));
        }

        StartPageIndex = Math.Max(0, _startPageKeys.IndexOf(S.StartPage));
    }

    private void BuildLayoutItems()
    {
        HomeItems.Clear();
        foreach (var id in S.HomeOrder())
        {
            var item = new LayoutItem(id, "Home.Section." + id, id switch
            {
                "search" => "search",
                "profiles" => "layers",
                "favorites" => "heart",
                "recent" => "history",
                "new" => "plus",
                "status" => "activity",
                _ => "layout-dashboard"
            }) { IsShown = !S.HomeHidden.Contains(id) };
            item.Changed = SaveHome;
            HomeItems.Add(item);
        }

        DashboardItems.Clear();
        foreach (var id in DashboardCards.All)
        {
            var item = new LayoutItem(id, "Dash.Card." + id, DashboardCards.Icon(id)) { IsShown = !S.DashboardHidden.Contains(id) };
            item.Changed = SaveDashboard;
            DashboardItems.Add(item);
        }
    }

    private void BuildShortcuts()
    {
        ShortcutItem K(string key, params string[] keys) => new(Loc.T("Key." + key), keys);
        Shortcuts =
        [
            K("NewTab", "Ctrl", "T"), K("CloseTab", "Ctrl", "W"), K("Reopen", "Ctrl", "Shift", "T"), K("NextTab", "Ctrl", "Tab"), K("GoTab", "Ctrl", "1…9"),
            K("NewWindow", "Ctrl", "Shift", "N"), K("Tree", "Ctrl", "B"), K("Search", "Ctrl", "F"), K("Palette", "Ctrl", "K"), K("Start", "Ctrl", "Shift", "K"), K("NewEntry", "Ctrl", "N"),
            K("Open", "Enter"), K("Details", "Space"), K("Rename", "F2"), K("Delete", "Del"), K("CopyCut", "Ctrl", "C / X / V"), K("Duplicate", "Ctrl", "D"),
            K("Edit", "Ctrl", "E"), K("Undo", "Ctrl", "Z"), K("Lock", "Ctrl", "L"), K("Import", "Ctrl", "O"), K("Settings", "Ctrl", ","), K("Fullscreen", "F11")
        ];
        // All pills share the width of the longest combination (monospace, ~7 px per character).
        ShortcutPillWidth = Shortcuts.Max(k => k.Combo.Length) * 7.2 + 22;
        OnPropertyChanged(nameof(Shortcuts));
        OnPropertyChanged(nameof(ShortcutPillWidth));
    }

    public double ShortcutPillWidth { get; private set; } = 140;

    private void RelocalizeV4()
    {
        foreach (var i in HomeItems.Concat(DashboardItems)) i.Relocalize();
        foreach (var o in Monitors.Concat(DashboardMonitors)) o.Relocalize();
        var page = StartPageIndex;
        _loading = true;
        BuildStartPages();
        BuildMonitors();
        StartMonitorIndex = Math.Clamp(S.StartMonitor, 0, Monitors.Count - 1);
        DashboardMonitorIndex = Math.Clamp(S.DashboardMonitor, 0, DashboardMonitors.Count - 1);
        StartPageIndex = page;
        _loading = false;
        BuildShortcuts();
        OnPropertyChanged(nameof(WebEngineText));
    }

    private static void RefreshHomePages()
    {
        foreach (var vm in AppHost.Windows.ViewModels)
        {
            foreach (var t in vm.Tabs.Items.Where(t => t is StartTabViewModel or DashboardTabViewModel)) t.Refresh();
        }
    }

    // ------------------------------------------------------------------ appearance / behavior

    partial void OnTreeAnimationChanged(bool value)
    {
        if (_loading) return;
        S.TreeAnimation = value;
        Save();
        RefreshWindows(motion: true);
    }

    partial void OnRailColorsChanged(bool value)
    {
        if (_loading) return;
        S.RailColors = value;
        Save();
        foreach (var vm in AppHost.Windows.ViewModels) vm.RefreshPins();
    }

    partial void OnOpenWebExternalChanged(bool value)
    {
        if (_loading) return;
        S.OpenWebExternal = value;
        Save();
    }

    [RelayCommand]
    private void ClearWebData()
    {
        var ok = WebPanels.ClearData();
        Owner?.ShowToast(ok ? Loc.T("Web.DataCleared") : Loc.T("Web.DataInUse"), !ok);
    }

    // ------------------------------------------------------------------ start

    partial void OnAutostartChanged(bool value)
    {
        if (_loading || Environment.ProcessPath is not { } exe) return;
        if (!Platform.Autostart.Set(value, exe))
        {
            Owner?.ShowToast(Loc.T("Start.AutostartFailed"), true);
            _loading = true;
            Autostart = Platform.Autostart.IsEnabled();
            _loading = false;
        }
    }

    partial void OnStartPageIndexChanged(int value)
    {
        if (_loading || value < 0 || value >= _startPageKeys.Count) return;
        S.StartPage = _startPageKeys[value];
        Save();
    }

    partial void OnStartMonitorIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.StartMonitor = value;
        Save();
    }

    partial void OnTrayIconChanged(bool value)
    {
        if (_loading) return;
        S.TrayIcon = value;
        Save();
        if (Avalonia.Application.Current is { } app) TrayService.Apply(app);
    }

    partial void OnCloseToTrayChanged(bool value)
    {
        if (_loading) return;
        S.CloseToTray = value;
        Save();
    }

    partial void OnUpdateCheckChanged(bool value)
    {
        if (_loading) return;
        S.UpdateCheck = value;
        Save();
    }

    [RelayCommand]
    private async Task CheckUpdatesNow()
    {
        UpdateText = Loc.T("Update.Checking");
        try
        {
            var release = await Task.Run(() => Services.UpdateCheck.RunAsync(force: true));
            UpdateText = release is null ? Loc.F("Update.Current", AppHost.Version) : Loc.F("Update.Available", release.Version);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or TaskCanceledException or IOException)
        {
            UpdateText = Loc.T("Update.Failed");
        }

        OnPropertyChanged(nameof(UpdateAvailable));
    }

    [RelayCommand]
    private void OpenRelease()
    {
        if (Services.UpdateCheck.Available is { Url.Length: > 0 } r) Shell.OpenWebUrl(r.Url);
    }

    // ------------------------------------------------------------------ home page

    private void SaveHome()
    {
        if (_loading) return;
        S.HomeSections = HomeItems.Select(i => i.Id).ToList();
        S.HomeHidden = HomeItems.Where(i => !i.IsShown).Select(i => i.Id).ToList();
        Save();
        RefreshHomePages();
    }

    [RelayCommand]
    private void HomeUp(LayoutItem item) => Move(HomeItems, item, -1, SaveHome);

    [RelayCommand]
    private void HomeDown(LayoutItem item) => Move(HomeItems, item, 1, SaveHome);

    [RelayCommand]
    private void HomeReset()
    {
        S.HomeSections = [.. HomeLayout.DefaultOrder];
        S.HomeHidden = [.. HomeLayout.DefaultHidden];
        Save();
        _loading = true;
        BuildLayoutItems();
        _loading = false;
        RefreshHomePages();
    }

    private static void Move(ObservableCollection<LayoutItem> list, LayoutItem item, int delta, Action save)
    {
        var i = list.IndexOf(item);
        var j = i + delta;
        if (i < 0 || j < 0 || j >= list.Count) return;
        list.Move(i, j);
        save();
    }

    // ------------------------------------------------------------------ dashboard and live status

    private void SaveDashboard()
    {
        if (_loading) return;
        S.DashboardHidden = DashboardItems.Where(i => !i.IsShown).Select(i => i.Id).ToList();
        Save();
        RefreshHomePages();
    }

    partial void OnDashboardListCountChanged(decimal value)
    {
        if (_loading) return;
        S.DashboardListCount = (int)value;
        Save();
        RefreshHomePages();
    }

    partial void OnDashboardMonitorIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.DashboardMonitor = value;
        Save();
    }

    partial void OnDashboardFullscreenChanged(bool value)
    {
        if (_loading) return;
        S.DashboardFullscreen = value;
        Save();
    }

    partial void OnDashboardOnStartupChanged(bool value)
    {
        if (_loading) return;
        S.DashboardOnStartup = value;
        Save();
    }

    partial void OnLiveStatusOnChanged(bool value)
    {
        if (_loading) return;
        S.LiveStatus = value;
        Save();
        LiveStatus.Start();
        RefreshHomePages();
    }

    partial void OnLiveStatusSecondsChanged(decimal value)
    {
        if (_loading) return;
        S.LiveStatusSeconds = (int)value;
        Save();
        LiveStatus.Start();
    }

    [RelayCommand]
    private void OpenDashboardWindow() => AppHost.Windows.OpenDashboard();

    // ------------------------------------------------------------------ data: history, sample cleanup

    partial void OnConnectionHistoryChanged(bool value)
    {
        if (_loading) return;
        S.ConnectionHistory = value;
        Save();
    }

    [RelayCommand]
    private void ShowHistory() => Owner?.Tabs.OpenOrFocus("history", () => new HistoryTabViewModel());

    [RelayCommand]
    private async Task ClearHistory()
    {
        if (Owner?.Window is not { } w) return;
        if (await Dialog.ConfirmAsync(w, Loc.T("History.Clear"), Loc.T("History.ClearText"), Loc.T("History.Clear"), danger: true)) Services.ConnectionHistory.Clear();
    }

    /// <summary>Resets Nexode RDM to a fresh install after two confirmations (Yes/No each).</summary>
    [RelayCommand]
    private async Task ResetApp()
    {
        if (Owner?.Window is not { } w) return;
        var keep = new Avalonia.Controls.CheckBox { Content = Loc.T("Reset.KeepBackups"), IsChecked = false };
        var body = new Avalonia.Controls.StackPanel { Spacing = 12 };
        body.Children.Add(DialogWindow.Text(Loc.T("Reset.Text")));
        body.Children.Add(new Nexode.App.Controls.Hint { Text = Loc.T("Reset.External") });
        body.Children.Add(keep);
        var first = new DialogWindow(Loc.T("Reset.Title"), "triangle-alert", 520) { Body = body };
        first.AddButton(Loc.T("No"), "x", false, false, null, false);
        first.AddButton(Loc.T("Yes"), "rotate-ccw", true, true, null, true);
        if (await first.ShowDialog<object?>(w) is not true) return;

        var second = new DialogWindow(Loc.T("Reset.Title"), "triangle-alert") { Body = DialogWindow.Text(Loc.T("Reset.Confirm")) };
        second.AddButton(Loc.T("No"), "x", false, false, null, false);
        second.AddButton(Loc.T("Reset.Button"), "rotate-ccw", true, true, null, true);
        if (await second.ShowDialog<object?>(w) is not true) return;
        AppReset.Request(keep.IsChecked == true);
    }

    /// <summary>Finds sample entries in the active data source, shows them and moves them to the trash after confirmation.</summary>
    [RelayCommand]
    private async Task CleanupSample()
    {
        if (Owner?.Window is not { } w) return;
        var found = SampleCleanup.Find();
        if (found.Count == 0)
        {
            await Dialog.InfoAsync(w, Loc.T("Sample.Cleanup"), Loc.T("Sample.CleanupNone"), "circle-check");
            return;
        }

        var list = string.Join(Environment.NewLine, found.Take(40).Select(e => "• " + Ws.Tree.Path(e) + (e.IsContainer ? "" : " (" + Loc.Kind(e.Kind) + ")")));
        if (found.Count > 40) list += Environment.NewLine + Loc.F("Sample.CleanupMore", found.Count - 40);
        if (!await Dialog.ConfirmAsync(w, Loc.T("Sample.Cleanup"), Loc.F("Sample.CleanupText", found.Count) + Environment.NewLine + Environment.NewLine + list,
                Loc.T("Sample.CleanupDo"), danger: true, icon: "eraser")) return;
        var n = SampleCleanup.Remove(found);
        Owner.ShowToast(Loc.F("Sample.CleanupDone", n));
    }
}
