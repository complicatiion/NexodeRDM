using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Core.Model;
using Nexode.Core.Theming;
using Nexode.Platform;

namespace Nexode.App.ViewModels;

/// <summary>Localised list entry: the list instance never changes (stable ComboBox items), only the text.</summary>
public sealed partial class OptionItem(string key, string? icon = null, string? flag = null) : ObservableObject
{
    public string Key { get; } = key;
    public string? Icon { get; } = icon;
    public string? Flag { get; } = flag;
    public bool HasFlag => Flag is not null;
    public string Title => Key.StartsWith('=') ? Key[1..] : Loc.T(Key);
    public void Relocalize() => OnPropertyChanged(nameof(Title));
}

public sealed partial class AccentSwatch(string name, string hex) : ObservableObject
{
    public string Name { get; } = name;
    public string Hex { get; } = hex;
    public IBrush Brush { get; } = new SolidColorBrush(Color.Parse(hex));

    [ObservableProperty]
    private bool _isSelected;
}

public sealed class SourceItem(DataSourceInfo info, bool isActive)
{
    public DataSourceInfo Info { get; } = info;
    public bool IsActive { get; } = isActive;
    public string Name => Info.Name;
    public string Path => Info.Path;
    public string Icon => string.IsNullOrEmpty(Info.Icon) ? "database" : Info.Icon;
    public IBrush? IconBrush => Info.ColorHue is { } h ? AppHost.Theme.FolderBrush(h) : null;
    public bool Exists => File.Exists(Info.Path) || IsActive;
    public string Status => IsActive ? Loc.T("Source.Active") : Exists ? Loc.T("Source.Available") : Loc.T("Source.Missing");
    public bool IsShared => Info.Shared;
    public bool IsSample => Info.IsSample;
    public string StatusClass => IsActive ? "accent" : Exists ? "" : "danger";
    public string SizeText => Exists ? SettingsTabViewModel.FormatSize(new FileInfo(Info.Path).Length) : "–";
}

public sealed class BackupItem(FileInfo file)
{
    public FileInfo File { get; } = file;
    public string Name => File.Name;
    public string When => File.LastWriteTime.ToString("g", CultureInfo.CurrentCulture);
    public string Size => SettingsTabViewModel.FormatSize(File.Length);
}

public sealed class KnownHostItem(string key, Nexode.Protocols.KnownHost host, int usedBy)
{
    public string Key { get; } = key;
    public string Kind => Key.Split(':')[0].ToUpperInvariant();
    public string Target => string.Join(':', Key.Split(':').Skip(1));
    public string Host => Key.Split(':').ElementAtOrDefault(1) ?? "";
    public string Fingerprint => host.Fingerprint;
    public string Algorithm => host.Algorithm;
    public string Details => host.AddedUtc == default ? host.Algorithm : $"{host.Algorithm} · {Loc.F("Hosts.Added", host.AddedUtc.ToLocalTime().ToString("d", System.Globalization.CultureInfo.CurrentCulture))}";

    /// <summary>Entries of the active data source with this host (0 = left over from a removed or renamed entry).</summary>
    public int UsedBy { get; } = usedBy;
    public bool IsUnused => UsedBy == 0;
    public string UsedText => UsedBy == 0 ? Loc.T("Hosts.Unused") : Loc.F("Hosts.UsedBy", UsedBy);
}

public sealed partial class CategoryItem(Category category) : ObservableObject
{
    public Category Category { get; } = category;
    public string Icon => Category.Icon;
    public string Name => CategoryNames.Of(Category);
    public int Usage => AppHost.Workspace.Tree.All.Count(e => e.CategoryId == Category.Id);
    public string UsageText => Usage == 1 ? Loc.T("Category.UsageOne") : Loc.F("Category.Usage", Usage);
}

public static class CategoryNames
{
    /// <summary>Built-in categories show their translated name until the user renames them.</summary>
    public static string Of(Category c) =>
        c.BuiltIn && Category.Defaults.FirstOrDefault(d => d.Id == c.Id)?.Name == c.Name && Loc.T("Cat." + c.Id) is var t && t != "Cat." + c.Id ? t : c.Name;
}

/// <summary>
/// Settings tab. Selection properties are the source of truth while the tab is open; external refreshes only update
/// derived lists (posted to the dispatcher), so a ComboBox is never reset while it is still committing a selection.
/// </summary>
public sealed partial class SettingsTabViewModel : TabViewModel
{
    private static readonly ThemeMode[] ThemeOrder =
        [ThemeMode.Nexode, ThemeMode.Dark, ThemeMode.Shadow, ThemeMode.Amoled, ThemeMode.Mica, ThemeMode.Light, ThemeMode.Lightweight, ThemeMode.System, ThemeMode.Auto];
    private static readonly PaletteVariant[] DarkVariantOrder = [PaletteVariant.Dark, PaletteVariant.Default, PaletteVariant.Shadow, PaletteVariant.Amoled, PaletteVariant.Mica];
    private static readonly PaletteVariant[] LightVariantOrder = [PaletteVariant.Default, PaletteVariant.Lightweight];
    private bool _loading;
    private bool _refreshQueued;

    public SettingsTabViewModel(string? page = null)
    {
        Icon = "settings";
        Title = Loc.T("Tab.Settings");
        Accents = Palette.AccentPresets.Select(p => new AccentSwatch(p.Name, p.Hex)).ToList();
        ThemeNames =
        [
            new("Theme.Nexode", "moon-star"), new("Theme.Dark", "moon"), new("Theme.Shadow", "cloud-moon"), new("Theme.Amoled", "circle"), new("Theme.Mica", "app-window"),
            new("Theme.Light", "sun"), new("Theme.Lightweight", "sun-dim"), new("Theme.System", "monitor"), new("Theme.Auto", "sun-moon")
        ];
        AutoSources = [new("Theme.FixedTimes"), new("Theme.Sun")];
        DarkVariants = [new("Theme.Dark"), new("Theme.Nexode"), new("Theme.Shadow"), new("Theme.Amoled"), new("Theme.Mica")];
        LightVariants = [new("Theme.Light"), new("Theme.Lightweight")];
        Languages = [.. Loc.Languages.Select(l => new OptionItem("=" + l.Name, flag: l.Code))];
        OpenBehaviors = [new("Open.Focus"), new("Open.NewTab")];
        SortModes = [new("Sort.Manual"), new("Sort.Name"), new("Sort.Kind"), new("Sort.LastUsed")];
        EncryptionModes = [new("Security.EncSecrets"), new("Security.EncFull")];
        ScaleOptions = [.. UiScaling.Presets.Select(p => new OptionItem(p.Key)), new OptionItem("Scale.Custom")];
        PageIndex = PageOf(page);
        Load();
        InitFeatures();
        Loc.LanguageChanged += Relocalize;
    }

    public override string Key => "settings";
    public override TabViewModel Recreate() => new SettingsTabViewModel();

    private static AppSettings S => AppHost.Settings;
    private static Workspace Ws => AppHost.Workspace;

    public IReadOnlyList<AccentSwatch> Accents { get; }
    public IReadOnlyList<OptionItem> ThemeNames { get; }
    public IReadOnlyList<OptionItem> AutoSources { get; }
    public IReadOnlyList<OptionItem> DarkVariants { get; }
    public IReadOnlyList<OptionItem> LightVariants { get; }
    [ObservableProperty] private int _lightVariantIndex;
    [ObservableProperty] private bool _useSystemAccent;
    public bool CustomAccent => !UseSystemAccent;
    public IReadOnlyList<OptionItem> Languages { get; }
    public IReadOnlyList<OptionItem> OpenBehaviors { get; }
    public IReadOnlyList<OptionItem> SortModes { get; }
    public IReadOnlyList<OptionItem> EncryptionModes { get; }
    public ObservableCollection<SourceItem> Sources { get; } = [];
    public ObservableCollection<BackupItem> Backups { get; } = [];
    public ObservableCollection<CategoryItem> Categories { get; } = [];
    public ObservableCollection<KnownHostItem> KnownHosts { get; } = [];

    [ObservableProperty] private int _pageIndex;
    [ObservableProperty] private int _themeIndex;
    [ObservableProperty] private int _autoSourceIndex;
    [ObservableProperty] private int _darkVariantIndex;
    [ObservableProperty] private string _lightFrom = "";
    [ObservableProperty] private string _darkFrom = "";
    [ObservableProperty] private string _latitude = "";
    [ObservableProperty] private string _longitude = "";
    [ObservableProperty] private Color _accentColor;
    [ObservableProperty] private string _accentHex = "";
    [ObservableProperty] private bool _compact;
    [ObservableProperty] private bool _showHostInTree;
    [ObservableProperty] private bool _autoFolderColors;
    [ObservableProperty] private bool _greetings;
    [ObservableProperty] private bool _showHints;
    [ObservableProperty] private int _languageIndex;
    [ObservableProperty] private int _openBehaviorIndex;
    [ObservableProperty] private int _rootSortIndex;
    [ObservableProperty] private bool _foldersFirst;
    [ObservableProperty] private bool _confirmProduction;
    [ObservableProperty] private decimal _terminalFontSize;
    [ObservableProperty] private decimal _terminalScrollback;
    [ObservableProperty] private bool _terminalCopyOnSelect;
    [ObservableProperty] private decimal _autoLockMinutes;
    [ObservableProperty] private bool _lockOnMinimize;
    [ObservableProperty] private bool _lockOnSessionLock;
    [ObservableProperty] private bool _lockOnSleep;
    [ObservableProperty] private bool _reauthToReveal;
    [ObservableProperty] private bool _exportWithMaster;
    [ObservableProperty] private decimal _clipboardSeconds;
    [ObservableProperty] private int _encryptionIndex;
    [ObservableProperty] private decimal _backupKeep;
    [ObservableProperty] private decimal _trashDays;
    [ObservableProperty] private bool _fileTypeRegistered;
    [ObservableProperty] private bool _busy;

    private ThemeMode CurrentTheme => ThemeOrder[Math.Clamp(ThemeIndex, 0, ThemeOrder.Length - 1)];
    public bool IsAuto => CurrentTheme == ThemeMode.Auto;
    public bool HasDarkVariant => CurrentTheme is ThemeMode.Auto or ThemeMode.System;
    public bool IsSunMode => IsAuto && AutoSourceIndex == 1;
    public bool IsFixedMode => IsAuto && AutoSourceIndex == 0;
    public bool HasPassword => Ws.HasPassword;
    public bool NoPassword => !HasPassword;
    public bool IsLocked => Ws.IsLocked;
    public bool VaultWeak => Ws.Vault?.IsWeak == true;
    public bool IsPortable => AppPaths.IsPortable;
    public bool CanRemovePassword => HasPassword && !IsPortable;
    public bool CanChooseEncryption => HasPassword && !IsLocked;
    public bool IsWindows => OperatingSystem.IsWindows();
    public string VaultStatus => HasPassword ? Loc.T("Vault.StatusPassword") : VaultWeak ? Loc.T("Vault.StatusWeak") : Loc.T("Vault.StatusSystem");
    public string VaultStateText => IsLocked ? Loc.T("Vault.Locked") : HasPassword ? Loc.T("Vault.Unlocked") : Loc.T("Vault.System");
    public string VaultStateClass => IsLocked ? "warning" : "success";
    public string DataDirectory => AppPaths.DataDirectory;
    public string AppDirectory => AppPaths.AppDirectory;
    public string BackupDirectory => Ws.Store is { } st ? DataSources.BackupFolder(st.Id) : AppPaths.BackupDirectory;
    public bool CanRegisterFileType => FileAssociation.IsSupported && !AppPaths.IsPortable;
    public string FileTypeStatus => FileTypeRegistered ? Loc.T("Integration.Registered") : Loc.T("Integration.NotRegistered");
    public string FileTypeButton => FileTypeRegistered ? Loc.T("Integration.Unregister") : Loc.T("Integration.Register");
    public bool IsNetworkStore => Ws.IsNetworkStore;
    public string ActiveName => Ws.Info?.Name ?? "";
    public string ActivePath => Ws.Info?.Path ?? "";
    public string ActiveEntries => Ws.IsContentHidden ? "–" : Ws.Tree.Count.ToString(CultureInfo.CurrentCulture);
    public string ActiveSize => Ws.Info is { } i && File.Exists(i.Path) ? FormatSize(new FileInfo(i.Path).Length + (File.Exists(i.Path + "-wal") ? new FileInfo(i.Path + "-wal").Length : 0)) : "–";
    public string ActiveBackup => Backups.FirstOrDefault()?.When ?? Loc.T("Source.NoBackup");
    public string ActiveEncryption => Ws.IsFullyEncrypted ? Loc.T("Security.EncFull") : Loc.T("Security.EncSecrets");
    public string ActiveVault => HasPassword ? Loc.T("Source.VaultPassword") : Loc.T("Source.VaultSystem");
    public bool HasBackups => Backups.Count > 0;
    public bool HasKnownHosts => KnownHosts.Count > 0;

    public static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.#} MB"
    };

    // ------------------------------------------------------------------ load / refresh

    private void Load()
    {
        _loading = true;
        ThemeIndex = Math.Max(0, Array.IndexOf(ThemeOrder, S.Theme));
        AutoSourceIndex = (int)S.AutoSource;
        DarkVariantIndex = Math.Max(0, Array.IndexOf(DarkVariantOrder, S.DarkVariant));
        LightVariantIndex = Math.Max(0, Array.IndexOf(LightVariantOrder, S.LightVariant));
        UseSystemAccent = S.UseSystemAccent;
        LightFrom = S.LightFrom;
        DarkFrom = S.DarkFrom;
        Latitude = S.Latitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        Longitude = S.Longitude?.ToString(CultureInfo.InvariantCulture) ?? "";
        AccentHex = S.Accent;
        AccentColor = Color.TryParse(S.Accent, out var c) ? c : Color.Parse(Palette.DefaultAccent);
        Compact = S.Compact;
        ShowHostInTree = S.ShowHostInTree;
        AutoFolderColors = S.AutoFolderColors;
        Greetings = S.Greetings;
        ShowHints = S.ShowHints;
        LanguageIndex = Math.Max(0, Loc.Languages.ToList().FindIndex(l => l.Code == Loc.Language));
        OpenBehaviorIndex = (int)S.OpenBehavior;
        RootSortIndex = (int)S.RootSort;
        FoldersFirst = S.FoldersFirst;
        ConfirmProduction = S.ConfirmConnect;
        TerminalFontSize = S.TerminalFontSize;
        TerminalScrollback = S.TerminalScrollback;
        TerminalCopyOnSelect = S.TerminalCopyOnSelect;
        AutoLockMinutes = S.AutoLockMinutes;
        LockOnMinimize = S.LockOnMinimize;
        LockOnSessionLock = S.LockOnSessionLock;
        LockOnSleep = S.LockOnSleep;
        ReauthToReveal = S.ReauthToReveal;
        ExportWithMaster = S.ExportWithMasterPassword;
        ClipboardSeconds = S.ClipboardClearSeconds;
        EncryptionIndex = Ws.IsFullyEncrypted ? 1 : 0;
        BackupKeep = S.BackupKeep;
        TrashDays = S.TrashRetentionDays;
        FileTypeRegistered = FileAssociation.IsSupported && Environment.ProcessPath is { } exe && FileAssociation.IsRegistered(exe);
        UpdateAccentSelection();
        LoadV3();
        LoadV4();
        LoadLists();
        _loading = false;
    }

    private void LoadLists()
    {
        LoadTemplates();
        Sources.Clear();
        foreach (var info in S.DataSources) Sources.Add(new SourceItem(info, info.Id == Ws.Info?.Id));
        Backups.Clear();
        foreach (var f in DataSources.Backups()) Backups.Add(new BackupItem(f));
        Categories.Clear();
        foreach (var cat in Ws.CategoryList) Categories.Add(new CategoryItem(cat));
        KnownHosts.Clear();
        var hosts = HostUsage();
        foreach (var (key, host) in AppHost.KnownHosts.All())
        {
            var name = key.Split(':').ElementAtOrDefault(1) ?? "";
            KnownHosts.Add(new KnownHostItem(key, host, hosts.GetValueOrDefault(name)));
        }

        FilterKnownHosts();
        LoadListsV3();
    }

    /// <summary>External change (workspace, vault, theme): refresh derived data once, after the current UI pass.</summary>
    public override void Refresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _refreshQueued = false;
            Title = Loc.T("Tab.Settings");
            LoadLists();
            _loading = true;
            EncryptionIndex = Ws.IsFullyEncrypted ? 1 : 0;
            _loading = false;
            foreach (var name in new[]
                     {
                         nameof(HasPassword), nameof(NoPassword), nameof(IsLocked), nameof(VaultWeak), nameof(CanRemovePassword), nameof(CanChooseEncryption),
                         nameof(VaultStatus), nameof(VaultStateText), nameof(VaultStateClass), nameof(IsNetworkStore), nameof(ActiveName), nameof(ActivePath),
                         nameof(ActiveEntries), nameof(ActiveSize), nameof(ActiveBackup), nameof(ActiveEncryption), nameof(ActiveVault), nameof(HasBackups),
                         nameof(HasKnownHosts), nameof(FileTypeStatus), nameof(FileTypeButton), nameof(BackupDirectory), nameof(HasSampleSource),
                         nameof(IsSampleActive), nameof(HasProfileList), nameof(IsSharedSource), nameof(ActiveShared), nameof(CanCleanupSample)
                     })
            {
                OnPropertyChanged(name);
            }
        }, DispatcherPriority.Background);
    }

    private void Relocalize()
    {
        foreach (var o in ThemeNames.Concat(AutoSources).Concat(DarkVariants).Concat(LightVariants).Concat(OpenBehaviors).Concat(SortModes).Concat(EncryptionModes).Concat(ScaleOptions)) o.Relocalize();
        RelocalizeFeatures();
        OnPropertyChanged(nameof(AnimationSpeedText));
        RelocalizeV4();
        Refresh();
    }

    private void UpdateAccentSelection()
    {
        foreach (var a in Accents) a.IsSelected = string.Equals(a.Hex, S.Accent, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Persists; theme and tree changes are applied after the binding update finished.</summary>
    private void Save(bool theme = false, bool tree = false)
    {
        if (_loading) return;
        S.Save();
        if (!theme && !tree) return;
        Dispatcher.UIThread.Post(() =>
        {
            if (theme) AppHost.Theme.Apply();
            if (tree) Ws.Rebuild();
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ appearance

    partial void OnThemeIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.Theme = ThemeOrder[Math.Clamp(value, 0, ThemeOrder.Length - 1)];
        // Mica pairs with the system accent; every other theme uses the Nexode accent.
        S.UseSystemAccent = S.Theme == ThemeMode.Mica;
        _loading = true;
        UseSystemAccent = S.UseSystemAccent;
        _loading = false;
        OnPropertyChanged(nameof(CustomAccent));
        OnPropertyChanged(nameof(IsAuto));
        OnPropertyChanged(nameof(HasDarkVariant));
        OnPropertyChanged(nameof(IsSunMode));
        OnPropertyChanged(nameof(IsFixedMode));
        Save(theme: true);
    }

    partial void OnAutoSourceIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.AutoSource = (AutoThemeSource)value;
        OnPropertyChanged(nameof(IsSunMode));
        OnPropertyChanged(nameof(IsFixedMode));
        Save(theme: true);
    }

    partial void OnDarkVariantIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.DarkVariant = DarkVariantOrder[Math.Clamp(value, 0, DarkVariantOrder.Length - 1)];
        S.DarkVariantAmoled = S.DarkVariant == PaletteVariant.Amoled;
        Save(theme: true);
    }

    partial void OnLightVariantIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.LightVariant = LightVariantOrder[Math.Clamp(value, 0, LightVariantOrder.Length - 1)];
        Save(theme: true);
    }

    partial void OnUseSystemAccentChanged(bool value)
    {
        OnPropertyChanged(nameof(CustomAccent));
        if (_loading) return;
        S.UseSystemAccent = value;
        Save(theme: true);
    }

    partial void OnLightFromChanged(string value)
    {
        if (_loading || !TimeOnly.TryParse(value, CultureInfo.CurrentCulture, out _)) return;
        S.LightFrom = value;
        Save(theme: true);
    }

    partial void OnDarkFromChanged(string value)
    {
        if (_loading || !TimeOnly.TryParse(value, CultureInfo.CurrentCulture, out _)) return;
        S.DarkFrom = value;
        Save(theme: true);
    }

    partial void OnLatitudeChanged(string value)
    {
        if (_loading) return;
        S.Latitude = double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= -90 and <= 90 ? v : null;
        Save(theme: true);
    }

    partial void OnLongitudeChanged(string value)
    {
        if (_loading) return;
        S.Longitude = double.TryParse(value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v is >= -180 and <= 180 ? v : null;
        Save(theme: true);
    }

    partial void OnAccentColorChanged(Color value)
    {
        if (_loading) return;
        SetAccent($"#{value.R:X2}{value.G:X2}{value.B:X2}");
    }

    partial void OnAccentHexChanged(string value)
    {
        if (_loading || !Rgb.TryParse(value, out var rgb)) return;
        SetAccent(rgb.ToHex());
    }

    private void SetAccent(string hex)
    {
        if (string.Equals(S.Accent, hex, StringComparison.OrdinalIgnoreCase)) return;
        S.Accent = hex;
        _loading = true;
        AccentHex = hex;
        AccentColor = Color.Parse(hex);
        _loading = false;
        UpdateAccentSelection();
        Save(theme: true);
    }

    partial void OnCompactChanged(bool value)
    {
        S.Compact = value;
        Save(tree: true);
    }

    partial void OnShowHostInTreeChanged(bool value)
    {
        S.ShowHostInTree = value;
        Save(tree: true);
    }

    partial void OnAutoFolderColorsChanged(bool value)
    {
        S.AutoFolderColors = value;
        Save();
    }

    partial void OnGreetingsChanged(bool value)
    {
        S.Greetings = value;
        Save();
        if (!_loading) Dispatcher.UIThread.Post(() => AppHost.Windows.RefreshStartPages());
    }

    partial void OnShowHintsChanged(bool value)
    {
        S.ShowHints = value;
        UiState.Instance.ShowHints = value;
        Save();
    }

    partial void OnLanguageIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.Language = Loc.Languages[Math.Clamp(value, 0, Loc.Languages.Count - 1)].Code;
        S.Save();
        Dispatcher.UIThread.Post(() => Loc.Apply(S.Language), DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ behavior

    partial void OnOpenBehaviorIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.OpenBehavior = (OpenBehavior)value;
        Save();
    }

    partial void OnRootSortIndexChanged(int value)
    {
        if (_loading || value < 0) return;
        S.RootSort = (SortMode)value;
        Save(tree: true);
    }

    partial void OnFoldersFirstChanged(bool value)
    {
        S.FoldersFirst = value;
        Save(tree: true);
    }

    partial void OnConfirmProductionChanged(bool value)
    {
        S.ConfirmConnect = value;
        Save();
    }

    partial void OnTerminalFontSizeChanged(decimal value)
    {
        S.TerminalFontSize = (int)Math.Clamp(value, 8, 32);
        Save();
    }

    partial void OnTerminalScrollbackChanged(decimal value)
    {
        S.TerminalScrollback = (int)Math.Clamp(value, 0, 100000);
        Save();
    }

    partial void OnTerminalCopyOnSelectChanged(bool value)
    {
        S.TerminalCopyOnSelect = value;
        Save();
    }

    // ------------------------------------------------------------------ security

    partial void OnAutoLockMinutesChanged(decimal value)
    {
        S.AutoLockMinutes = (int)value;
        Save();
    }

    partial void OnLockOnMinimizeChanged(bool value)
    {
        S.LockOnMinimize = value;
        Save();
    }

    partial void OnLockOnSessionLockChanged(bool value)
    {
        S.LockOnSessionLock = value;
        Save();
    }

    partial void OnLockOnSleepChanged(bool value)
    {
        S.LockOnSleep = value;
        Save();
    }

    partial void OnReauthToRevealChanged(bool value)
    {
        S.ReauthToReveal = value;
        Save();
    }

    partial void OnExportWithMasterChanged(bool value)
    {
        S.ExportWithMasterPassword = value;
        Save();
    }

    partial void OnClipboardSecondsChanged(decimal value)
    {
        S.ClipboardClearSeconds = (int)value;
        Save();
    }

    partial void OnEncryptionIndexChanged(int oldValue, int newValue)
    {
        if (_loading || newValue < 0 || newValue == (Ws.IsFullyEncrypted ? 1 : 0)) return;
        Dispatcher.UIThread.Post(() => _ = ChangeEncryptionAsync(newValue == 1));
    }

    private async Task ChangeEncryptionAsync(bool full)
    {
        if (Owner?.Window is not { } window) return;
        var ok = full
            ? HasPassword && await Dialog.ConfirmAsync(window, Loc.T("Security.EncFull"), Loc.T("Security.EncFullConfirm"), Loc.T("Security.Encrypt"), icon: "lock")
            : await Dialog.ConfirmAsync(window, Loc.T("Security.EncSecrets"), Loc.T("Security.EncOffConfirm"), Loc.T("Ok"), icon: "lock-open");
        if (ok && await Owner.Actions.EnsureUnlockedAsync())
        {
            Busy = true;
            try
            {
                await Task.Yield();
                Ws.SetFullEncryption(full);
                Owner.ShowToast(Loc.T(full ? "Toast.EncFullOn" : "Toast.EncFullOff"));
            }
            catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
            {
                Owner.ShowToast(ex.Message, true);
            }
            finally
            {
                Busy = false;
            }
        }

        _loading = true;
        EncryptionIndex = Ws.IsFullyEncrypted ? 1 : 0;
        _loading = false;
        Refresh();
    }

    [RelayCommand]
    private void PickAccent(AccentSwatch swatch) => SetAccent(swatch.Hex);

    [RelayCommand]
    private void UseCurrentLocation()
    {
        // No network lookup: Bremen as a sensible default the user can change.
        Latitude = "53.08";
        Longitude = "8.80";
    }

    [RelayCommand]
    private async Task SetPassword()
    {
        if (Owner?.Window is not { } window) return;
        if (!await Owner.Actions.EnsureUnlockedAsync()) return;
        var pw = await Dialog.PasswordAsync(window, Loc.T(HasPassword ? "Vault.ChangePassword" : "Vault.SetPassword"), Loc.T("Vault.SetPasswordText"), confirm: true, minLength: 8);
        if (pw is null) return;
        Busy = true;
        await Task.Run(() => Ws.SetMasterPassword(pw));
        Busy = false;
        Owner.ShowToast(Loc.T("Toast.PasswordSet"));
        Refresh();
    }

    [RelayCommand]
    private async Task RemovePassword()
    {
        if (Owner?.Window is not { } window) return;
        if (!await Owner.Actions.EnsureUnlockedAsync()) return;
        if (Ws.IsFullyEncrypted &&
            !await Dialog.ConfirmAsync(window, Loc.T("Vault.RemovePassword"), Loc.T("Security.RemoveNeedsSecretsOnly"), Loc.T("Security.SwitchAndRemove"), icon: "lock-open"))
        {
            return;
        }

        var message = Loc.T("Vault.RemovePasswordText") + (Ws.Vault?.IsWeak == true || !OperatingSystem.IsWindows() ? "\n\n" + Loc.T("Vault.RemoveWeakWarning") : "");
        if (!await Dialog.ConfirmAsync(window, Loc.T("Vault.RemovePassword"), message, Loc.T("Vault.RemovePassword"), danger: true)) return;
        Ws.RemoveMasterPassword();
        Owner.ShowToast(Loc.T("Toast.PasswordRemoved"));
        Refresh();
    }

    [RelayCommand]
    private void LockNow() => Ws.Lock();

    [RelayCommand]
    private async Task Unlock()
    {
        if (Owner is not null) await Owner.Actions.UnlockAsync();
    }

    [RelayCommand]
    private void ForgetHost(KnownHostItem item)
    {
        AppHost.KnownHosts.Remove(item.Key);
        Refresh();
    }

    // ------------------------------------------------------------------ data sources

    [RelayCommand]
    private async Task NewSource()
    {
        if (Owner?.Window is not { } window) return;
        var name = await Dialog.InputAsync(window, Loc.T("Source.New"), Loc.T("Field.Name"), Loc.T("Source.NewDefault"), "database");
        if (name is null) return;
        var info = DataSources.CreateInfo(name);
        info.ColorHue = FolderColors.Next(S.DataSources.Where(d => d.ColorHue is not null).Select(d => d.ColorHue!.Value).ToList(), AppHost.Theme.AccentHue);
        if (await DataSources.OpenAsync(window, info, isNew: true)) Owner.ShowToast(Loc.F("Toast.SourceOpened", name));
        Refresh();
    }

    [RelayCommand]
    private async Task OpenSourceFile()
    {
        if (Owner?.Window is not { } window) return;
        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.T("Source.Open"),
            FileTypeFilter = [new FilePickerFileType(Loc.T("Source.FileType")) { Patterns = ["*.db"] }]
        });
        var path = files.FirstOrDefault()?.TryGetLocalPath();
        if (path is null) return;
        var existing = S.DataSources.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var info = existing ?? new DataSourceInfo { Name = Path.GetFileNameWithoutExtension(path), Path = path };
        if (existing is null && Shell.IsNetworkPath(path))
        {
            var choice = await Dialog.ChoiceAsync(window, Loc.T("Source.Open"), Loc.T("Shared.OpenAsTeam"),
                [(Loc.T("Shared.OpenAlone"), "user", false), (Loc.T("Shared.New"), "users-round", false)]);
            if (choice is null) return;
            info.Shared = choice == 1;
        }

        await TryOpen(window, info);
    }

    private async Task TryOpen(Avalonia.Controls.Window window, DataSourceInfo info)
    {
        try
        {
            await DataSources.OpenAsync(window, info, isNew: false);
            Owner?.ShowToast(Loc.F("Toast.SourceOpened", info.Name));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            await Dialog.InfoAsync(window, Loc.T("Source.Open"), Loc.F("Source.OpenFailed", ex.Message), "circle-alert");
        }

        Refresh();
    }

    [RelayCommand]
    private async Task SwitchSource(SourceItem item)
    {
        if (item.IsActive || Owner?.Window is not { } window) return;
        await TryOpen(window, item.Info);
    }

    [RelayCommand]
    private async Task EditSource(SourceItem item)
    {
        if (Owner?.Window is not { } window) return;
        var wasShared = item.Info.Shared;
        var result = await SourceEditDialog.ShowAsync(window, item.Info);
        if (!result) return;
        if (item.IsActive && wasShared != item.Info.Shared)
        {
            // Switching between local and team mode reopens the data source.
            S.Save();
            await TryOpen(window, item.Info);
            return;
        }

        if (item.IsActive && Ws.Store is { } store) store.Name = item.Info.Name;
        S.Save();
        Ws.Rebuild();
        AppHost.Windows.RefreshSourceNames();
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveSource(SourceItem item)
    {
        if (item.IsActive || Owner?.Window is not { } window) return;
        var choice = await Dialog.ChoiceAsync(window, Loc.T("Source.Remove"), Loc.F("Source.RemoveChoice", item.Name, item.Path),
            [(Loc.T("Source.RemoveList"), "list-x", false), (Loc.T("Source.DeleteFile"), "trash-2", true)]);
        if (choice is null) return;
        if (choice == 1)
        {
            if (!await Dialog.ConfirmAsync(window, Loc.T("Source.DeleteFile"), Loc.F("Source.DeleteFileConfirm", item.Name), Loc.T("Source.DeleteFile"), danger: true)) return;
            try
            {
                DataSources.DeleteFile(item.Info);
            }
            catch (IOException ex)
            {
                Owner.ShowToast(ex.Message, true);
            }
        }
        else
        {
            S.DataSources.Remove(item.Info);
            S.Save();
        }

        Refresh();
    }

    [RelayCommand]
    private void BackupNow()
    {
        try
        {
            var file = Ws.BackupNow();
            Owner?.ShowToast(Loc.F("Toast.BackupDone", Path.GetFileName(file)));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or InvalidOperationException)
        {
            Owner?.ShowToast(ex.Message, true);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task RestoreBackup(BackupItem item)
    {
        if (Owner?.Window is not { } window) return;
        if (!await Dialog.ConfirmAsync(window, Loc.T("Backup.Restore"), Loc.F("Backup.RestoreConfirm", item.When), Loc.T("Backup.Restore"), icon: "history")) return;
        try
        {
            await DataSources.RestoreAsync(window, item.File);
            Owner.ShowToast(Loc.T("Toast.Restored"));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            await Dialog.InfoAsync(window, Loc.T("Backup.Restore"), ex.Message, "circle-alert");
        }

        Refresh();
    }

    [RelayCommand]
    private void DeleteBackup(BackupItem item)
    {
        try
        {
            item.File.Delete();
        }
        catch (IOException ex)
        {
            Owner?.ShowToast(ex.Message, true);
        }

        Refresh();
    }

    [RelayCommand]
    private void OpenBackups() => Shell.Open(BackupDirectory);

    [RelayCommand]
    private void OpenDataFolder() => Shell.Open(AppPaths.DataDirectory);

    [RelayCommand]
    private void OpenAppFolder() => Shell.Open(AppPaths.AppDirectory);

    [RelayCommand]
    private void OpenTrash() => Owner?.Tabs.OpenOrFocus("trash", () => new TrashTabViewModel());

    [RelayCommand]
    private Task ExportConnections() => Owner?.Actions.ExportAsync([]) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ImportFile() => Owner?.Actions.ImportAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task ImportForeign() => Owner?.Actions.ImportForeignAsync() ?? Task.CompletedTask;

    [RelayCommand]
    private Task ExportSettings() => Owner?.Window is { } w ? SettingsTransfer.ExportAsync(w, themeOnly: false) : Task.CompletedTask;

    [RelayCommand]
    private Task ExportTheme() => Owner?.Window is { } w ? SettingsTransfer.ExportAsync(w, themeOnly: true) : Task.CompletedTask;


    partial void OnBackupKeepChanged(decimal value)
    {
        S.BackupKeep = (int)value;
        Save();
    }

    partial void OnTrashDaysChanged(decimal value)
    {
        S.TrashRetentionDays = (int)value;
        Save();
    }

    // ------------------------------------------------------------------ categories

    [RelayCommand]
    private async Task AddCategory()
    {
        if (Owner?.Window is not { } window) return;
        var result = await CategoryEditDialog.ShowAsync(window, null);
        if (result is null) return;
        var list = Ws.CategoryList.ToList();
        list.Add(new Category { Id = "c-" + Guid.NewGuid().ToString("N")[..8], Name = result.Value.Name, Icon = result.Value.Icon, Order = list.Count == 0 ? 0 : list.Max(c => c.Order) + 1 });
        Ws.SaveCategories(list);
        Refresh();
    }

    [RelayCommand]
    private async Task EditCategory(CategoryItem item)
    {
        if (Owner?.Window is not { } window) return;
        var result = await CategoryEditDialog.ShowAsync(window, item.Category);
        if (result is null) return;
        var list = Ws.CategoryList.Select(c => c.Id == item.Category.Id
            ? new Category { Id = c.Id, Name = result.Value.Name, Icon = result.Value.Icon, BuiltIn = c.BuiltIn, Order = c.Order, ColorHue = c.ColorHue }
            : c).ToList();
        Ws.SaveCategories(list);
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteCategory(CategoryItem item)
    {
        if (Owner?.Window is not { } window) return;
        var message = Loc.F("Category.DeleteConfirm", item.Name) + (item.Usage > 0 ? "\n\n" + Loc.F("Category.DeleteUsed", item.Usage) : "");
        if (!await Dialog.ConfirmAsync(window, Loc.T("Category.Delete"), message, Loc.T("Category.Delete"), danger: true)) return;
        Ws.SaveCategories(Ws.CategoryList.Where(c => c.Id != item.Category.Id));
        Refresh();
    }

    [RelayCommand]
    private void RestoreCategories()
    {
        var list = Ws.CategoryList.ToList();
        foreach (var d in Category.Defaults.Where(d => list.All(c => c.Id != d.Id)))
        {
            list.Add(new Category { Id = d.Id, Name = d.Name, Icon = d.Icon, BuiltIn = true, Order = d.Order });
        }

        Ws.SaveCategories(list);
        Refresh();
    }

    // ------------------------------------------------------------------ integration

    [RelayCommand]
    private void RegisterFileType()
    {
        if (Environment.ProcessPath is not { } exe) return;
        try
        {
            if (FileTypeRegistered)
            {
                FileAssociation.Unregister();
            }
            else
            {
                var icon = OperatingSystem.IsWindows() ? Path.Combine(AppPaths.AppDirectory, "nexode-file.ico") : Path.Combine(AppPaths.AppDirectory, "nexode.png");
                if (!File.Exists(icon)) icon = exe;
                FileAssociation.Register(exe, icon, Loc.T("Integration.FileDescription"));
            }

            FileTypeRegistered = FileAssociation.IsRegistered(exe);
            OnPropertyChanged(nameof(FileTypeButton));
            Owner?.ShowToast(Loc.T(FileTypeRegistered ? "Toast.FileTypeRegistered" : "Toast.FileTypeRemoved"));
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or System.Security.SecurityException)
        {
            Owner?.ShowToast(ex.Message, true);
        }
    }

    partial void OnFileTypeRegisteredChanged(bool value)
    {
        OnPropertyChanged(nameof(FileTypeStatus));
        OnPropertyChanged(nameof(FileTypeButton));
    }

    public override void OnClosed()
    {
        Loc.LanguageChanged -= Relocalize;
        CloseFeatures();
    }
}
