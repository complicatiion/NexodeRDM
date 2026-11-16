using Nexode.Core.Sync;

namespace Nexode.Core.Model;

/// <summary>One node of the connection tree (folder, host, connection, credential, note).</summary>
public sealed class Entry
{
    public Guid Id { get; set; } = Guid.CreateVersion7();
    public Guid? ParentId { get; set; }
    public EntryKind Kind { get; set; }
    public string Name { get; set; } = "";

    public string? Host { get; set; }
    public int? Port { get; set; }
    public string? Url { get; set; }

    public CredentialMode CredentialMode { get; set; } = CredentialMode.Inherit;
    public string? Username { get; set; }
    public string? Domain { get; set; }
    /// <summary>Credential profile entry used when <see cref="CredentialMode"/> is Profile.</summary>
    public Guid? CredentialId { get; set; }

    public string? CategoryId { get; set; }
    public List<string> Tags { get; set; } = [];
    /// <summary>Environment id (see <see cref="EnvironmentDef"/>); null = inherit from the parent, "" = none.</summary>
    public string? EnvironmentId { get; set; }

    /// <summary>Files up to v0.0.2 stored a fixed enum; read only.</summary>
    [System.Text.Json.Serialization.JsonPropertyName("environment")]
    public EnvironmentTag? LegacyEnvironment
    {
        get => null;
        set
        {
            if (value is { } v && EnvironmentId is null) EnvironmentId = v == EnvironmentTag.None ? "" : v.ToString().ToLowerInvariant();
        }
    }
    public OsFamily Os { get; set; }

    /// <summary>Icon key; null = resolved from category, OS or kind.</summary>
    public string? Icon { get; set; }
    /// <summary>OKLCH hue in degrees; null = no own color.</summary>
    public double? ColorHue { get; set; }

    public bool IsFavorite { get; set; }
    public string FavoriteKey { get; set; } = "";
    /// <summary>Pinned to the rail (independent of favorites); <see cref="PinKey"/> orders the rail.</summary>
    public bool IsPinned { get; set; }
    public string PinKey { get; set; } = "";
    /// <summary>Personal color of the rail pin (OKLCH hue); null = the entry's own or inherited color.</summary>
    public double? PinHue { get; set; }
    public string SortKey { get; set; } = "";
    /// <summary>Folders only; null = inherit.</summary>
    public SortMode? SortMode { get; set; }
    public bool IsExpanded { get; set; }

    public string? Description { get; set; }
    public string? Notes { get; set; }
    /// <summary>Protocol-specific options (key/value).</summary>
    public Dictionary<string, string> Settings { get; set; } = [];

    /// <summary>Imported launcher/command entries stay disabled until the user reviewed them.</summary>
    public bool NeedsReview { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedUtc { get; set; }
    /// <summary>Days (since 2000-01-01) of the recent connections; drives "most used" (last 90 days).</summary>
    public List<int> UseDays { get; set; } = [];
    public DateTime? DeletedUtc { get; set; }
    public Hlc Version { get; set; }

    public bool IsDeleted => DeletedUtc is not null;
    public bool IsContainer => Kind is EntryKind.Folder or EntryKind.Host;

    public string? Setting(string key) => Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;

    public void SetSetting(string key, string? value)
    {
        if (string.IsNullOrEmpty(value)) Settings.Remove(key);
        else Settings[key] = value;
    }

    public Entry Clone()
    {
        var copy = (Entry)MemberwiseClone();
        copy.Tags = [.. Tags];
        copy.UseDays = [.. UseDays];
        copy.Settings = new Dictionary<string, string>(Settings);
        return copy;
    }

    /// <summary>True when the stored content (not UI state) differs.</summary>
    public bool ContentEquals(Entry o) =>
        ParentId == o.ParentId && Kind == o.Kind && Name == o.Name && Host == o.Host && Port == o.Port && Url == o.Url &&
        CredentialMode == o.CredentialMode && Username == o.Username && Domain == o.Domain && CredentialId == o.CredentialId &&
        CategoryId == o.CategoryId && Tags.SequenceEqual(o.Tags) && EnvironmentId == o.EnvironmentId && Os == o.Os &&
        Icon == o.Icon && ColorHue == o.ColorHue && IsFavorite == o.IsFavorite && IsPinned == o.IsPinned && PinHue == o.PinHue && SortKey == o.SortKey && SortMode == o.SortMode &&
        Description == o.Description && Notes == o.Notes && NeedsReview == o.NeedsReview && DeletedUtc == o.DeletedUtc &&
        Settings.Count == o.Settings.Count && Settings.All(p => o.Settings.TryGetValue(p.Key, out var v) && v == p.Value);

    public override string ToString() => $"{Kind} {Name}";
}

/// <summary>Optional organizational category: default icon and color, filter.</summary>
public sealed class Category
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "tag";
    public double? ColorHue { get; set; }
    public bool BuiltIn { get; set; }
    public int Order { get; set; }

    public static IReadOnlyList<Category> Defaults { get; } =
    [
        new() { Id = "server", Name = "Server", Icon = "server", BuiltIn = true, Order = 0 },
        new() { Id = "workstation", Name = "Workstation", Icon = "monitor", BuiltIn = true, Order = 1 },
        new() { Id = "hardware", Name = "Hardware", Icon = "cpu", BuiltIn = true, Order = 2 },
        new() { Id = "network", Name = "Network", Icon = "network", BuiltIn = true, Order = 3 },
        new() { Id = "webservice", Name = "Web service", Icon = "cloud", BuiltIn = true, Order = 4 },
        new() { Id = "website", Name = "Website", Icon = "globe", BuiltIn = true, Order = 5 },
        new() { Id = "database", Name = "Database", Icon = "database", BuiltIn = true, Order = 6 },
        new() { Id = "storage", Name = "Storage", Icon = "hard-drive", BuiltIn = true, Order = 7 },
        new() { Id = "virtualization", Name = "Virtualization", Icon = "layers", BuiltIn = true, Order = 8 },
        new() { Id = "printer", Name = "Printer", Icon = "printer", BuiltIn = true, Order = 9 },
        new() { Id = "firewall", Name = "Firewall", Icon = "brick-wall", BuiltIn = true, Order = 10 },
        new() { Id = "container", Name = "Container", Icon = "container", BuiltIn = true, Order = 11 },
        new() { Id = "identity", Name = "Identity", Icon = "key-round", BuiltIn = true, Order = 12 },
        new() { Id = "backup", Name = "Backup", Icon = "archive-restore", BuiltIn = true, Order = 13 }
    ];
}

/// <summary>User-defined environment (Production, Staging …): badge, tab stripe and color.</summary>
public sealed class EnvironmentDef
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    /// <summary>Short badge text in the tree ("PROD").</summary>
    public string Short { get; set; } = "";
    /// <summary>#RRGGBB.</summary>
    public string Color { get; set; } = "#808080";
    public bool BuiltIn { get; set; }
    public int Order { get; set; }

    public static IReadOnlyList<EnvironmentDef> Defaults { get; } =
    [
        new() { Id = "production", Name = "Production", Short = "PROD", Color = "#E5484D", BuiltIn = true, Order = 0 },
        new() { Id = "staging", Name = "Staging", Short = "STAGE", Color = "#F59E0B", BuiltIn = true, Order = 1 },
        new() { Id = "test", Name = "Test", Short = "TEST", Color = "#3B82F6", BuiltIn = true, Order = 2 },
        new() { Id = "development", Name = "Development", Short = "DEV", Color = "#22A06B", BuiltIn = true, Order = 3 },
        new() { Id = "trash", Name = "Trash", Short = "TRASH", Color = "#8B5E3C", BuiltIn = true, Order = 4 }
    ];
}

/// <summary>How a connection profile opens its entries.</summary>
public enum ProfileLayout
{
    Tabs = 0,
    NewWindow = 1,
    Split = 2
}

/// <summary>Named set of connections that open together ("Citrix management").</summary>
public sealed class ConnectionProfile
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Icon { get; set; } = "layers";
    public double? ColorHue { get; set; }
    public List<Guid> Entries { get; set; } = [];
    public ProfileLayout Layout { get; set; }
    /// <summary>Connect one after the other (with <see cref="DelayMs"/>) instead of all at once.</summary>
    public bool Sequential { get; set; } = true;
    public int DelayMs { get; set; } = 400;
    public int Order { get; set; }
}

/// <summary>
/// Template for new entries ("Windows Server default": gateway, drives, resolution …). The stored entry carries the
/// options only – no name, parent, host or secrets.
/// </summary>
public sealed class EntryTemplate
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Entry Entry { get; set; } = new();
    public int Order { get; set; }

    /// <summary>Template from an existing entry: everything that makes up its options, nothing that identifies the machine.</summary>
    public static EntryTemplate From(Entry source, string name)
    {
        var e = source.Clone();
        e.Id = Guid.Empty;
        e.ParentId = null;
        e.Name = "";
        e.Host = null;
        e.Url = null;
        e.Notes = null;
        e.Description = null;
        e.Tags = [];
        e.IsFavorite = false;
        e.FavoriteKey = "";
        e.IsPinned = false;
        e.PinKey = "";
        e.PinHue = null;
        e.UseDays = [];
        e.LastUsedUtc = null;
        e.DeletedUtc = null;
        e.Version = default;
        e.CredentialId = null;
        e.SortKey = "";
        foreach (var key in new[] { SettingKeys.RdpHyperVId, SettingKeys.SshKeyFile, SettingKeys.MacAddress }) e.SetSetting(key, null);
        return new EntryTemplate { Name = name, Entry = e };
    }

    /// <summary>A new entry from the template (new id, placed under <paramref name="parentId"/>).</summary>
    public Entry Create(Guid? parentId)
    {
        var e = Entry.Clone();
        e.Id = Guid.CreateVersion7();
        e.ParentId = parentId;
        e.Name = "";
        e.CreatedUtc = DateTime.UtcNow;
        e.Version = default;
        return e;
    }
}

/// <summary>Usage statistics of an entry.</summary>
public static class Usage
{
    public const int WindowDays = 90;
    private static readonly DateOnly Epoch = new(2000, 1, 1);

    public static int Today => DateOnly.FromDateTime(DateTime.UtcNow).DayNumber - Epoch.DayNumber;

    /// <summary>Records one connection and drops days outside the window.</summary>
    public static void Record(Entry e)
    {
        var today = Today;
        e.UseDays.RemoveAll(d => d < today - WindowDays);
        e.UseDays.Add(today);
        if (e.UseDays.Count > 500) e.UseDays.RemoveRange(0, e.UseDays.Count - 500);
    }

    public static int Count(Entry e)
    {
        var from = Today - WindowDays;
        return e.UseDays.Count(d => d >= from);
    }
}

/// <summary>Well-known setting keys of <see cref="Entry.Settings"/>.</summary>
public static class SettingKeys
{
    public const string RdpResolution = "rdp.resolution";
    public const string RdpAdmin = "rdp.admin";
    public const string RdpGateway = "rdp.gateway";
    public const string RdpColorDepth = "rdp.colorDepth";
    public const string RdpClipboard = "rdp.clipboard";
    public const string RdpDrives = "rdp.drives";
    public const string RdpPrinters = "rdp.printers";
    public const string RdpAudio = "rdp.audio";
    public const string RdpSmartSizing = "rdp.smartSizing";
    public const string SshKeyFile = "ssh.keyFile";
    /// <summary>"1" = sign in with the keys of the running SSH agent (OpenSSH agent, Pageant, SSH_AUTH_SOCK).</summary>
    public const string SshAgent = "ssh.agent";
    public const string SshKeepAlive = "ssh.keepAlive";
    public const string SshTerminalType = "ssh.term";
    public const string SshStartCommand = "ssh.startCommand";
    public const string TelnetAutoLogin = "telnet.autoLogin";
    public const string RemotePath = "remote.path";
    public const string FtpEncryption = "ftp.encryption";
    public const string FtpPassive = "ftp.passive";
    public const string VncViewOnly = "vnc.viewOnly";
    public const string VncScale = "vnc.scale";
    /// <summary>"auto" (default), "vnc", "vencrypt", "ard", "none".</summary>
    public const string VncSecurity = "vnc.security";
    /// <summary>"balanced" (default: Tight + JPEG), "lan" (lossless ZRLE), "low" (strong compression for slow links).</summary>
    public const string VncQuality = "vnc.quality";
    public const string CommandLine = "cmd.line";
    public const string CommandArgs = "cmd.args";
    public const string DbEngine = "db.engine";
    public const string DbName = "db.name";
    public const string MacAddress = "host.mac";
    public const string WolBroadcast = "wol.broadcast";
    public const string WolPort = "wol.port";
    public const string SerialPort = "serial.port";
    public const string SerialBaud = "serial.baud";
    public const string SerialDataBits = "serial.dataBits";
    public const string SerialParity = "serial.parity";
    public const string SerialStopBits = "serial.stopBits";
    public const string SerialFlow = "serial.flow";
    public const string SerialNewLine = "serial.newLine";
    public const string SerialEcho = "serial.echo";
    public const string AutoReconnect = "session.autoReconnect";
    public const string IpAddress = "host.ip";
    /// <summary>Address of URL based kinds (WebDAV) inside connection parameters.</summary>
    public const string WebUrl = "web.url";
    public const string UseTls = "conn.tls";
    public const string S3Bucket = "s3.bucket";
    public const string S3Region = "s3.region";
    public const string S3PathStyle = "s3.pathStyle";
    /// <summary>Id of an SSH entry used as jump host.</summary>
    public const string TunnelVia = "tunnel.via";
    public const string WebIgnoreCert = "web.ignoreCert";
    public const string WebBasicAuth = "web.basicAuth";
    public const string WebFormFill = "web.formFill";
    public const string WebUserSelector = "web.userSelector";
    public const string WebPassSelector = "web.passSelector";
    public const string WebExternal = "web.external";
    public const string RdpMonitor = "rdp.monitor";
    public const string RdpFullscreen = "rdp.fullscreen";
    /// <summary>Smart card redirection.</summary>
    public const string RdpSmartCards = "rdp.smartcards";
    /// <summary>Use all monitors (external client: mstsc multimon / FreeRDP /multimon).</summary>
    public const string RdpSpan = "rdp.span";
    /// <summary>Bypass the RD Gateway for local addresses.</summary>
    public const string RdpGatewayBypass = "rdp.gatewayBypass";
    /// <summary>"0" = ask for separate gateway credentials; default: the connection's credentials are used for the gateway too.</summary>
    public const string RdpGatewaySameCreds = "rdp.gatewaySameCreds";
    /// <summary>Hyper-V console: VM id (GUID); the host is the Hyper-V server, port 2179.</summary>
    public const string RdpHyperVId = "rdp.hypervId";
    /// <summary>With "use all monitors": only these monitors (mstsc/FreeRDP numbering from 0, e.g. "0,2"); empty = all.</summary>
    public const string RdpMonitors = "rdp.monitors";
    /// <summary>RemoteApp: program alias ("||calc") or path on the server; opens in the system client as its own window.</summary>
    public const string RdpRemoteApp = "rdp.remoteApp";
    /// <summary>RemoteApp command line arguments.</summary>
    public const string RdpRemoteAppArgs = "rdp.remoteAppArgs";
    /// <summary>"0" = clipboard for text only (no files); enforced by FreeRDP 3, the Windows client follows the server policy.</summary>
    public const string RdpClipboardFiles = "rdp.clipboardFiles";

    /// <summary>PowerShell transport: "winrm" (default, HTTP 5985), "winrm-ssl" (HTTPS 5986) or "ssh" (PowerShell 7 over SSH).</summary>
    public const string PsTransport = "ps.transport";
    /// <summary>WinRM authentication: "Default", "Negotiate", "Kerberos", "CredSSP".</summary>
    public const string PsAuth = "ps.auth";
    /// <summary>Session configuration (JEA endpoint), e.g. "Microsoft.PowerShell" or a custom endpoint.</summary>
    public const string PsConfiguration = "ps.configuration";
    /// <summary>"1" = Windows: use PowerShell 7 (pwsh.exe) instead of Windows PowerShell when installed.</summary>
    public const string PsPwsh = "ps.pwsh";
    /// <summary>"1" = HTTPS: skip CA and name checks of the WinRM certificate (self-signed lab servers).</summary>
    public const string PsSkipCertCheck = "ps.skipCertCheck";
}
