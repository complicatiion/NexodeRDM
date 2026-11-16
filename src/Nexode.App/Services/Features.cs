using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>One optional feature that can be switched off in Settings → Features.</summary>
/// <param name="Id">Stable key (stored in settings.json).</param>
/// <param name="Group">Card on the Features page: "sessions", "tools", "view" or "data".</param>
/// <param name="DefaultOn">State as long as the user has not touched the switch.</param>
/// <param name="Requires">Feature that must be on for this one to work.</param>
/// <param name="Kinds">Entry kinds that belong to this feature (hidden from "New", greyed in the tree when off).</param>
public sealed record FeatureDef(string Id, string Group, string Icon, bool DefaultOn, string? Requires = null, EntryKind[]? Kinds = null);

/// <summary>
/// Optional features. Everything ships in the package; a switched-off feature only hides its entry points and stops its
/// background work. Data is never deleted: entries of a switched-off kind stay in the tree (greyed) and in team sources.
/// RDP, SSH, SFTP/FTP/SCP, hosts, credentials and notes are the core and cannot be switched off.
/// </summary>
public static class Features
{
    // Connection types
    public const string Vnc = "vnc";
    public const string Telnet = "telnet";
    public const string Serial = "serial";
    public const string Web = "web";
    public const string Cloud = "cloud";
    public const string Database = "database";
    public const string Command = "command";
    public const string PowerShell = "powershell";

    // Tools
    public const string Monitoring = "monitoring";
    public const string TreeStatus = "treestatus";
    public const string FolderSync = "foldersync";
    public const string WakeOnLan = "wol";
    public const string SshAgent = "sshagent";
    public const string SshKeys = "sshkeys";
    public const string Broadcast = "broadcast";
    public const string FtpServer = "ftpserver";
    public const string SysInfo = "sysinfo";

    // View
    public const string Split = "split";
    public const string Palette = "palette";

    // Data
    public const string Team = "team";
    public const string LanSync = "lansync";

    public static IReadOnlyList<FeatureDef> All { get; } =
    [
        new(Vnc, "sessions", "screen-share", true, Kinds: [EntryKind.Vnc]),
        new(Telnet, "sessions", "terminal", true, Kinds: [EntryKind.Telnet]),
        new(Serial, "sessions", "cable", true, Kinds: [EntryKind.Serial]),
        new(PowerShell, "sessions", "code-xml", true, Kinds: [EntryKind.PowerShell]),
        new(Web, "sessions", "globe", true, Kinds: [EntryKind.Web]),
        new(Cloud, "sessions", "cloud-cog", true, Kinds: [EntryKind.WebDav, EntryKind.S3]),
        new(Database, "sessions", "database", true, Kinds: [EntryKind.Database]),
        new(Command, "sessions", "square-chevron-right", true, Kinds: [EntryKind.Command]),

        new(Monitoring, "tools", "layout-dashboard", true),
        new(TreeStatus, "tools", "circle-dot", false, Requires: Monitoring),
        new(FolderSync, "tools", "folder-sync", false),
        new(WakeOnLan, "tools", "power", true),
        new(SshAgent, "tools", "key-square", true),
        new(SshKeys, "tools", "key-round", true),
        new(Broadcast, "tools", "radio-tower", true, Requires: Split),
        new(SysInfo, "tools", "cpu", true),
        new(FtpServer, "tools", "server-cog", false),

        new(Split, "view", "columns-2", true),
        new(Palette, "view", "command", true),

        new(Team, "data", "users-round", true),
        new(LanSync, "data", "arrow-left-right", false)
    ];

    /// <summary>Features that stay on in the "Minimal" profile (everything else is switched off).</summary>
    public static IReadOnlySet<string> Minimal { get; } = new HashSet<string> { Split, Palette };

    public static event Action? Changed;

    public static FeatureDef? Get(string id) => All.FirstOrDefault(f => f.Id == id);

    /// <summary>Own switch only (ignores <see cref="FeatureDef.Requires"/>).</summary>
    public static bool IsSwitchedOn(string id)
    {
        var s = AppHost.Settings;
        if (s.Features.TryGetValue(id, out var on)) return on;
        // Folder sync had its own switch in 0.0.5 (Settings → Behavior).
        if (id == FolderSync) return s.FolderSync;
        return Get(id)?.DefaultOn ?? true;
    }

    /// <summary>Feature usable: switched on and everything it requires as well.</summary>
    public static bool On(string id)
    {
        if (!IsSwitchedOn(id)) return false;
        return Get(id)?.Requires is not { } req || On(req);
    }

    public static void Set(string id, bool on)
    {
        var s = AppHost.Settings;
        if (IsSwitchedOn(id) == on && s.Features.ContainsKey(id)) return;
        s.Features[id] = on;
        if (id == FolderSync) s.FolderSync = on;
        s.Save();
        Changed?.Invoke();
    }

    /// <summary>Sets every switch at once ("Minimal" or the defaults) and notifies once.</summary>
    public static void SetAll(Func<FeatureDef, bool> on)
    {
        var s = AppHost.Settings;
        foreach (var f in All) s.Features[f.Id] = on(f);
        s.FolderSync = s.Features[FolderSync];
        s.Save();
        Changed?.Invoke();
    }

    /// <summary>Feature that owns the kind; null for the core kinds.</summary>
    public static string? FeatureOf(EntryKind kind) => All.FirstOrDefault(f => f.Kinds?.Contains(kind) == true)?.Id;

    public static bool KindEnabled(EntryKind kind) => FeatureOf(kind) is not { } id || On(id);

    /// <summary>Kinds for "New" menus and the editor (disabled kinds hidden; <paramref name="keep"/> stays for existing entries).</summary>
    public static IReadOnlyList<EntryKind> CreatableKinds(EntryKind? keep = null) =>
        EntryKinds.Creatable.Where(k => KindEnabled(k) || k == keep).ToList();
}
