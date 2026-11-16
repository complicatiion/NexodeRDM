using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.Platform;

namespace Nexode.App.ViewModels;

/// <summary>One switch on the Features page.</summary>
public sealed partial class FeatureItem : ObservableObject
{
    private bool _syncing;

    public FeatureItem(FeatureDef def)
    {
        Def = def;
        _isOn = Features.IsSwitchedOn(def.Id);
    }

    public FeatureDef Def { get; }
    public string Icon => Def.Icon;
    public string Title => Loc.T("Feature." + Def.Id);
    public string Info => Loc.T("Feature." + Def.Id + ".Info");

    /// <summary>Switch is on but a required feature is off ("needs …").</summary>
    public bool IsBlocked => IsOn && Def.Requires is { } req && !Features.On(req);
    public string BlockedText => Def.Requires is { } req ? Loc.F("Feature.Needs", Loc.T("Feature." + req)) : "";

    [ObservableProperty]
    private bool _isOn;

    partial void OnIsOnChanged(bool value)
    {
        if (_syncing) return;
        Features.Set(Def.Id, value);
    }

    /// <summary>Reads the stored state again (after "Minimal"/"Defaults" or a change elsewhere).</summary>
    public void Sync()
    {
        _syncing = true;
        IsOn = Features.IsSwitchedOn(Def.Id);
        _syncing = false;
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(BlockedText));
    }

    public void Relocalize()
    {
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(Info));
        OnPropertyChanged(nameof(BlockedText));
    }
}

/// <summary>Card on the Features page.</summary>
public sealed partial class FeatureGroup(string id, string icon, IReadOnlyList<FeatureItem> items) : ObservableObject
{
    public string Id { get; } = id;
    public string Icon { get; } = icon;
    public string Title => Loc.T("Feature.Group." + Id);
    public IReadOnlyList<FeatureItem> Items { get; } = items;
    public void Relocalize() => OnPropertyChanged(nameof(Title));
}

/// <summary>One line of the diagnostics status card.</summary>
/// <param name="State">"ok", "warning" or "" (neutral).</param>
public sealed record DiagItem(string Label, string Icon, string Value, string State)
{
    public bool IsOk => State == "ok";
    public bool IsWarning => State == "warning";
}

/// <summary>v0.0.6 settings: Features page (optional features on/off), diagnostics page.</summary>
public sealed partial class SettingsTabViewModel
{
    private static readonly (string Id, string Icon)[] FeatureGroupOrder =
        [("sessions", "plug-zap"), ("tools", "wrench"), ("view", "app-window"), ("data", "database")];

    private IReadOnlyList<FeatureGroup>? _featureGroups;

    public IReadOnlyList<FeatureGroup> FeatureGroups => _featureGroups ??= FeatureGroupOrder
        .Select(g => new FeatureGroup(g.Id, g.Icon, Features.All.Where(f => f.Group == g.Id).Select(f => new FeatureItem(f)).ToList()))
        .ToList();

    public int FeaturesOnCount => Features.All.Count(f => Features.On(f.Id));
    public string FeaturesSummary => Loc.F("Feature.Summary", FeaturesOnCount, Features.All.Count);
    public bool ShowDashboardPage => Features.On(Features.Monitoring);
    public bool TeamEnabled => Features.On(Features.Team);
    public bool KeyToolsEnabled => Features.On(Features.SshKeys);
    public bool ShowMacFileTypeHint => !FileAssociation.IsSupported;

    private void InitFeatures() => Features.Changed += OnFeaturesChanged;

    private void CloseFeatures() => Features.Changed -= OnFeaturesChanged;

    private void OnFeaturesChanged()
    {
        foreach (var item in FeatureGroups.SelectMany(g => g.Items)) item.Sync();
        OnPropertyChanged(nameof(FeaturesOnCount));
        OnPropertyChanged(nameof(FeaturesSummary));
        OnPropertyChanged(nameof(ShowDashboardPage));
        OnPropertyChanged(nameof(TeamEnabled));
        OnPropertyChanged(nameof(KeyToolsEnabled));
    }

    private void RelocalizeFeatures()
    {
        foreach (var g in FeatureGroups)
        {
            g.Relocalize();
            foreach (var i in g.Items) i.Relocalize();
        }

        OnPropertyChanged(nameof(FeaturesSummary));
    }

    // ------------------------------------------------------------------ trusted host keys

    [ObservableProperty]
    private string _knownHostFilter = "";

    public System.Collections.ObjectModel.ObservableCollection<KnownHostItem> VisibleKnownHosts { get; } = [];
    public bool HasUnusedHosts => KnownHosts.Any(h => h.IsUnused);
    public string KnownHostSummary => Loc.F("Hosts.Summary", KnownHosts.Count, KnownHosts.Count(h => h.IsUnused));

    partial void OnKnownHostFilterChanged(string value) => FilterKnownHosts();

    private void FilterKnownHosts()
    {
        VisibleKnownHosts.Clear();
        var f = KnownHostFilter.Trim();
        foreach (var h in KnownHosts.Where(h => f.Length == 0 || h.Key.Contains(f, StringComparison.OrdinalIgnoreCase) || h.Fingerprint.Contains(f, StringComparison.OrdinalIgnoreCase)))
            VisibleKnownHosts.Add(h);
        OnPropertyChanged(nameof(HasUnusedHosts));
        OnPropertyChanged(nameof(KnownHostSummary));
    }

    /// <summary>Host names (lower case) of all entries of the active source and how many entries use each.</summary>
    private static Dictionary<string, int> HostUsage()
    {
        var ws = AppHost.Workspace;
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        if (ws.Store is null || ws.IsLocked) return result;
        foreach (var e in ws.Tree.All)
        {
            var eff = ws.Resolver.Resolve(e);
            var host = eff.Host ?? (Uri.TryCreate(eff.Url, UriKind.Absolute, out var u) ? u.Host : null);
            if (string.IsNullOrWhiteSpace(host)) continue;
            host = host.Trim().ToLowerInvariant();
            result[host] = result.GetValueOrDefault(host) + 1;
        }

        return result;
    }

    [RelayCommand]
    private async Task CopyFingerprint(KnownHostItem item)
    {
        if (Owner?.Window?.Clipboard is not { } cb) return;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, item.Fingerprint);
        Owner?.ShowToast(Loc.T("Hosts.Copied"));
    }

    /// <summary>Forgets the identities no entry of the active data source points to any more.</summary>
    [RelayCommand]
    private async Task ForgetUnusedHosts()
    {
        if (Owner?.Window is not { } w) return;
        var unused = KnownHosts.Where(h => h.IsUnused).Select(h => h.Key).ToList();
        if (unused.Count == 0) return;
        if (!await Dialog.ConfirmAsync(w, Loc.T("Security.KnownHosts"), Loc.F("Hosts.ForgetUnusedText", unused.Count), Loc.T("Hosts.ForgetUnused"), danger: true, icon: "trash-2")) return;
        AppHost.KnownHosts.Remove(unused);
        Refresh();
    }

    [RelayCommand]
    private async Task ForgetAllHosts()
    {
        if (Owner?.Window is not { } w || KnownHosts.Count == 0) return;
        if (!await Dialog.ConfirmAsync(w, Loc.T("Security.KnownHosts"), Loc.F("Hosts.ForgetAllText", KnownHosts.Count), Loc.T("Hosts.ForgetAll"), danger: true, icon: "trash-2")) return;
        AppHost.KnownHosts.Remove(KnownHosts.Select(h => h.Key).ToList());
        Refresh();
    }

    /// <summary>SSH key tools (Settings → Security).</summary>
    [RelayCommand]
    private async Task OpenKeyTools()
    {
        if (Owner is { } vm) await SshKeyDialog.ShowAsync(vm);
    }

    // ------------------------------------------------------------------ diagnostics page

    public const int DiagnosticsPage = 12;

    [ObservableProperty]
    private IReadOnlyList<DiagItem> _diagItems = [];

    [ObservableProperty]
    private string _logTail = "";

    public string LogFile => Path.Combine(AppPaths.LogDirectory, "nexode.log");

    partial void OnPageIndexChanged(int value)
    {
        if (value == DiagnosticsPage) RefreshDiagnostics();
    }

    /// <summary>What Nexode found on this computer (no hosts, names or passwords): for checking a new installation.</summary>
    [RelayCommand]
    private void RefreshDiagnostics()
    {
        var items = new List<DiagItem>();
        void Add(string key, string icon, string value, string state = "") => items.Add(new DiagItem(Loc.T(key), icon, value, state));
        string Yes(bool b) => Loc.T(b ? "Diag.Yes" : "Diag.No");

        Add("Diag.Version", "info", $"Nexode RDM {AppHost.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        Add("Diag.Runtime", "cpu", System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        Add("Diag.Portable", "usb", AppPaths.IsPortable ? Loc.T("Diag.PortableOn") : Loc.T("Diag.PortableOff"), AppPaths.IsPortable ? "ok" : "");
        Add("Diag.DataFolder", "folder", AppPaths.DataDirectory);

        var theme = AppHost.Theme;
        Add("Diag.Theme", "palette", $"{Loc.T("Theme." + S.Theme)} · {(theme.IsDark ? Loc.T("Theme.Dark") : Loc.T("Theme.Light"))} · {theme.EffectiveAccent}");
        if (OperatingSystem.IsWindows())
        {
            var mica = !Backdrop.MicaSupported ? (Loc.T("Diag.MicaUnsupported"), "warning")
                : theme.Variant == Core.Theming.PaletteVariant.Mica ? (Loc.T("Diag.MicaActive"), "ok") : (Loc.T("Diag.MicaReady"), "");
            Add("Diag.Mica", "app-window", mica.Item1, mica.Item2);
        }

        var accent = ThemeService.SystemAccent();
        Add("Diag.SystemAccent", "droplet", accent ?? Loc.T("Diag.NotReported"), accent is null ? "" : "ok");

        if (Features.On(Features.SshAgent))
        {
            var agent = Nexode.Protocols.SshAgent.Describe();
            Add("Diag.SshAgent", "key-square", agent ?? Loc.T("Diag.NoAgent"), agent is null ? "warning" : "ok");
        }

        Add("Diag.Rdp", "monitor", RdpClient(out var rdpOk), rdpOk ? "ok" : "warning");
        if (Features.On(Features.PowerShell))
        {
            var ps = PowerShellPaths();
            Add("Diag.PowerShell", "code-xml", ps ?? Loc.T(OperatingSystem.IsWindows() ? "Ps.NoPowerShell" : "Ps.NoPwsh"), ps is null ? "warning" : "ok");
        }

        if (Features.On(Features.Web)) Add("Diag.WebEngine", "globe", WebEngineText, WebEngineOk ? "ok" : "warning");
        if (Features.On(Features.Serial)) Add("Diag.Serial", "cable", Nexode.Protocols.Terminal.SerialConnection.ListPorts().Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        Add("Diag.LiveStatus", "activity", LiveStatus.IsActive
            ? Loc.F("Diag.LiveStatusOn", LiveStatus.LastRunUtc?.ToLocalTime().ToString("T", System.Globalization.CultureInfo.CurrentCulture) ?? "–")
            : Loc.T("Diag.Off"), LiveStatus.IsActive ? "ok" : "");
        Add("Diag.KnownHosts", "shield-check", AppHost.KnownHosts.All().Count.ToString(System.Globalization.CultureInfo.CurrentCulture));
        Add("Diag.Autostart", "rocket", Yes(Platform.Autostart.IsSupported && Platform.Autostart.IsEnabled()));
        DiagItems = items;
        LogTail = ReadLogTail(60);
    }

    private static string RdpClient(out bool ok)
    {
        ok = true;
        if (OperatingSystem.IsWindows())
        {
            var mstsc = Path.Combine(Environment.SystemDirectory, "mstsc.exe");
            var ax = File.Exists(Path.Combine(Environment.SystemDirectory, "mstscax.dll"));
            ok = ax || File.Exists(mstsc);
            return (ax ? Loc.T("Diag.RdpEmbedded") : Loc.T("Diag.RdpNoEmbedded")) + (File.Exists(mstsc) ? " · mstsc.exe" : "");
        }

        if (OperatingSystem.IsLinux())
        {
            var exe = ExternalLauncher.Which("xfreerdp3") ?? ExternalLauncher.Which("xfreerdp") ?? ExternalLauncher.Which("wlfreerdp") ?? ExternalLauncher.Which("sdl-freerdp");
            ok = exe is not null;
            return exe is null ? Loc.T("Session.NoFreeRdp") : $"FreeRDP {ExternalLauncher.FreeRdpMajor(exe)} · {exe}";
        }

        return Loc.T("Diag.RdpMac");
    }

    private static string? PowerShellPaths()
    {
        if (!OperatingSystem.IsWindows()) return ExternalLauncher.Which("pwsh");
        var list = new List<string>();
        var winPs = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");
        if (File.Exists(winPs)) list.Add("Windows PowerShell");
        if (ExternalLauncher.Which("pwsh.exe") is { } pwsh) list.Add("PowerShell 7 (" + pwsh + ")");
        return list.Count == 0 ? null : string.Join(" · ", list);
    }

    /// <summary>Last lines of the technical log (it never contains secrets).</summary>
    private string ReadLogTail(int lines)
    {
        try
        {
            if (!File.Exists(LogFile)) return Loc.T("Diag.LogEmpty");
            using var stream = new FileStream(LogFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var all = reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
            return all.Length == 0 ? Loc.T("Diag.LogEmpty") : string.Join('\n', all.TakeLast(lines).Select(l => l.TrimEnd('\r')));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return ex.Message;
        }
    }

    [RelayCommand]
    private void OpenLogFolder() => Shell.Open(AppPaths.LogDirectory);

    [RelayCommand]
    private async Task CopyDiagnostics()
    {
        if (Owner?.Window?.Clipboard is not { } clipboard) return;
        var text = Diagnostics.Build() + "\n" + string.Join("\n", DiagItems.Select(d => $"{d.Label}: {d.Value}"));
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(clipboard, text);
        Owner?.ShowToast(Loc.T("Diag.Copied"));
    }

    /// <summary>"Minimal": only the core (RDP, SSH, file transfer) plus split view and command palette.</summary>
    [RelayCommand]
    private async Task FeaturesMinimal()
    {
        if (Owner?.Window is not { } w) return;
        if (!await Dialog.ConfirmAsync(w, Loc.T("Feature.MinimalTitle"), Loc.T("Feature.MinimalText"), Loc.T("Feature.Minimal"), icon: "toggle-left")) return;
        Features.SetAll(f => Features.Minimal.Contains(f.Id));
        Owner?.ShowToast(Loc.T("Feature.MinimalDone"));
    }

    [RelayCommand]
    private async Task FeaturesDefaults()
    {
        if (Owner?.Window is not { } w) return;
        if (!await Dialog.ConfirmAsync(w, Loc.T("Feature.DefaultsTitle"), Loc.T("Feature.DefaultsText"), Loc.T("Feature.Defaults"), icon: "rotate-ccw")) return;
        Features.SetAll(f => f.DefaultOn);
        Owner?.ShowToast(Loc.T("Feature.DefaultsDone"));
    }
}
