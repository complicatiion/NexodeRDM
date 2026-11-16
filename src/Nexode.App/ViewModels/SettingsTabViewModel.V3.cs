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

namespace Nexode.App.ViewModels;

/// <summary>Environment row on the settings page.</summary>
public sealed class EnvItem(EnvironmentDef env)
{
    public EnvironmentDef Env { get; } = env;
    public string Name => Envs.Name(Env);
    public string Short => Envs.Short(Env);
    public IBrush Brush => new SolidColorBrush(Envs.ColorOf(Env));
    public IBrush Background => new SolidColorBrush(Envs.ColorOf(Env), 0.18);
    public int Usage => AppHost.Workspace.Tree.All.Count(e => e.EnvironmentId == Env.Id);
    public string UsageText => Usage == 1 ? Loc.T("Category.UsageOne") : Loc.F("Category.Usage", Usage);
}

/// <summary>v0.0.3 settings: animations, scaling, status bar, environments, profiles, sample data and testing, team sources.</summary>
public sealed partial class SettingsTabViewModel
{
    public IReadOnlyList<OptionItem> ScaleOptions { get; }
    public ObservableCollection<EnvItem> EnvironmentItems { get; } = [];
    public ObservableCollection<ProfileItem> ProfileList { get; } = [];
    public bool HasProfileList => ProfileList.Count > 0;

    [ObservableProperty] private bool _showStatusBar;
    [ObservableProperty] private bool _animations;
    [ObservableProperty] private double _animationSpeedStep;
    [ObservableProperty] private int _scaleIndex;
    [ObservableProperty] private double _customScale;
    [ObservableProperty] private bool _autoReconnect;
    [ObservableProperty] private decimal _reconnectAttempts;
    [ObservableProperty] private string _testHost = "";
    [ObservableProperty] private decimal _testPort = 22;
    [ObservableProperty] private string _testResult = "";
    [ObservableProperty] private bool _testOk;

    public string AnimationSpeedText => Motion.Speeds[Math.Clamp((int)Math.Round(AnimationSpeedStep), 0, Motion.Speeds.Length - 1)].ToString("0.##", CultureInfo.CurrentCulture) + "×";
    public bool IsCustomScale => ScaleIndex == ScaleOptions.Count - 1;
    public string CustomScaleText => CustomScale.ToString("0", CultureInfo.CurrentCulture) + " %";
    public bool PresetPerformance => !Animations;
    public bool PresetBalanced => Animations && Math.Abs(AppHost.Settings.AnimationSpeed - 1.0) < 0.01;
    public bool PresetSmooth => Animations && Math.Abs(AppHost.Settings.AnimationSpeed - 0.75) < 0.01;
    public bool PresetFast => Animations && Math.Abs(AppHost.Settings.AnimationSpeed - 1.75) < 0.01;
    public bool HasSampleSource => S.DataSources.Any(d => d.IsSample);
    public bool IsSampleActive => Ws.Info?.IsSample == true;
    public bool IsSharedSource => Ws.Shared is not null;
    public string ActiveShared => Ws.Shared is null ? Loc.T("No") : Ws.Shared.IsEditor ? Loc.T("Shared.EditingShort") : Loc.T("Shared.ReadOnlyShort");
    public bool HasTestResult => TestResult.Length > 0;

    private void LoadV3()
    {
        ShowStatusBar = S.ShowStatusBar;
        Animations = S.Animations;
        AnimationSpeedStep = Math.Max(0, Array.FindIndex(Motion.Speeds, v => Math.Abs(v - S.AnimationSpeed) < 0.01));
        if (Math.Abs(Motion.Speeds[(int)AnimationSpeedStep] - S.AnimationSpeed) > 0.01) AnimationSpeedStep = 3;
        var presetIndex = Array.FindIndex(UiScaling.Presets, p => Math.Abs(p.Value - S.UiScale) < 0.001);
        ScaleIndex = presetIndex >= 0 ? presetIndex : UiScaling.Presets.Length;
        CustomScale = Math.Round((S.UiScale > 0 ? S.UiScale : 1.0) * 100 / 5) * 5;
        AutoReconnect = S.AutoReconnect;
        ReconnectAttempts = S.ReconnectAttempts;
    }

    private void LoadListsV3()
    {
        EnvironmentItems.Clear();
        foreach (var e in Ws.EnvironmentList) EnvironmentItems.Add(new EnvItem(e));
        ProfileList.Clear();
        foreach (var p in Ws.Profiles) ProfileList.Add(new ProfileItem(p));
        OnPropertyChanged(nameof(HasProfileList));
    }

    private static void RefreshWindows(bool layout = false, bool motion = false, bool scale = false)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (motion && Avalonia.Application.Current is { } app) Motion.Apply(app);
            foreach (var w in AppHost.Windows.Windows)
            {
                if (layout && w.DataContext is MainWindowViewModel vm) vm.RefreshLayout();
                if (motion) w.ApplyMotion();
                if (scale) UiScaling.Apply(w);
            }
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ appearance

    partial void OnShowStatusBarChanged(bool value)
    {
        S.ShowStatusBar = value;
        Save();
        if (!_loading) RefreshWindows(layout: true);
    }

    partial void OnAnimationsChanged(bool value)
    {
        S.Animations = value;
        Save();
        NotifyPresets();
        if (!_loading) RefreshWindows(motion: true);
    }

    partial void OnAnimationSpeedStepChanged(double value)
    {
        OnPropertyChanged(nameof(AnimationSpeedText));
        if (_loading) return;
        S.AnimationSpeed = Motion.Speeds[Math.Clamp((int)Math.Round(value), 0, Motion.Speeds.Length - 1)];
        Save();
        NotifyPresets();
        RefreshWindows(motion: true);
    }

    private void NotifyPresets()
    {
        OnPropertyChanged(nameof(PresetPerformance));
        OnPropertyChanged(nameof(PresetBalanced));
        OnPropertyChanged(nameof(PresetSmooth));
        OnPropertyChanged(nameof(PresetFast));
    }

    /// <summary>Performance: no animations · Fast: 1.75× · Balanced: 1.0× · Smooth: 0.75× (a little longer and softer).</summary>
    [RelayCommand]
    private void AnimationPreset(string preset)
    {
        switch (preset)
        {
            case "performance":
                Animations = false;
                break;
            case "smooth":
                Animations = true;
                AnimationSpeedStep = Array.IndexOf(Motion.Speeds, 0.75);
                break;
            case "fast":
                Animations = true;
                AnimationSpeedStep = Array.IndexOf(Motion.Speeds, 1.75);
                break;
            default:
                Animations = true;
                AnimationSpeedStep = Array.IndexOf(Motion.Speeds, 1.0);
                break;
        }

        NotifyPresets();
    }

    partial void OnScaleIndexChanged(int value)
    {
        OnPropertyChanged(nameof(IsCustomScale));
        if (_loading || value < 0) return;
        S.UiScale = value < UiScaling.Presets.Length ? UiScaling.Presets[value].Value : CustomScale / 100.0;
        Save();
        RefreshWindows(scale: true);
    }

    partial void OnCustomScaleChanged(double value)
    {
        OnPropertyChanged(nameof(CustomScaleText));
        if (_loading || !IsCustomScale) return;
        S.UiScale = Math.Clamp(value, 75, 200) / 100.0;
        Save();
        RefreshWindows(scale: true);
    }

    [RelayCommand]
    private void ResetHints()
    {
        UiState.Instance.ResetDismissed();
        Owner?.ShowToast(Loc.T("Toast.HintsReset"));
    }

    // ------------------------------------------------------------------ behavior

    partial void OnAutoReconnectChanged(bool value)
    {
        S.AutoReconnect = value;
        Save();
    }

    partial void OnReconnectAttemptsChanged(decimal value)
    {
        S.ReconnectAttempts = (int)Math.Clamp(value, 1, 20);
        Save();
    }

    // ------------------------------------------------------------------ environments

    [RelayCommand]
    private async Task AddEnvironment()
    {
        if (Owner?.Window is not { } window) return;
        var r = await EnvironmentEditDialog.ShowAsync(window, null);
        if (r is null) return;
        var list = Ws.EnvironmentList.ToList();
        list.Add(new EnvironmentDef
        {
            Id = "e-" + Guid.NewGuid().ToString("N")[..8], Name = r.Value.Name, Short = r.Value.Short, Color = r.Value.Color,
            Order = list.Count == 0 ? 0 : list.Max(e => e.Order) + 1
        });
        Ws.SaveEnvironments(list);
        Refresh();
    }

    [RelayCommand]
    private async Task EditEnvironment(EnvItem item)
    {
        if (Owner?.Window is not { } window) return;
        var r = await EnvironmentEditDialog.ShowAsync(window, item.Env);
        if (r is null) return;
        Ws.SaveEnvironments(Ws.EnvironmentList.Select(e => e.Id == item.Env.Id
            ? new EnvironmentDef { Id = e.Id, Name = r.Value.Name, Short = r.Value.Short, Color = r.Value.Color, BuiltIn = e.BuiltIn, Order = e.Order }
            : e));
        Refresh();
    }

    [RelayCommand]
    private async Task DeleteEnvironment(EnvItem item)
    {
        if (Owner?.Window is not { } window) return;
        var message = Loc.F("Env.DeleteConfirm", item.Name) + (item.Usage > 0 ? "\n\n" + Loc.F("Env.DeleteUsed", item.Usage) : "");
        if (!await Dialog.ConfirmAsync(window, Loc.T("Env.Delete"), message, Loc.T("Env.Delete"), danger: true)) return;
        Ws.SaveEnvironments(Ws.EnvironmentList.Where(e => e.Id != item.Env.Id));
        Refresh();
    }

    [RelayCommand]
    private void MoveEnvironment(EnvItem item)
    {
        // Moves the environment one place up (menus and badges follow this order).
        var list = Ws.EnvironmentList.ToList();
        var i = list.FindIndex(e => e.Id == item.Env.Id);
        if (i <= 0) return;
        (list[i - 1], list[i]) = (list[i], list[i - 1]);
        for (var k = 0; k < list.Count; k++) list[k].Order = k;
        Ws.SaveEnvironments(list);
        Refresh();
    }

    [RelayCommand]
    private void RestoreEnvironments()
    {
        var list = Ws.EnvironmentList.ToList();
        foreach (var d in EnvironmentDef.Defaults.Where(d => list.All(e => e.Id != d.Id)))
        {
            list.Add(new EnvironmentDef { Id = d.Id, Name = d.Name, Short = d.Short, Color = d.Color, BuiltIn = true, Order = d.Order });
        }

        Ws.SaveEnvironments(list);
        Refresh();
    }

    // ------------------------------------------------------------------ profiles

    [RelayCommand]
    private Task NewProfile() => Owner?.Actions.EditProfileAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private Task EditProfile(ProfileItem item) => Owner?.Actions.EditProfileAsync(item.Profile) ?? Task.CompletedTask;

    [RelayCommand]
    private Task DeleteProfile(ProfileItem item) => Owner?.Actions.DeleteProfileAsync(item.Profile) ?? Task.CompletedTask;

    [RelayCommand]
    private Task StartProfile(ProfileItem item) => Owner?.Actions.OpenProfileAsync(item.Profile) ?? Task.CompletedTask;

    // ------------------------------------------------------------------ sample data and testing

    /// <summary>Sample data lives in its own data source, so removing it never touches real entries.</summary>
    [RelayCommand]
    private async Task AddSampleSource()
    {
        if (Owner?.Window is not { } window) return;
        var info = S.DataSources.FirstOrDefault(d => d.IsSample);
        var fresh = info is null;
        if (info is null)
        {
            info = DataSources.CreateInfo(Loc.T("Sample.SourceName"));
            info.IsSample = true;
            info.Icon = "flask-conical";
            info.ColorHue = 300;
        }

        try
        {
            if (Ws.Info?.Id != info.Id) await DataSources.OpenAsync(window, info, isNew: fresh);
            if (fresh || Ws.Tree.Count == 0) DemoData.Create(Ws);
            AppHost.Windows.RefreshSourceNames();
            Owner.ShowToast(Loc.T("Toast.SampleLoaded"));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Owner.ShowToast(ex.Message, true);
        }

        Refresh();
    }

    [RelayCommand]
    private async Task RemoveSampleSource()
    {
        if (Owner?.Window is not { } window || S.DataSources.FirstOrDefault(d => d.IsSample) is not { } info) return;
        if (!await Dialog.ConfirmAsync(window, Loc.T("Sample.Remove"), Loc.T("Sample.RemoveText"), Loc.T("Sample.Remove"), danger: true, icon: "flask-conical")) return;
        try
        {
            if (Ws.Info?.Id == info.Id)
            {
                var other = S.DataSources.FirstOrDefault(d => !d.IsSample && File.Exists(d.Path));
                var isNew = other is null;
                other ??= DataSources.CreateInfo("Default");
                await DataSources.OpenAsync(window, other, isNew);
            }

            DataSources.DeleteFile(info);
            AppHost.Windows.RefreshSourceNames();
            Owner.ShowToast(Loc.T("Toast.SampleRemoved"));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            Owner.ShowToast(ex.Message, true);
        }

        Refresh();
    }

    /// <summary>Window size and panel layout back to the defaults (tree width, rail, tree visible).</summary>
    [RelayCommand]
    private void ResetLayout()
    {
        S.TreeWidth = 300;
        S.TreeVisible = true;
        S.RailExpanded = false;
        S.Window = new WindowBounds();
        S.Save();
        foreach (var vm in AppHost.Windows.ViewModels)
        {
            vm.TreeVisible = true;
            vm.RailExpanded = false;
        }

        Owner?.ShowToast(Loc.T("Toast.LayoutReset"));
    }

    /// <summary>All preferences to defaults; data sources, device id and dismissed hints stay.</summary>
    [RelayCommand]
    private async Task ResetSettings()
    {
        if (Owner?.Window is not { } window) return;
        if (!await Dialog.ConfirmAsync(window, Loc.T("Testing.ResetSettings"), Loc.T("Testing.ResetSettingsText"), Loc.T("Testing.Reset"), danger: true)) return;
        var defaults = new AppSettings();
        S.ApplyPreferences(defaults);
        S.Language = Loc.Language;
        S.Save();
        UiState.Instance.ShowHints = S.ShowHints;
        Load();
        Dispatcher.UIThread.Post(() =>
        {
            AppHost.Theme.Apply();
            Ws.Rebuild();
            RefreshWindows(layout: true, motion: true, scale: true);
        }, DispatcherPriority.Background);
        Owner.ShowToast(Loc.T("Toast.SettingsReset"));
    }

    /// <summary>SQLite integrity check and VACUUM of the active data source.</summary>
    [RelayCommand]
    private async Task CheckDatabase()
    {
        if (Ws.Store is not { } store) return;
        Busy = true;
        try
        {
            var result = await Task.Run(() => store.CheckAndOptimize());
            Owner?.ShowToast(result == "ok" ? Loc.T("Testing.DbOk") : Loc.F("Testing.DbProblem", result), result != "ok");
        }
        catch (Microsoft.Data.Sqlite.SqliteException ex)
        {
            Owner?.ShowToast(ex.Message, true);
        }
        finally
        {
            Busy = false;
            Refresh();
        }
    }

    /// <summary>Text report for support: versions, OS, settings, data source statistics – no hosts, names or passwords.</summary>
    [RelayCommand]
    private async Task ExportDiagnostics()
    {
        if (Owner?.Window is not { } window) return;
        var file = await window.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = Loc.T("Testing.Diagnostics"),
            SuggestedFileName = $"nexode-diagnostics-{DateTime.Now:yyyyMMdd-HHmm}.txt",
            FileTypeChoices = [new FilePickerFileType("Text") { Patterns = ["*.txt"] }]
        });
        if (file?.TryGetLocalPath() is not { } path) return;
        await File.WriteAllTextAsync(path, Diagnostics.Build());
        Owner.ShowToast(Loc.F("Toast.Exported", Path.GetFileName(path)));
    }

    /// <summary>TCP reachability of host:port (no protocol handshake).</summary>
    [RelayCommand]
    private async Task TestConnection()
    {
        var host = TestHost.Trim();
        if (host.Length == 0) return;
        TestResult = Loc.F("Testing.Checking", host, (int)TestPort);
        OnPropertyChanged(nameof(HasTestResult));
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var tcp = new System.Net.Sockets.TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await tcp.ConnectAsync(host, (int)TestPort, cts.Token);
            TestOk = true;
            TestResult = Loc.F("Testing.Reachable", host, (int)TestPort, sw.ElapsedMilliseconds);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or OperationCanceledException or ArgumentException)
        {
            TestOk = false;
            TestResult = ex is OperationCanceledException ? Loc.F("Testing.Timeout", host, (int)TestPort) : Loc.F("Testing.Unreachable", host, (int)TestPort, ex.Message);
        }

        OnPropertyChanged(nameof(HasTestResult));
    }

    // ------------------------------------------------------------------ team data source

    /// <summary>Creates or opens a data source on a shared folder (team): local copy, one editor at a time, master password required.</summary>
    [RelayCommand]
    private async Task NewSharedSource()
    {
        if (Owner?.Window is not { } window) return;
        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("Shared.PickFolder") });
        if (folders.FirstOrDefault()?.TryGetLocalPath() is not { } folder) return;
        var name = await Dialog.InputAsync(window, Loc.T("Shared.New"), Loc.T("Field.Name"), Loc.T("Shared.DefaultName"), "users-round");
        if (string.IsNullOrWhiteSpace(name)) return;
        var slug = string.Concat(name.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-')).Trim('-');
        var path = Path.Combine(folder, (slug.Length == 0 ? "team" : slug) + ".db");
        var existing = S.DataSources.FirstOrDefault(d => string.Equals(Path.GetFullPath(d.Path), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase));
        var info = existing ?? new DataSourceInfo { Name = name.Trim(), Path = path, Icon = "users-round", Shared = true };
        info.Shared = true;
        try
        {
            if (await DataSources.OpenAsync(window, info, isNew: !File.Exists(path))) Owner.ShowToast(Loc.F("Toast.SourceOpened", info.Name));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            await Dialog.InfoAsync(window, Loc.T("Source.Open"), Loc.F("Source.OpenFailed", ex.Message), "circle-alert");
        }

        Refresh();
    }
}
