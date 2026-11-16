using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Core.Theming;

namespace Nexode.App.ViewModels;

/// <summary>Display part of an option (for templates).</summary>
public interface IOption
{
    string Text { get; }
    string? Icon { get; }
}

public sealed record Option<T>(T Value, string Text, string? Icon = null) : IOption
{
    public override string ToString() => Text;
}

public sealed partial class HueSwatch(double? hue, IBrush brush, string tip) : ObservableObject
{
    public double? Hue { get; } = hue;
    public IBrush Brush { get; } = brush;
    public string Tip { get; } = tip;
    public bool IsNone => Hue is null;

    [ObservableProperty]
    private bool _isSelected;
}

/// <summary>Editor for one entry: general, credentials, protocol options, appearance, notes.</summary>
public sealed partial class EntryEditorViewModel : ObservableObject
{
    private readonly Entry _entry;
    private readonly bool _hadPassword;

    public EntryEditorViewModel(Entry entry, bool isNew)
    {
        _entry = entry;
        IsNew = isNew;
        var ws = AppHost.Workspace;

        Kinds = Features.CreatableKinds(isNew ? null : entry.Kind).Select(k => new Option<EntryKind>(k, Loc.Kind(k), EntryKinds.Icon(k))).ToList();
        Categories = [new Option<string?>(null, Loc.T("Category.None"), "circle-x"), .. ws.CategoryList.Select(c => new Option<string?>(c.Id, CategoryNames.Of(c), c.Icon))];
        Environments =
        [
            new(null, Loc.T("Env.Inherit"), "arrow-up"), new("", Loc.T("Env.None"), "circle-x"),
            .. ws.EnvironmentList.Select(e => new Option<string?>(e.Id, Envs.Name(e), "flag"))
        ];
        OsList = Enum.GetValues<OsFamily>().Select(o => new Option<OsFamily>(o, Loc.T("Os." + o), EntryKinds.OsIcon(o) is { Length: > 0 } i ? i : "circle-help")).ToList();
        CredentialModes = Enum.GetValues<CredentialMode>().Select(m => new Option<CredentialMode>(m, Loc.T("Cred." + m))).ToList();
        Profiles = ws.Tree.All.Where(e => e.Kind == EntryKind.Credential && e.Id != entry.Id).OrderBy(e => e.Name)
            .Select(e => new Option<Guid>(e.Id, string.IsNullOrEmpty(e.Domain) ? $"{e.Name} ({e.Username})" : $@"{e.Name} ({e.Domain}\{e.Username})", "key-round")).ToList();
        FtpModes = Enum.GetValues<FtpEncryption>().Select(f => new Option<FtpEncryption>(f, Loc.T("Ftp." + f))).ToList();
        Resolutions = [Loc.T("Rdp.Fit"), "1920x1080", "1680x1050", "1600x900", "1440x900", "1366x768", "1280x1024", "1280x800", "1024x768"];
        DbEngines = ["", "Microsoft SQL Server", "MySQL / MariaDB", "PostgreSQL", "Oracle", "SQLite", "MongoDB", "Redis", "Other"];

        var theme = AppHost.Theme;
        Swatches = [.. FolderColors.Swatches.Select(h => new HueSwatch(h, theme.FolderBrush(h), Loc.T("Color.H" + (int)h)))];
        ColorDepths = ["32", "24", "16", "15"];
        AudioModes = [Loc.T("Rdp.AudioLocal"), Loc.T("Rdp.AudioRemote"), Loc.T("Rdp.AudioOff")];

        _kind = Kinds.First(k => k.Value == entry.Kind);
        _name = entry.Name;
        _host = entry.Host ?? "";
        _port = entry.Port?.ToString(CultureInfo.InvariantCulture) ?? "";
        _url = entry.Url ?? "";
        _description = entry.Description ?? "";
        _notes = entry.Notes ?? "";
        _tags = string.Join(", ", entry.Tags);
        _category = Categories.FirstOrDefault(c => c.Value == entry.CategoryId) ?? Categories[0];
        _environment = Environments.FirstOrDefault(e => e.Value == entry.EnvironmentId) ?? Environments[0];
        _os = OsList.First(o => o.Value == entry.Os);
        _credentialMode = CredentialModes.First(m => m.Value == entry.CredentialMode);
        _username = entry.Username ?? "";
        _domain = entry.Domain ?? "";
        _profile = Profiles.FirstOrDefault(p => p.Value == entry.CredentialId);
        _icon = entry.Icon;
        _isFavorite = entry.IsFavorite;
        _colorHue = entry.ColorHue;
        _ip = entry.Setting(SettingKeys.IpAddress) ?? "";
        _mac = entry.Setting(SettingKeys.MacAddress) ?? "";
        _wolBroadcast = entry.Setting(SettingKeys.WolBroadcast) ?? "";
        _wolPort = entry.Setting(SettingKeys.WolPort) ?? "";
        _serialPort = entry.Setting(SettingKeys.SerialPort) ?? "";
        _serialBaud = entry.Setting(SettingKeys.SerialBaud) ?? "9600";
        _serialDataBits = entry.Setting(SettingKeys.SerialDataBits) ?? "8";
        _serialParityIndex = Math.Max(0, Array.IndexOf(ParityCodes, entry.Setting(SettingKeys.SerialParity) ?? "N"));
        _serialStopBits = entry.Setting(SettingKeys.SerialStopBits) ?? "1";
        _serialFlowIndex = Math.Max(0, Array.IndexOf(FlowCodes, entry.Setting(SettingKeys.SerialFlow) ?? "none"));
        _serialNewLineIndex = Math.Max(0, Array.IndexOf(NewLineCodes, entry.Setting(SettingKeys.SerialNewLine) ?? "cr"));
        _serialEcho = entry.Setting(SettingKeys.SerialEcho) == "1";
        _reconnectIndex = entry.Setting(SettingKeys.AutoReconnect) switch { "1" => 1, "0" => 2, _ => 0 };
        _resolution = entry.Setting(SettingKeys.RdpResolution) is { } r && r.Contains('x') ? r : Loc.T("Rdp.Fit");
        _rdpAdmin = entry.Setting(SettingKeys.RdpAdmin) is "1" or "true";
        _rdpClipboard = entry.Setting(SettingKeys.RdpClipboard) != "0";
        _rdpDrives = entry.Setting(SettingKeys.RdpDrives) is "1" or "true";
        _rdpPrinters = entry.Setting(SettingKeys.RdpPrinters) is "1" or "true";
        _rdpSmartSizing = entry.Setting(SettingKeys.RdpSmartSizing) != "0";
        _colorDepth = entry.Setting(SettingKeys.RdpColorDepth) ?? "32";
        _audioIndex = int.TryParse(entry.Setting(SettingKeys.RdpAudio), out var am) ? Math.Clamp(am, 0, 2) : 0;
        _terminalType = entry.Setting(SettingKeys.SshTerminalType) ?? "";
        _startCommand = entry.Setting(SettingKeys.SshStartCommand) ?? "";
        _autoLogin = entry.Setting(SettingKeys.TelnetAutoLogin) is "1" or "true";
        _vncScale = entry.Setting(SettingKeys.VncScale) != "0";
        _vncSecurityIndex = Math.Max(0, Array.IndexOf(VncSecurityKeys, entry.Setting(SettingKeys.VncSecurity) ?? "auto"));
        _vncQualityIndex = Math.Max(0, Array.IndexOf(VncQualityKeys, entry.Setting(SettingKeys.VncQuality) ?? "balanced"));
        _gateway = entry.Setting(SettingKeys.RdpGateway) ?? "";
        _keyFile = entry.Setting(SettingKeys.SshKeyFile) ?? "";
        _sshAgent = entry.Setting(SettingKeys.SshAgent) == "1";
        _remotePath = entry.Setting(SettingKeys.RemotePath) ?? "";
        _ftpMode = FtpModes.FirstOrDefault(f => f.Value.ToString() == entry.Setting(SettingKeys.FtpEncryption)) ?? FtpModes[1];
        _ftpPassive = entry.Setting(SettingKeys.FtpPassive) != "false";
        _vncViewOnly = entry.Setting(SettingKeys.VncViewOnly) is "1" or "true";
        _commandLine = entry.Setting(SettingKeys.CommandLine) ?? "";
        _commandArgs = entry.Setting(SettingKeys.CommandArgs) ?? "";
        _dbEngine = entry.Setting(SettingKeys.DbEngine) ?? "";
        _dbName = entry.Setting(SettingKeys.DbName) ?? "";
        _hadPassword = ws.HasSecret(entry.Id);
        JumpHosts =
        [
            new(null, Loc.T("Tunnel.Direct"), "plug"),
            .. ws.Tree.All.Where(e => e.Kind == EntryKind.Ssh && e.Id != entry.Id).OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(e => new Option<Guid?>(e.Id, $"{e.Name} ({ws.Resolver.Resolve(e).Host})", "waypoints"))
        ];
        _jumpHost = JumpHosts.FirstOrDefault(j => j.Value is { } id && id.ToString() == entry.Setting(SettingKeys.TunnelVia)) ?? JumpHosts[0];
        _webIgnoreCert = entry.Setting(SettingKeys.WebIgnoreCert) == "1";
        _webBasicAuth = entry.Setting(SettingKeys.WebBasicAuth) == "1";
        _webFormFill = entry.Setting(SettingKeys.WebFormFill) == "1";
        _webUserSelector = entry.Setting(SettingKeys.WebUserSelector) ?? "";
        _webPassSelector = entry.Setting(SettingKeys.WebPassSelector) ?? "";
        _webExternal = entry.Setting(SettingKeys.WebExternal) == "1";
        _s3Bucket = entry.Setting(SettingKeys.S3Bucket) ?? "";
        _s3Region = entry.Setting(SettingKeys.S3Region) ?? "";
        _s3StyleIndex = entry.Setting(SettingKeys.S3PathStyle) switch { "1" => 1, "0" => 2, _ => 0 };
        _useTls = entry.Setting(SettingKeys.UseTls) != "0";
        Monitors = [Loc.T("Rdp.MonitorCurrent"), .. Enumerable.Range(1, Math.Max(1, MonitorCount())).Select(i => Loc.F("Rdp.MonitorN", i))];
        _rdpMonitorIndex = int.TryParse(entry.Setting(SettingKeys.RdpMonitor), out var mon) && mon > 0 && mon < Monitors.Count ? mon : 0;
        _rdpFullscreen = entry.Setting(SettingKeys.RdpFullscreen) == "1";
        _rdpSmartCards = entry.Setting(SettingKeys.RdpSmartCards) == "1";
        _rdpSpan = entry.Setting(SettingKeys.RdpSpan) == "1";
        var chosenMonitors = (entry.Setting(SettingKeys.RdpMonitors) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToHashSet();
        SpanMonitors = Enumerable.Range(0, Math.Max(2, MonitorCount()))
            .Select(i => new MonitorChoice(i, Loc.F("Rdp.MonitorN", i + 1), chosenMonitors.Count == 0 || chosenMonitors.Contains(i.ToString(CultureInfo.InvariantCulture))))
            .ToList();
        _gatewayBypass = entry.Setting(SettingKeys.RdpGatewayBypass) == "1";
        _gatewaySameCreds = entry.Setting(SettingKeys.RdpGatewaySameCreds) != "0";
        _hyperVId = entry.Setting(SettingKeys.RdpHyperVId) ?? "";
        _remoteApp = entry.Setting(SettingKeys.RdpRemoteApp) ?? "";
        _remoteAppArgs = entry.Setting(SettingKeys.RdpRemoteAppArgs) ?? "";
        _rdpClipboardFiles = entry.Setting(SettingKeys.RdpClipboardFiles) != "0";
        _psTransportIndex = Array.IndexOf(PsTransportKeys, entry.Setting(SettingKeys.PsTransport) ?? "winrm") is var ti and >= 0 ? ti : 0;
        _psAuthIndex = Array.IndexOf(PsAuthKeys, entry.Setting(SettingKeys.PsAuth) ?? "Default") is var ai and >= 0 ? ai : 0;
        _psConfiguration = entry.Setting(SettingKeys.PsConfiguration) ?? "";
        _psPwsh = entry.Setting(SettingKeys.PsPwsh) == "1";
        _psSkipCertCheck = entry.Setting(SettingKeys.PsSkipCertCheck) == "1";
        if (isNew)
        {
            // New entries: random preset color unless they sit in a colored folder (then they inherit it).
            var parentHue = ws.Tree.Get(entry.ParentId) is { } parent ? ws.Resolver.Resolve(parent).ColorHue : null;
            _colorHue = null;
            _randomColor = parentHue is null;
        }

        UpdateSwatches();
    }

    /// <summary>"Random" swatch: a preset is picked on save, different from the colors of the neighbors.</summary>
    [ObservableProperty] private bool _randomColor;

    partial void OnRandomColorChanged(bool value)
    {
        if (value) ColorHue = null;
        OnPropertyChanged(nameof(NoColor));
    }

    /// <summary>Random preset that no sibling uses yet (or at least not the last sibling's color).</summary>
    public static double PickRandomHue(IEnumerable<double> siblingHues)
    {
        var used = siblingHues.ToList();
        var free = FolderColors.Swatches.Where(h => used.All(u => FolderColors.Distance(u, h) >= 1)).ToList();
        if (free.Count == 0) free = FolderColors.Swatches.Where(h => used.Count == 0 || FolderColors.Distance(used[^1], h) >= 1).ToList();
        return free[Random.Shared.Next(free.Count)];
    }

    public bool IsNew { get; }
    public Guid EntryId => _entry.Id;
    public string Title => IsNew ? Loc.F("Editor.NewTitle", Loc.Kind(Kind.Value)) : Loc.F("Editor.EditTitle", _entry.Name);
    public IReadOnlyList<Option<EntryKind>> Kinds { get; }
    public IReadOnlyList<Option<string?>> Categories { get; }
    public IReadOnlyList<Option<string?>> Environments { get; }
    public IReadOnlyList<Option<OsFamily>> OsList { get; }
    public IReadOnlyList<Option<CredentialMode>> CredentialModes { get; }
    public IReadOnlyList<Option<Guid>> Profiles { get; }
    public IReadOnlyList<Option<FtpEncryption>> FtpModes { get; }
    public IReadOnlyList<string> Resolutions { get; }
    public IReadOnlyList<string> DbEngines { get; }
    public IReadOnlyList<HueSwatch> Swatches { get; }
    public IReadOnlyList<string> ColorDepths { get; }
    public IReadOnlyList<string> AudioModes { get; }

    [ObservableProperty] private Option<EntryKind> _kind;
    [ObservableProperty] private string _name;
    [ObservableProperty] private string _host;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _url;
    [ObservableProperty] private string _description;
    [ObservableProperty] private string _notes;
    [ObservableProperty] private string _tags;
    [ObservableProperty] private Option<string?> _category;
    [ObservableProperty] private Option<string?> _environment;
    [ObservableProperty] private Option<OsFamily> _os;
    [ObservableProperty] private Option<CredentialMode> _credentialMode;
    [ObservableProperty] private string _username;
    [ObservableProperty] private string _domain;
    [ObservableProperty] private Option<Guid>? _profile;
    [ObservableProperty] private string _password = "";
    [ObservableProperty] private bool _passwordChanged;
    [ObservableProperty] private string? _icon;
    [ObservableProperty] private bool _isFavorite;
    [ObservableProperty] private double? _colorHue;
    [ObservableProperty] private string _ip;
    [ObservableProperty] private string _mac;
    [ObservableProperty] private string _resolution;
    [ObservableProperty] private bool _rdpAdmin;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowClipboardFiles))] private bool _rdpClipboard;
    [ObservableProperty] private bool _rdpClipboardFiles;
    [ObservableProperty] private string _remoteApp;
    [ObservableProperty] private string _remoteAppArgs;
    public bool ShowClipboardFiles => RdpClipboard;

    // ---- PowerShell
    public static readonly string[] PsTransportKeys = ["winrm", "winrm-ssl", "ssh"];
    public static readonly string[] PsAuthKeys = ["Default", "Negotiate", "Kerberos", "CredSSP"];
    public IReadOnlyList<string> PsTransports { get; } = [Loc.T("Ps.WinRm"), Loc.T("Ps.WinRmSsl"), Loc.T("Ps.Ssh")];
    public IReadOnlyList<string> PsAuths { get; } = [Loc.T("Ps.AuthDefault"), "Negotiate", "Kerberos", "CredSSP"];
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsPsWinRm), nameof(IsPsSsl))] private int _psTransportIndex;
    [ObservableProperty] private int _psAuthIndex;
    [ObservableProperty] private string _psConfiguration;
    [ObservableProperty] private bool _psPwsh;
    [ObservableProperty] private bool _psSkipCertCheck;
    public bool IsPowerShell => K == EntryKind.PowerShell;
    public bool IsPsWinRm => PsTransportIndex < 2;
    public bool IsPsSsl => PsTransportIndex == 1;
    public bool IsWindowsHost => OperatingSystem.IsWindows();

    partial void OnPsTransportIndexChanged(int oldValue, int newValue)
    {
        // Follow the default port of the transport unless the user typed another one.
        int[] ports = [5985, 5986, 22];
        if (Port.Trim().Length == 0 || Port.Trim() == ports[Math.Clamp(oldValue, 0, 2)].ToString(CultureInfo.InvariantCulture))
            Port = newValue == 0 ? "" : ports[Math.Clamp(newValue, 0, 2)].ToString(CultureInfo.InvariantCulture);
    }
    [ObservableProperty] private bool _rdpDrives;
    [ObservableProperty] private bool _rdpPrinters;
    [ObservableProperty] private bool _rdpSmartSizing;
    [ObservableProperty] private string _colorDepth;
    [ObservableProperty] private int _audioIndex;
    [ObservableProperty] private string _terminalType;
    [ObservableProperty] private string _startCommand;
    [ObservableProperty] private bool _autoLogin;
    [ObservableProperty] private bool _vncScale;
    public static readonly string[] VncSecurityKeys = ["auto", "vnc", "vencrypt", "ard", "none"];
    public static readonly string[] VncQualityKeys = ["balanced", "lan", "low"];
    public IReadOnlyList<string> VncSecurities { get; } = VncSecurityKeys.Select(k => Loc.T("Vnc.Sec." + k)).ToList();
    public IReadOnlyList<string> VncQualities { get; } = VncQualityKeys.Select(k => Loc.T("Vnc.Q." + k)).ToList();
    [ObservableProperty] private int _vncSecurityIndex;
    [ObservableProperty] private int _vncQualityIndex;
    [ObservableProperty] private string _gateway;
    [ObservableProperty] private string _keyFile;
    [ObservableProperty] private string _remotePath;
    [ObservableProperty] private Option<FtpEncryption> _ftpMode;
    [ObservableProperty] private bool _ftpPassive;
    [ObservableProperty] private bool _vncViewOnly;
    [ObservableProperty] private string _commandLine;
    [ObservableProperty] private string _commandArgs;
    [ObservableProperty] private string _dbEngine;
    [ObservableProperty] private string _dbName;
    [ObservableProperty] private string? _error;

    public EntryKind K => Kind.Value;

    private static readonly string[] ParityCodes = ["N", "E", "O", "M", "S"];
    private static readonly string[] FlowCodes = ["none", "rts", "xon"];
    private static readonly string[] NewLineCodes = ["cr", "crlf", "lf"];
    public IReadOnlyList<string> BaudRates { get; } = [.. Nexode.Protocols.Terminal.SerialSettings.BaudRates.Select(b => b.ToString(CultureInfo.InvariantCulture))];
    public IReadOnlyList<string> DataBitsList { get; } = ["8", "7", "6", "5"];
    public IReadOnlyList<string> StopBitsList { get; } = ["1", "2"];
    public IReadOnlyList<string> ParityList { get; } = [Loc.T("Serial.ParityNone"), Loc.T("Serial.ParityEven"), Loc.T("Serial.ParityOdd"), Loc.T("Serial.ParityMark"), Loc.T("Serial.ParitySpace")];
    public IReadOnlyList<string> FlowList { get; } = [Loc.T("Serial.FlowNone"), "RTS/CTS", "XON/XOFF"];
    public IReadOnlyList<string> NewLineList { get; } = ["CR", "CR LF", "LF"];
    public IReadOnlyList<string> ReconnectModes { get; } = [Loc.T("Editor.ReconnectDefault"), Loc.T("Editor.ReconnectOn"), Loc.T("Editor.ReconnectOff")];
    public IReadOnlyList<string> SerialPorts => Nexode.Protocols.Terminal.SerialConnection.ListPorts();

    [ObservableProperty] private string _wolBroadcast = "";
    [ObservableProperty] private string _wolPort = "";
    [ObservableProperty] private string _serialPort = "";
    [ObservableProperty] private string _serialBaud = "9600";
    [ObservableProperty] private string _serialDataBits = "8";
    [ObservableProperty] private int _serialParityIndex;
    [ObservableProperty] private string _serialStopBits = "1";
    [ObservableProperty] private int _serialFlowIndex;
    [ObservableProperty] private int _serialNewLineIndex;
    [ObservableProperty] private bool _serialEcho;
    [ObservableProperty] private int _reconnectIndex;

    public IReadOnlyList<Option<Guid?>> JumpHosts { get; }
    public IReadOnlyList<string> S3Styles { get; } = [Loc.T("S3.StyleAuto"), Loc.T("S3.StylePath"), Loc.T("S3.StyleVirtual")];
    public IReadOnlyList<string> Monitors { get; }

    /// <summary>"Use all monitors": which ones (numbering of mstsc /l, from 0).</summary>
    public IReadOnlyList<MonitorChoice> SpanMonitors { get; }
    [ObservableProperty] private Option<Guid?> _jumpHost;
    [ObservableProperty] private bool _webIgnoreCert;
    [ObservableProperty] private bool _webBasicAuth;
    [ObservableProperty] private bool _webFormFill;
    [ObservableProperty] private string _webUserSelector = "";
    [ObservableProperty] private string _webPassSelector = "";
    [ObservableProperty] private bool _webExternal;
    [ObservableProperty] private string _s3Bucket = "";
    [ObservableProperty] private string _s3Region = "";
    [ObservableProperty] private int _s3StyleIndex;
    [ObservableProperty] private bool _useTls = true;
    [ObservableProperty] private int _rdpMonitorIndex;
    [ObservableProperty] private bool _rdpFullscreen;
    [ObservableProperty] private bool _rdpSmartCards;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(ShowAgentStatus))] private bool _sshAgent;

    /// <summary>Which agent would answer right now (or that none runs).</summary>
    public string AgentStatus => Nexode.Protocols.SshAgent.Describe() is { } name ? Loc.F("Ssh.AgentFound", name) : Loc.T("Ssh.AgentNone");
    [ObservableProperty] private bool _rdpSpan;
    [ObservableProperty] private bool _gatewayBypass;
    [ObservableProperty] private bool _gatewaySameCreds;
    [ObservableProperty] private string _hyperVId;
    public bool HasGateway => !string.IsNullOrWhiteSpace(Gateway);
    partial void OnGatewayChanged(string value) => OnPropertyChanged(nameof(HasGateway));

    private static int MonitorCount()
    {
        try
        {
            return Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime { MainWindow: { } w }
                ? w.Screens.All.Count : 1;
        }
        catch (InvalidOperationException)
        {
            return 1;
        }
    }

    public bool CanTunnel => EntryKinds.CanTunnel(K);
    public bool IsS3 => K == EntryKind.S3;
    public bool IsWebDav => K == EntryKind.WebDav;
    public bool HasUrl => EntryKinds.HasUrl(K);
    public string UrlWatermark => IsWebDav ? "https://cloud.example.org/remote.php/dav/files/me/" : "https://panel.example.com:8443";
    public bool IsSerial => K == EntryKind.Serial;
    public bool HasWol => Features.On(Features.WakeOnLan) && (K == EntryKind.Host || HasHost && EntryKinds.IsConnection(K));
    public bool HasAgentOption => IsSsh && Features.On(Features.SshAgent);
    public bool ShowAgentStatus => HasAgentOption && SshAgent;
    public bool KeyToolsEnabled => Features.On(Features.SshKeys);
    public bool IsSessionKind => EntryKinds.IsSession(K);
    public bool CanChangeKind => IsNew;
    public bool HasHost => EntryKinds.HasHost(K);
    public bool HasPort => HasHost && K != EntryKind.Host && K != EntryKind.Database || K == EntryKind.Database;
    public bool IsWeb => K == EntryKind.Web;
    public bool IsHostKind => K == EntryKind.Host;
    public bool IsRdp => K == EntryKind.Rdp;
    public bool IsSsh => K is EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp;
    public bool IsTerminal => K is EntryKind.Ssh or EntryKind.Telnet;
    public bool IsTelnet => K == EntryKind.Telnet;
    public bool HasColor => ColorHue is not null;
    public bool NoColor => ColorHue is null && !RandomColor;

    /// <summary>Custom color from the color picker: only its hue is stored (lightness follows the theme).</summary>
    public Avalonia.Media.Color CustomColor
    {
        get => ThemeService.ToColor(FolderColors.ToRgb(ColorHue ?? AppHost.Theme.AccentHue, AppHost.Theme.IsDark));
        set
        {
            var hue = Math.Round(Core.Theming.Oklch.FromRgb(new Core.Theming.Rgb(value.R / 255d, value.G / 255d, value.B / 255d)).H, 1);
            if (ColorHue is { } h && Math.Abs(h - hue) < 0.5) return;
            ColorHue = hue;
        }
    }
    public bool IsFileTransfer => EntryKinds.IsFileTransfer(K);
    public bool IsFtp => K == EntryKind.Ftp;
    public bool IsVnc => K == EntryKind.Vnc;
    public bool IsCommand => K == EntryKind.Command;
    public bool IsDatabase => K == EntryKind.Database;
    public bool IsCredentialKind => K == EntryKind.Credential;
    public bool HasCredentials => EntryKinds.HasCredentials(K);
    public bool HasCredentialFields => IsCredentialKind || CredentialMode.Value is Core.Model.CredentialMode.Own or Core.Model.CredentialMode.Prompt;
    public bool HasPasswordField => IsCredentialKind || CredentialMode.Value == Core.Model.CredentialMode.Own;
    public bool CanChooseMode => HasCredentials && !IsCredentialKind;
    public bool IsProfileMode => CanChooseMode && CredentialMode.Value == Core.Model.CredentialMode.Profile;
    public bool IsInheritMode => CanChooseMode && CredentialMode.Value == Core.Model.CredentialMode.Inherit;
    public bool HasProtocolOptions => IsPowerShell || IsRdp || IsSsh || IsTerminal || IsFileTransfer || IsVnc || IsCommand || IsDatabase || IsHostKind || IsSerial || HasWol || IsWeb || CanTunnel;
    public bool HadPassword => _hadPassword;
    public string PasswordWatermark => _hadPassword ? Loc.T("Editor.PasswordUnchanged") : Loc.T("Editor.PasswordNone");
    public string DefaultPortText => EntryKinds.DefaultPort(K)?.ToString(CultureInfo.InvariantCulture) ?? "";
    public string IconPreview => Icon ?? AppHost.Workspace.Resolver.ResolveIcon(new Entry { Kind = K, Os = Os.Value, CategoryId = Category.Value }, Os.Value);

    public string InheritInfo
    {
        get
        {
            var ws = AppHost.Workspace;
            var probe = _entry.Clone();
            probe.CredentialMode = Core.Model.CredentialMode.Inherit;
            var parent = ws.Tree.Get(probe.ParentId);
            if (parent is null) return Loc.T("Editor.InheritNothing");
            var eff = ws.Resolver.Resolve(parent);
            var from = eff.CredentialFrom is { } f ? ws.Tree.Get(f)?.Name : null;
            return eff.Username is null ? Loc.T("Editor.InheritNothing") : Loc.F("Editor.InheritFrom", eff.UserWithDomain, from ?? parent.Name);
        }
    }

    partial void OnKindChanged(Option<EntryKind> value) => OnPropertyChanged(string.Empty);
    partial void OnCredentialModeChanged(Option<CredentialMode> value) => OnPropertyChanged(string.Empty);
    partial void OnPasswordChanged(string value) => PasswordChanged = true;
    partial void OnIconChanged(string? value) => OnPropertyChanged(nameof(IconPreview));
    partial void OnOsChanged(Option<OsFamily> value) => OnPropertyChanged(nameof(IconPreview));
    partial void OnCategoryChanged(Option<string?> value) => OnPropertyChanged(nameof(IconPreview));
    partial void OnColorHueChanged(double? value)
    {
        if (value is not null) RandomColor = false;
        UpdateSwatches();
        OnPropertyChanged(nameof(HasColor));
        OnPropertyChanged(nameof(NoColor));
        OnPropertyChanged(nameof(CustomColor));
    }

    private void UpdateSwatches()
    {
        foreach (var s in Swatches) s.IsSelected = s.Hue == ColorHue;
    }

    public void ClearPassword()
    {
        Password = "";
        PasswordChanged = true;
    }

    /// <summary>Validates and writes the values into the entry. Null = ok, otherwise the error text.</summary>
    public string? Apply()
    {
        var e = _entry;
        var name = Name.Trim();
        var host = Host.Trim();
        var url = Url.Trim();

        if (HasUrl)
        {
            if (url.Length > 0 && !url.Contains("://", StringComparison.Ordinal)) url = "https://" + url;
            if (IsWebDav && Uri.TryCreate(url, UriKind.Absolute, out var dav) && dav.Scheme is "dav" or "davs" or "webdav" or "webdavs")
            {
                url = new UriBuilder(dav) { Scheme = dav.Scheme.EndsWith('s') ? "https" : "http", Port = dav.IsDefaultPort ? -1 : dav.Port }.Uri.AbsoluteUri;
            }

            if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || (u.Scheme != Uri.UriSchemeHttp && u.Scheme != Uri.UriSchemeHttps))
            {
                return Loc.T("Editor.ErrorUrl");
            }

            if (name.Length == 0) name = u.Host;
        }

        if (HasHost && host.Length == 0 && K != EntryKind.Host && !HasHostParent()) return Loc.T("Editor.ErrorHost");
        if (name.Length == 0) name = host;
        if (name.Length == 0) return Loc.T("Editor.ErrorName");

        int? port = null;
        if (HasPort && Port.Trim().Length > 0)
        {
            if (!int.TryParse(Port.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var p) || p is < 1 or > 65535) return Loc.T("Editor.ErrorPort");
            port = p == EntryKinds.DefaultPort(K) ? null : p;
        }

        e.Kind = K;
        e.Name = name;
        e.Host = HasHost && host.Length > 0 ? host : null;
        e.Port = port;
        e.Url = HasUrl ? url : null;
        e.Description = Description.Trim().Length > 0 ? Description.Trim() : null;
        e.Notes = Notes.Trim().Length > 0 ? Notes : null;
        e.Tags = Tags.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.TrimStart('#')).Where(t => t.Length > 0).Distinct().ToList();
        e.CategoryId = Category.Value;
        e.EnvironmentId = Environment.Value;
        e.Os = Os.Value;
        e.Icon = string.IsNullOrEmpty(Icon) ? null : Icon;
        e.IsFavorite = IsFavorite;
        e.ColorHue = RandomColor
            ? PickRandomHue(AppHost.Workspace.Tree.Children(e.ParentId).Where(c => c.Id != e.Id && c.ColorHue is not null).Select(c => c.ColorHue!.Value))
            : ColorHue;

        e.CredentialMode = IsCredentialKind ? Core.Model.CredentialMode.Own : HasCredentials ? CredentialMode.Value : Core.Model.CredentialMode.None;
        e.Username = HasCredentialFields && Username.Trim().Length > 0 ? Username.Trim() : null;
        e.Domain = HasCredentialFields && Domain.Trim().Length > 0 ? Domain.Trim() : null;
        e.CredentialId = IsProfileMode ? Profile?.Value : null;
        if (IsProfileMode && Profile is null) return Loc.T("Editor.ErrorProfile");

        e.Settings.Clear();
        if (IsHostKind) e.SetSetting(SettingKeys.IpAddress, Ip.Trim());
        if (HasWol)
        {
            if (Mac.Trim().Length > 0 && Nexode.Protocols.WakeOnLan.ParseMac(Mac) is null) return Loc.T("Editor.ErrorMac");
            e.SetSetting(SettingKeys.MacAddress, Nexode.Protocols.WakeOnLan.ParseMac(Mac) is { } mac ? Nexode.Protocols.WakeOnLan.FormatMac(mac) : null);
            e.SetSetting(SettingKeys.WolBroadcast, WolBroadcast.Trim());
            e.SetSetting(SettingKeys.WolPort, WolPort.Trim() is "9" ? null : WolPort.Trim());
        }

        if (IsSerial)
        {
            if (SerialPort.Trim().Length == 0) return Loc.T("Editor.ErrorSerialPort");
            e.SetSetting(SettingKeys.SerialPort, SerialPort.Trim());
            e.SetSetting(SettingKeys.SerialBaud, SerialBaud.Trim());
            e.SetSetting(SettingKeys.SerialDataBits, SerialDataBits == "8" ? null : SerialDataBits);
            e.SetSetting(SettingKeys.SerialParity, SerialParityIndex == 0 ? null : ParityCodes[SerialParityIndex]);
            e.SetSetting(SettingKeys.SerialStopBits, SerialStopBits == "1" ? null : SerialStopBits);
            e.SetSetting(SettingKeys.SerialFlow, SerialFlowIndex == 0 ? null : FlowCodes[SerialFlowIndex]);
            e.SetSetting(SettingKeys.SerialNewLine, SerialNewLineIndex == 0 ? null : NewLineCodes[SerialNewLineIndex]);
            e.SetSetting(SettingKeys.SerialEcho, SerialEcho ? "1" : null);
        }

        if (CanTunnel) e.SetSetting(SettingKeys.TunnelVia, JumpHost.Value?.ToString());
        if (IsWeb)
        {
            e.SetSetting(SettingKeys.WebIgnoreCert, WebIgnoreCert ? "1" : null);
            e.SetSetting(SettingKeys.WebBasicAuth, WebBasicAuth ? "1" : null);
            e.SetSetting(SettingKeys.WebFormFill, WebFormFill ? "1" : null);
            e.SetSetting(SettingKeys.WebUserSelector, WebFormFill ? WebUserSelector.Trim() : null);
            e.SetSetting(SettingKeys.WebPassSelector, WebFormFill ? WebPassSelector.Trim() : null);
            e.SetSetting(SettingKeys.WebExternal, WebExternal ? "1" : null);
        }

        if (IsS3)
        {
            if (S3Bucket.Trim().Contains('/')) return Loc.T("S3.ErrorBucket");
            e.SetSetting(SettingKeys.S3Bucket, S3Bucket.Trim());
            e.SetSetting(SettingKeys.S3Region, S3Region.Trim());
            e.SetSetting(SettingKeys.S3PathStyle, S3StyleIndex switch { 1 => "1", 2 => "0", _ => null });
            e.SetSetting(SettingKeys.UseTls, UseTls ? null : "0");
        }

        if (IsSessionKind) e.SetSetting(SettingKeys.AutoReconnect, ReconnectIndex switch { 1 => "1", 2 => "0", _ => null });

        if (IsRdp)
        {
            e.SetSetting(SettingKeys.RdpResolution, Resolution.Contains('x') ? Resolution : null);
            e.SetSetting(SettingKeys.RdpAdmin, RdpAdmin ? "1" : null);
            e.SetSetting(SettingKeys.RdpGateway, Gateway.Trim());
            e.SetSetting(SettingKeys.RdpClipboard, RdpClipboard ? null : "0");
            e.SetSetting(SettingKeys.RdpDrives, RdpDrives ? "1" : null);
            e.SetSetting(SettingKeys.RdpPrinters, RdpPrinters ? "1" : null);
            e.SetSetting(SettingKeys.RdpSmartSizing, RdpSmartSizing ? null : "0");
            e.SetSetting(SettingKeys.RdpColorDepth, ColorDepth == "32" ? null : ColorDepth);
            e.SetSetting(SettingKeys.RdpAudio, AudioIndex == 0 ? null : AudioIndex.ToString(CultureInfo.InvariantCulture));
            e.SetSetting(SettingKeys.RdpMonitor, RdpMonitorIndex > 0 ? RdpMonitorIndex.ToString(CultureInfo.InvariantCulture) : null);
            e.SetSetting(SettingKeys.RdpFullscreen, RdpFullscreen ? "1" : null);
            e.SetSetting(SettingKeys.RdpSmartCards, RdpSmartCards ? "1" : null);
            e.SetSetting(SettingKeys.RdpSpan, RdpSpan ? "1" : null);
            var picked = SpanMonitors.Where(m => m.IsSelected).Select(m => m.Index.ToString(CultureInfo.InvariantCulture)).ToList();
            e.SetSetting(SettingKeys.RdpMonitors, RdpSpan && picked.Count > 0 && picked.Count < SpanMonitors.Count ? string.Join(',', picked) : null);
            e.SetSetting(SettingKeys.RdpGatewayBypass, GatewayBypass && HasGateway ? "1" : null);
            e.SetSetting(SettingKeys.RdpGatewaySameCreds, GatewaySameCreds || !HasGateway ? null : "0");
            e.SetSetting(SettingKeys.RdpHyperVId, Guid.TryParse(HyperVId.Trim(), out var vmId) ? vmId.ToString() : null);
            e.SetSetting(SettingKeys.RdpRemoteApp, RemoteApp.Trim());
            e.SetSetting(SettingKeys.RdpRemoteAppArgs, RemoteApp.Trim().Length > 0 ? RemoteAppArgs.Trim() : null);
            e.SetSetting(SettingKeys.RdpClipboardFiles, RdpClipboard && !RdpClipboardFiles ? "0" : null);
        }

        if (IsPowerShell)
        {
            e.SetSetting(SettingKeys.PsTransport, PsTransportIndex == 0 ? null : PsTransportKeys[Math.Clamp(PsTransportIndex, 0, 2)]);
            e.SetSetting(SettingKeys.PsAuth, PsAuthIndex == 0 || !IsPsWinRm ? null : PsAuthKeys[Math.Clamp(PsAuthIndex, 0, 3)]);
            e.SetSetting(SettingKeys.PsConfiguration, IsPsWinRm ? PsConfiguration.Trim() : null);
            e.SetSetting(SettingKeys.PsPwsh, PsPwsh ? "1" : null);
            e.SetSetting(SettingKeys.PsSkipCertCheck, IsPsSsl && PsSkipCertCheck ? "1" : null);
        }

        if (IsSsh) e.SetSetting(SettingKeys.SshKeyFile, KeyFile.Trim());
        if (IsSsh) e.SetSetting(SettingKeys.SshAgent, SshAgent ? "1" : null);
        if (IsTerminal)
        {
            e.SetSetting(SettingKeys.SshTerminalType, TerminalType.Trim());
            e.SetSetting(SettingKeys.SshStartCommand, K == EntryKind.Ssh ? StartCommand.Trim() : null);
            e.SetSetting(SettingKeys.TelnetAutoLogin, K == EntryKind.Telnet && AutoLogin ? "1" : null);
        }
        if (IsFileTransfer) e.SetSetting(SettingKeys.RemotePath, RemotePath.Trim());
        if (IsFtp)
        {
            e.SetSetting(SettingKeys.FtpEncryption, FtpMode.Value.ToString());
            e.SetSetting(SettingKeys.FtpPassive, FtpPassive ? null : "false");
        }

        if (IsVnc)
        {
            e.SetSetting(SettingKeys.VncViewOnly, VncViewOnly ? "1" : null);
            e.SetSetting(SettingKeys.VncScale, VncScale ? null : "0");
            e.SetSetting(SettingKeys.VncSecurity, VncSecurityIndex <= 0 ? null : VncSecurityKeys[Math.Min(VncSecurityIndex, VncSecurityKeys.Length - 1)]);
            e.SetSetting(SettingKeys.VncQuality, VncQualityIndex <= 0 ? null : VncQualityKeys[Math.Min(VncQualityIndex, VncQualityKeys.Length - 1)]);
        }
        if (IsCommand)
        {
            e.SetSetting(SettingKeys.CommandLine, CommandLine.Trim());
            e.SetSetting(SettingKeys.CommandArgs, CommandArgs.Trim());
            if (string.IsNullOrWhiteSpace(CommandLine)) return Loc.T("Editor.ErrorCommand");
        }

        if (IsDatabase)
        {
            e.SetSetting(SettingKeys.DbEngine, DbEngine);
            e.SetSetting(SettingKeys.DbName, DbName.Trim());
        }

        return null;
    }

    private bool HasHostParent()
    {
        var ws = AppHost.Workspace;
        return ws.Tree.Get(_entry.ParentId) is { } parent && ws.Resolver.Resolve(parent).Host is not null && parent.Kind == EntryKind.Host;
    }

    /// <summary>Null = keep, "" = remove, else the new password.</summary>
    public string? PasswordToSave => HasPasswordField && PasswordChanged ? Password : !HasPasswordField && _hadPassword ? "" : null;
}

/// <summary>One monitor in the RDP "use all monitors" selection.</summary>
public sealed partial class MonitorChoice(int index, string label, bool selected) : CommunityToolkit.Mvvm.ComponentModel.ObservableObject
{
    public int Index { get; } = index;
    public string Label { get; } = label;

    [CommunityToolkit.Mvvm.ComponentModel.ObservableProperty]
    private bool _isSelected = selected;
}
