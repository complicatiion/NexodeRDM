using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Core.Model;

namespace Nexode.App.ViewModels;

public sealed partial class RailItem(string key, string icon, string textKey, TreeMode? mode) : ObservableObject
{
    public string Key { get; } = key;
    public string Icon { get; } = icon;
    public string TextKey { get; } = textKey;
    public TreeMode? Mode { get; } = mode;
    public string Title => Loc.T(TextKey);

    public void Relocalize() => OnPropertyChanged(nameof(Title));
}

/// <summary>Entry pinned to the rail.</summary>
public sealed class PinItem(Entry entry)
{
    public Entry Entry { get; } = entry;
    public Guid Id => Entry.Id;
    public string Title => string.IsNullOrEmpty(Entry.Name) ? Loc.T("Unnamed") : Entry.Name;
    public string Icon => AppHost.Workspace.Resolver.Resolve(Entry).Icon;
    /// <summary>Personal pin color, else the entry's own or inherited color; none when rail colors are off.</summary>
    public Avalonia.Media.IBrush? IconBrush =>
        !AppHost.Settings.RailColors ? null
        : (Entry.PinHue ?? AppHost.Workspace.Resolver.Resolve(Entry).ColorHue) is { } h ? AppHost.Theme.FolderBrush(h) : null;
    public string Tooltip => Entry.Kind == EntryKind.Folder ? Title : $"{Title} · {Loc.Kind(Entry.Kind)}";
}

/// <summary>One main window: rail, tree panel, tabs, status bar.</summary>
public sealed partial class MainWindowViewModel : ObservableObject
{
    private readonly DispatcherTimer _toastTimer = new() { Interval = TimeSpan.FromSeconds(5) };

    public MainWindowViewModel(bool primary)
    {
        IsPrimary = primary;
        Tree = new TreeViewModel();
        Tabs = new TabsViewModel(this);
        Actions = new EntryActions(this);
        RailItems =
        [
            new RailItem("connections", "folder-tree", "Rail.Connections", TreeMode.All),
            new RailItem("favorites", "heart", "Rail.Favorites", TreeMode.Favorites),
            new RailItem("profiles", "layers", "Rail.Profiles", TreeMode.Profiles),
            new RailItem("recent", "history", "Rail.Recent", TreeMode.Recent)
        ];
        _selectedRail = RailItems[0];
        _railExpanded = AppHost.Settings.RailExpanded;
        _treeVisible = AppHost.Settings.TreeVisible;
        Tree.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(TreeViewModel.Mode))
            {
                OnPropertyChanged(nameof(IsProfilesMode));
                OnPropertyChanged(nameof(IsRecentMode));
                OnPropertyChanged(nameof(ShowEmptyTree));
                OnPropertyChanged(nameof(ShowNoResults));
            }
            else if (e.PropertyName is nameof(TreeViewModel.IsEmpty) or nameof(TreeViewModel.NoResults))
            {
                OnPropertyChanged(nameof(ShowEmptyTree));
                OnPropertyChanged(nameof(ShowNoResults));
            }
        };
        _toastTimer.Tick += (_, _) =>
        {
            _toastTimer.Stop();
            HasToast = false;
        };
    }

    public bool IsPrimary { get; }
    public MainWindow? Window { get; private set; }
    public TreeViewModel Tree { get; }
    public TabsViewModel Tabs { get; }
    public EntryActions Actions { get; }
    public IReadOnlyList<RailItem> RailItems { get; }
    public System.Collections.ObjectModel.ObservableCollection<PinItem> Pins { get; } = [];
    public System.Collections.ObjectModel.ObservableCollection<ProfileItem> ProfileItems { get; } = [];
    public bool HasProfiles => ProfileItems.Count > 0;
    public bool IsProfilesMode => Tree.Mode == TreeMode.Profiles;
    public bool IsRecentMode => Tree.Mode == TreeMode.Recent;
    public bool RecentMostUsed => AppHost.Settings.RecentMostUsed;
    public bool ShowEmptyTree => Tree.IsEmpty && !IsProfilesMode && !IsContentHidden;
    public bool ShowNoResults => Tree.NoResults && !IsProfilesMode;
    public bool ShowStatusBar => !IsKiosk && AppHost.Settings.ShowStatusBar;

    // Shared (team) data source
    public bool IsShared => AppHost.Workspace.Shared is not null;
    public bool IsReadOnly => AppHost.Workspace.IsReadOnly;
    public string SharedIcon => IsReadOnly ? "lock-keyhole" : "users-round";
    public string SharedText
    {
        get
        {
            if (AppHost.Workspace.Shared is not { } sh) return "";
            if (sh.IsEditor) return Loc.T("Shared.Editing");
            var l = sh.ReadLock();
            return l is null ? Loc.T("Shared.ReadOnly")
                : Loc.F("Shared.LockedBy", l.User, l.Machine, l.SinceUtc.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture));
        }
    }

    public string TakeOverTip => AppHost.Workspace.Shared?.ReadLock() is { } l && !l.IsStale(DateTime.UtcNow) ? Loc.T("Shared.TakeOverTip") : Loc.T("Shared.EditTip");
    public bool HasPins => Pins.Count > 0;
    public string AppVersion => AppHost.Version;

    [ObservableProperty]
    private RailItem? _selectedRail;

    [ObservableProperty]
    private bool _railExpanded;

    [ObservableProperty]
    private bool _treeVisible;

    [ObservableProperty]
    private string _toast = "";

    [ObservableProperty]
    private bool _toastIsError;

    [ObservableProperty]
    private bool _hasToast;

    [ObservableProperty]
    private int _clipboardSeconds;

    public double RailWidth => RailExpanded ? 196 : 56;
    public string SourceName => AppHost.Workspace.Info?.Name ?? "";
    public string SourceIcon => AppHost.Workspace.Info is { Icon.Length: > 0 } i ? i.Icon : "database";
    public Avalonia.Media.IBrush? SourceBrush => AppHost.Workspace.Info?.ColorHue is { } h ? AppHost.Theme.FolderBrush(h) : null;
    public bool IsContentHidden => AppHost.Workspace.IsContentHidden;
    public int SessionCount => Tabs.Items.Count(t => t.IsSession);
    public bool HasSessions => SessionCount > 0;
    public string SessionText => SessionCount == 1 ? Loc.T("Status.SessionsOne") : Loc.F("Status.Sessions", SessionCount);
    public string EntryCountText => Loc.F("Status.Entries", AppHost.Workspace.Tree.Count);
    public bool IsLocked => AppHost.Workspace.IsLocked;
    public bool HasPassword => AppHost.Workspace.HasPassword;
    public bool VaultWeak => AppHost.Workspace.Vault?.IsWeak == true;
    public string VaultText => AppHost.Workspace.OpenError is not null ? Loc.T("Vault.KeyUnavailable")
        : IsLocked ? Loc.T("Vault.Locked") : HasPassword ? Loc.T("Vault.Unlocked") : Loc.T("Vault.System");
    public string VaultIcon => IsLocked ? "lock" : HasPassword ? "lock-open" : "shield-check";
    public string VaultTip => VaultWeak ? Loc.T("Vault.WeakTip") : IsLocked ? Loc.T("Vault.LockedTip") : HasPassword ? Loc.T("Vault.UnlockedTip") : Loc.T("Vault.SystemTip");
    public bool IsClipboardPending => ClipboardSeconds > 0;
    public string ClipboardText => Loc.F("Status.Clipboard", ClipboardSeconds);
    public bool IsPortable => AppPaths.IsPortable;
    public bool IsNetworkStore => AppHost.Workspace.IsNetworkStore;

    public string WindowTitle => string.IsNullOrEmpty(SourceName) ? AppHost.AppName : $"{AppHost.AppName} – {SourceName}";

    partial void OnSelectedRailChanged(RailItem? value)
    {
        if (value?.Mode is { } mode)
        {
            Tree.Mode = mode;
            TreeVisible = true;
        }
    }

    partial void OnRailExpandedChanged(bool value)
    {
        OnPropertyChanged(nameof(RailWidth));
        AppHost.Settings.RailExpanded = value;
        AppHost.Settings.Save();
    }

    partial void OnTreeVisibleChanged(bool value)
    {
        AppHost.Settings.TreeVisible = value;
        AppHost.Settings.Save();
    }

    partial void OnClipboardSecondsChanged(int value)
    {
        OnPropertyChanged(nameof(IsClipboardPending));
        OnPropertyChanged(nameof(ClipboardText));
    }

    public void Attach(MainWindow window)
    {
        Window = window;
        var ws = AppHost.Workspace;
        ws.Changed += OnWorkspaceChanged;
        ws.VaultChanged += OnVaultChanged;
        ws.SourceChanged += OnSourceChanged;
        ws.ReadOnlyBlocked += OnReadOnlyBlocked;
        ws.SharedStateChanged += OnSharedChanged;
        AppHost.Theme.Changed += OnThemeChanged;
        Loc.LanguageChanged += OnLanguageChanged;
        AppHost.Clipboard.CountdownChanged += OnClipboard;
        LiveStatus.Changed += OnLiveStatus;
        Features.Changed += OnFeaturesChanged;
        Tabs.Items.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(SessionCount));
            OnPropertyChanged(nameof(HasSessions));
            OnPropertyChanged(nameof(SessionText));
        };
        Tree.Refresh();
        RefreshPins();
        RefreshProfiles();
    }

    private void OnReadOnlyBlocked()
    {
        if (Window?.IsActive == true) ShowToast(Loc.T("Shared.Blocked"), true);
    }

    private void OnSharedChanged()
    {
        OnPropertyChanged(nameof(IsShared));
        OnPropertyChanged(nameof(IsReadOnly));
        OnPropertyChanged(nameof(SharedIcon));
        OnPropertyChanged(nameof(SharedText));
        OnPropertyChanged(nameof(TakeOverTip));
    }

    public void RefreshProfiles()
    {
        ProfileItems.Clear();
        foreach (var p in AppHost.Workspace.Profiles) ProfileItems.Add(new ProfileItem(p));
        OnPropertyChanged(nameof(HasProfiles));
    }

    /// <summary>Settings that change the window layout (status bar).</summary>
    public void RefreshLayout() => OnPropertyChanged(nameof(ShowStatusBar));

    public void RefreshPins()
    {
        Pins.Clear();
        foreach (var e in AppHost.Workspace.Pinned) Pins.Add(new PinItem(e));
        OnPropertyChanged(nameof(HasPins));
    }

    public void RefreshSource()
    {
        OnPropertyChanged(nameof(SourceName));
        OnPropertyChanged(nameof(SourceIcon));
        OnPropertyChanged(nameof(SourceBrush));
        OnPropertyChanged(nameof(WindowTitle));
    }

    public void Detach()
    {
        var ws = AppHost.Workspace;
        ws.Changed -= OnWorkspaceChanged;
        ws.VaultChanged -= OnVaultChanged;
        ws.SourceChanged -= OnSourceChanged;
        ws.ReadOnlyBlocked -= OnReadOnlyBlocked;
        ws.SharedStateChanged -= OnSharedChanged;
        AppHost.Theme.Changed -= OnThemeChanged;
        Loc.LanguageChanged -= OnLanguageChanged;
        AppHost.Clipboard.CountdownChanged -= OnClipboard;
        LiveStatus.Changed -= OnLiveStatus;
        Features.Changed -= OnFeaturesChanged;
    }

    /// <summary>Settings → Features changed: menus, tree (greyed kinds), home page and open file browsers follow.</summary>
    private void OnFeaturesChanged()
    {
        OnWorkspaceChanged();
        foreach (var t in Tabs.Items.OfType<Sessions.FileBrowserTabViewModel>()) t.RefreshSyncOption();
        foreach (var t in Tabs.Items.OfType<Sessions.SplitTabViewModel>()) t.RefreshFeatures();
        OnPropertyChanged(nameof(ShowDashboard));
        OnPropertyChanged(nameof(ShowPalette));
    }

    public bool ShowDashboard => Features.On(Features.Monitoring);
    public bool ShowPalette => Features.On(Features.Palette);

    private void OnLiveStatus()
    {
        Tree.UpdateStatus();
        foreach (var t in Tabs.Items.OfType<DashboardTabViewModel>()) t.UpdateStatus();
        foreach (var t in Tabs.Items.OfType<StartTabViewModel>()) t.UpdateStatus();
    }

    // ------------------------------------------------------------------ kiosk (fullscreen single view)

    /// <summary>Fullscreen window that shows only its content (dashboard on a wall screen, RDP fullscreen).</summary>
    [ObservableProperty]
    private bool _isKiosk;

    public bool ShowChrome => !IsKiosk;
    partial void OnIsKioskChanged(bool value)
    {
        OnPropertyChanged(nameof(ShowChrome));
        OnPropertyChanged(nameof(ShowStatusBar));
        Window?.ApplyKiosk(value);
    }

    /// <summary>Leaves kiosk mode: the window becomes a normal window again.</summary>
    [RelayCommand]
    private void ExitKiosk() => IsKiosk = false;

    private void OnClipboard(int seconds) => ClipboardSeconds = seconds;

    private void OnWorkspaceChanged()
    {
        Tree.Refresh();
        Tabs.RefreshAll();
        RefreshPins();
        RefreshProfiles();
        OnPropertyChanged(nameof(EntryCountText));
        OnPropertyChanged(nameof(IsContentHidden));
        OnPropertyChanged(nameof(ShowEmptyTree));
    }

    private void OnVaultChanged()
    {
        OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(HasPassword));
        OnPropertyChanged(nameof(VaultText));
        OnPropertyChanged(nameof(VaultIcon));
        OnPropertyChanged(nameof(VaultTip));
        OnPropertyChanged(nameof(VaultWeak));
        OnPropertyChanged(nameof(IsContentHidden));
        OnPropertyChanged(nameof(ShowEmptyTree));
        Tabs.RefreshAll();
    }

    private void OnSourceChanged()
    {
        RefreshSource();
        OnPropertyChanged(nameof(IsNetworkStore));
        OnSharedChanged();
        OnWorkspaceChanged();
        OnVaultChanged();
    }

    private void OnThemeChanged()
    {
        Tree.Refresh();
        RefreshPins();
        OnPropertyChanged(nameof(SourceBrush));
        foreach (var t in Tabs.Items) t.OnThemeChanged();
    }

    private void OnLanguageChanged()
    {
        foreach (var r in RailItems) r.Relocalize();
        RefreshProfiles();
        OnSharedChanged();
        OnPropertyChanged(nameof(SessionText));
        RefreshPins();
        OnVaultChanged();
        OnPropertyChanged(nameof(EntryCountText));
        OnPropertyChanged(nameof(ClipboardText));
        Tree.Refresh();
        foreach (var t in Tabs.Items) t.Refresh();
    }

    public void ShowToast(string message, bool error = false)
    {
        Toast = message;
        ToastIsError = error;
        HasToast = true;
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void CloseToast() => HasToast = false;

    [RelayCommand]
    private void ToggleRail() => RailExpanded = !RailExpanded;

    [RelayCommand]
    private void ToggleTree() => TreeVisible = !TreeVisible;

    [RelayCommand]
    private void NewTab() => Tabs.OpenStart();

    [RelayCommand]
    private void CloseTab()
    {
        if (Tabs.Selected is { } t) Tabs.Close(t);
    }

    [RelayCommand]
    private void ReopenTab() => Tabs.Reopen();

    [RelayCommand]
    private void NextTab() => Tabs.Cycle(1);

    [RelayCommand]
    private void PreviousTab() => Tabs.Cycle(-1);

    [RelayCommand]
    private void NewWindow() => AppHost.Windows.Open();

    [RelayCommand]
    private void OpenSettings() => Tabs.OpenOrFocus("settings", () => new SettingsTabViewModel());

    /// <summary>Opens settings on a page ("security", "data").</summary>
    [RelayCommand]
    private void OpenSettingsPageCmd(string page) => OpenSettingsPage(page);

    public void OpenSettingsPage(string page)
    {
        var tab = Tabs.OpenOrFocus("settings", () => new SettingsTabViewModel(page));
        if (tab is SettingsTabViewModel s) s.PageIndex = SettingsTabViewModel.PageOf(page);
    }

    [RelayCommand]
    private void OpenSecurity() => OpenSettingsPage("security");

    [RelayCommand]
    private async Task VaultStatusClick()
    {
        if (IsLocked && HasPassword) await Actions.UnlockAsync();
        else OpenSettingsPage("security");
    }

    /// <summary>Home is the first tab: focus it (moving it back to the left edge) or create it there.</summary>
    [RelayCommand]
    private void GoHome() => Tabs.GoHome();

    [RelayCommand]
    private void SetRecentMode(string mode)
    {
        AppHost.Settings.RecentMostUsed = mode == "most";
        AppHost.Settings.Save();
        OnPropertyChanged(nameof(RecentMostUsed));
        Tree.Refresh();
    }

    [RelayCommand]
    private Task NewProfile() => Actions.EditProfileAsync(null);

    [RelayCommand]
    private async Task TakeOver()
    {
        var sh = AppHost.Workspace.Shared;
        if (sh is null || Window is null) return;
        var l = sh.ReadLock();
        var force = false;
        if (l is not null && l.Owner != sh.Owner && !l.IsStale(DateTime.UtcNow))
        {
            if (!await Dialog.ConfirmAsync(Window, Loc.T("Shared.TakeOverTitle"), Loc.F("Shared.TakeOverText", l.User, l.Machine), Loc.T("Shared.TakeOver"),
                    danger: true, icon: "users-round")) return;
            force = true;
        }

        ShowToast(AppHost.Workspace.TakeOverEditing(force) ? Loc.T("Shared.NowEditing") : Loc.T("Shared.StillLocked"), !sh.IsEditor);
    }

    [RelayCommand]
    private void OpenPin(PinItem pin)
    {
        if (pin.Entry.IsContainer)
        {
            Window?.SelectAndReveal(pin.Id);
            Tree.SetExpanded(pin.Entry, true);
        }
        else
        {
            Actions.Open(pin.Entry);
        }
    }

    [RelayCommand]
    private void OpenAbout() => OpenSettingsPage("about");

    [RelayCommand]
    private void OpenDashboard()
    {
        if (Features.On(Features.Monitoring)) Tabs.OpenOrFocus("dashboard", () => new DashboardTabViewModel());
    }


    /// <summary>Dashboard in its own window (monitor and fullscreen from the dashboard settings).</summary>
    [RelayCommand]
    private void OpenDashboardWindow() => AppHost.Windows.OpenDashboard();

    [RelayCommand]
    private void OpenHistory() => Tabs.OpenOrFocus("history", () => new HistoryTabViewModel());

    [RelayCommand]
    private void OpenTrash() => Tabs.OpenOrFocus("trash", () => new TrashTabViewModel());

    [RelayCommand]
    private async Task ToggleLock()
    {
        if (IsLocked) await Actions.UnlockAsync();
        else if (HasPassword) AppHost.Workspace.Lock();
        else OpenSettingsPage("security");
    }

    [RelayCommand]
    private void Undo()
    {
        if (AppHost.Workspace.Undo()) ShowToast(Loc.T("Toast.Undone"));
    }

    [RelayCommand]
    private Task NewEntry(EntryKind kind) => Actions.NewEntryAsync(kind, Window?.SelectedEntryId);

    [RelayCommand]
    private Task Import() => Actions.ImportAsync(null);

    [RelayCommand]
    private Task Export() => Actions.ExportAsync(Window?.SelectedIds ?? []);

    [RelayCommand]
    private void ExpandAll() => Tree.ExpandAll(true);

    [RelayCommand]
    private void CollapseAll() => Tree.ExpandAll(false);
}
