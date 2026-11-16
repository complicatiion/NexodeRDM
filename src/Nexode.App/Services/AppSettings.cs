using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Nexode.Core.Model;
using Nexode.Core.Theming;

namespace Nexode.App.Services;

public sealed class DataSourceInfo
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public string Path { get; set; } = "";
    public string Icon { get; set; } = "database";
    public double? ColorHue { get; set; }
    /// <summary>Team data source on a shared folder: local working copy, one editor at a time.</summary>
    public bool Shared { get; set; }
    /// <summary>Created by "Add sample data" (Settings → Integration); removed as a whole.</summary>
    public bool IsSample { get; set; }
}

/// <summary>Sections of the home page.</summary>
public static class HomeLayout
{
    public static readonly string[] DefaultOrder = ["search", "profiles", "favorites", "recent", "new", "status", "dashboard"];
    public static readonly string[] DefaultHidden = ["status", "dashboard"];
}

public sealed class WindowBounds
{
    public double Width { get; set; } = 1360;
    public double Height { get; set; } = 860;
    public bool Maximized { get; set; } = true;

    /// <summary>False until Nexode has stored the placement once (new installs and updates start maximized).</summary>
    public bool Saved { get; set; }
}

public enum OpenBehavior
{
    FocusExisting = 0,
    NewTab = 1
}

/// <summary>Per-installation settings (settings.json). Exported/imported as .nexode kind "settings".</summary>
public sealed class AppSettings
{
    public string? Language { get; set; }
    public ThemeMode Theme { get; set; } = ThemeMode.Nexode;
    public AutoThemeSource AutoSource { get; set; } = AutoThemeSource.FixedTimes;
    public string LightFrom { get; set; } = "07:00";
    public string DarkFrom { get; set; } = "19:00";
    public double? Latitude { get; set; }
    public double? Longitude { get; set; }
    public string Accent { get; set; } = Palette.DefaultAccent;
    /// <summary>System/Auto theme: use AMOLED black instead of the normal dark theme.</summary>
    public bool DarkVariantAmoled { get; set; }
    /// <summary>System/Auto theme: dark family (Dark by default; Nexode, Shadow, Amoled, Mica).</summary>
    public PaletteVariant DarkVariant { get; set; } = PaletteVariant.Dark;
    /// <summary>System/Auto theme: light family (Default, Lightweight).</summary>
    public PaletteVariant LightVariant { get; set; }
    /// <summary>Accent follows the operating system (Windows accent color, Linux portal accent where available).</summary>
    public bool UseSystemAccent { get; set; }
    /// <summary>One-time 0.0.5 migration done (dark variant).</summary>
    public bool Migrated005 { get; set; }
    /// <summary>Theme naming generation: 2 = "Nexode"/"Dark"/"Shadow" since 0.0.6. Missing = older file (see <see cref="ThemeNames"/>).</summary>
    public int ThemeNames { get; set; } = Nexode.App.Services.ThemeNames.Current;
    public bool Greetings { get; set; } = true;
    public bool ShowHints { get; set; } = true;
    public int TerminalFontSize { get; set; } = 13;
    public string TerminalFont { get; set; } = "";
    public int TerminalScrollback { get; set; } = 5000;
    public bool TerminalCopyOnSelect { get; set; }
    public bool Compact { get; set; }
    public bool ShowHostInTree { get; set; } = true;
    public bool AutoFolderColors { get; set; } = true;

    public bool RailExpanded { get; set; }
    public bool TreeVisible { get; set; } = true;
    public double TreeWidth { get; set; } = 300;
    public SortMode RootSort { get; set; } = SortMode.Manual;
    public bool FoldersFirst { get; set; } = true;

    public OpenBehavior OpenBehavior { get; set; } = OpenBehavior.FocusExisting;
    public bool ConfirmCloseSessions { get; set; } = true;

    public int ClipboardClearSeconds { get; set; } = 30;
    public int AutoLockMinutes { get; set; } = 15;
    public bool LockOnMinimize { get; set; }
    public bool LockOnSessionLock { get; set; } = true;
    public bool LockOnSleep { get; set; } = true;
    public bool ReauthToReveal { get; set; } = true;
    public bool ExportWithMasterPassword { get; set; }
    /// <summary>Ask "Connect now?" before every connection.</summary>
    public bool ConfirmConnect { get; set; }
    public bool ShowStatusBar { get; set; }
    /// <summary>Web panels open in the default browser instead of a tab.</summary>
    public bool OpenWebExternal { get; set; }
    public bool FilesShowHidden { get; set; }
    /// <summary>Optional feature: "Compare &amp; sync" in the file browser (0.0.5 switch; now part of <see cref="Features"/>).</summary>
    public bool FolderSync { get; set; }
    /// <summary>Folder compare &amp; sync: excluded names and paths (see <see cref="Nexode.Core.Files.SyncExcludes"/>).</summary>
    public string SyncExcludes { get; set; } = Nexode.Core.Files.SyncExcludes.Default;
    /// <summary>Local FTP server (feature "ftpserver"); the password is never stored.</summary>
    public FtpServerSettings FtpServer { get; set; } = new();
    public LanSyncSettings LanSync { get; set; } = new();
    /// <summary>Settings → Features: switches the user has set (missing = the feature's default).</summary>
    public Dictionary<string, bool> Features { get; set; } = [];
    /// <summary>Folders in the tree expand and collapse animated (also needs <see cref="Animations"/>).</summary>
    public bool TreeAnimation { get; set; } = true;
    /// <summary>Rail pins show entry colors (the tree keeps its colors either way).</summary>
    public bool RailColors { get; set; } = true;

    // ---- start (v0.0.4)
    /// <summary>Start page: "home", "dashboard", "restore" (tabs of the last session), "entry:{id}" or "profile:{id}".</summary>
    public string StartPage { get; set; } = "home";
    /// <summary>Monitor of the main window at start (0 = where it was last time).</summary>
    public int StartMonitor { get; set; }
    /// <summary>Entries of the open tabs when Nexode was closed (for "restore last tabs").</summary>
    public List<string> LastTabs { get; set; } = [];
    /// <summary>Last data source that was not the sample source (opened at start instead of the sample).</summary>
    public string? LastRealSource { get; set; }

    // ---- home page
    /// <summary>Sections of the home page in display order.</summary>
    public List<string> HomeSections { get; set; } = [.. HomeLayout.DefaultOrder];
    /// <summary>Hidden home page sections (the dashboard cards are off by default).</summary>
    public List<string> HomeHidden { get; set; } = [.. HomeLayout.DefaultHidden];

    // ---- dashboard
    public List<string> DashboardHidden { get; set; } = [];
    public int DashboardListCount { get; set; } = 8;
    /// <summary>Monitor of the dashboard window (0 = same as the main window).</summary>
    public int DashboardMonitor { get; set; }
    public bool DashboardFullscreen { get; set; }
    public bool DashboardOnStartup { get; set; }

    // ---- live status
    public bool LiveStatus { get; set; } = true;
    public int LiveStatusSeconds { get; set; } = 60;

    // ---- system integration
    public bool TrayIcon { get; set; } = true;
    public bool CloseToTray { get; set; }

    // ---- updates (off by default: no network access without consent)
    public bool UpdateCheck { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    public string? SkippedVersion { get; set; }

    // ---- connection history
    public bool ConnectionHistory { get; set; } = true;
    /// <summary>Info cards closed with their X (ids).</summary>
    public List<string> DismissedHints { get; set; } = [];
    public bool Animations { get; set; } = true;
    /// <summary>Speed factor: 2 = twice as fast, 0.5 = half as fast.</summary>
    public double AnimationSpeed { get; set; } = 1.0;
    /// <summary>UI scale on top of the system scaling; 0 = automatic (system only).</summary>
    public double UiScale { get; set; }
    /// <summary>Rail "Recent": false = last used, true = most used (90 days).</summary>
    public bool RecentMostUsed { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public int ReconnectAttempts { get; set; } = 5;
    public List<string> RecentGreetings { get; set; } = [];

    public int BackupKeep { get; set; } = 10;
    public int TrashRetentionDays { get; set; } = 30;

    public List<DataSourceInfo> DataSources { get; set; } = [];
    public string? ActiveDataSource { get; set; }
    public string DeviceId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public WindowBounds Window { get; set; } = new();

    [JsonIgnore]
    public string? FilePath { get; private set; }

    /// <summary>Set by the app reset: nothing is written anymore before the restart wipes the folder.</summary>
    [JsonIgnore]
    public bool SuppressSave { get; set; }

    public static AppSettings Load(string path)
    {
        AppSettings settings;
        try
        {
            if (File.Exists(path))
            {
                settings = Parse(File.ReadAllText(path)) ?? new AppSettings();
            }
            else
            {
                // First start of this user: choices made in the installer (never applied to existing settings).
                settings = new AppSettings();
                settings.ApplySetupDefaults(Path.Combine(AppContext.BaseDirectory, SetupDefaultsFile));
            }
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            // Damaged file: keep a copy, start with defaults.
            try { File.Copy(path, path + ".broken", true); } catch (IOException) { }
            settings = new AppSettings();
        }

        settings.FilePath = path;
        settings.Migrate();
        return settings;
    }

    /// <summary>Written by the Windows installer next to Nexode.exe (tray choices); read only when no settings exist yet.</summary>
    public const string SetupDefaultsFile = "setup-defaults.ini";

    /// <summary>Reads "key=1/0" lines of the installer's defaults file; unknown keys and a missing file are ignored.</summary>
    public void ApplySetupDefaults(string file)
    {
        try
        {
            if (!File.Exists(file)) return;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.Trim();
                var eq = line.IndexOf('=', StringComparison.Ordinal);
                if (eq <= 0 || line.StartsWith(';') || line.StartsWith('[')) continue;
                var key = line[..eq].Trim();
                var on = line[(eq + 1)..].Trim() is "1" or "true" or "yes";
                switch (key)
                {
                    case "TrayIcon": TrayIcon = on; break;
                    case "CloseToTray": CloseToTray = on; break;
                }
            }

            if (!TrayIcon) CloseToTray = false;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Settings JSON (file or .nexode payload) with the theme names of older versions translated.</summary>
    public static AppSettings? Parse(string json)
    {
        var node = System.Text.Json.Nodes.JsonNode.Parse(json);
        if (node is null) return null;
        Nexode.App.Services.ThemeNames.Upgrade(node);
        return node.Deserialize(AppJson.Default.AppSettings);
    }

    /// <summary>
    /// One-time upgrades of older settings files. Existing choices are never reset to new defaults (an update keeps
    /// what the user set); only renamed values are carried over.
    /// </summary>
    private void Migrate()
    {
        if (Migrated005) return;
        Migrated005 = true;
        if (DarkVariantAmoled && DarkVariant == PaletteVariant.Default) DarkVariant = PaletteVariant.Amoled;
    }

    /// <summary>Atomic write: temp file + rename, so a crash never leaves a half-written file.</summary>
    public void Save()
    {
        if (FilePath is null || SuppressSave) return;
        try
        {
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(this, AppJson.Default.AppSettings), new UTF8Encoding(false));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (IOException)
        {
            // Read-only medium etc.: settings stay in memory.
        }
    }

    /// <summary>Copies the user-facing preferences (not data sources, device, window) from an import.</summary>
    public void ApplyPreferences(AppSettings o)
    {
        Language = o.Language;
        Theme = o.Theme;
        AutoSource = o.AutoSource;
        LightFrom = o.LightFrom;
        DarkFrom = o.DarkFrom;
        Latitude = o.Latitude;
        Longitude = o.Longitude;
        Accent = o.Accent;
        DarkVariantAmoled = o.DarkVariantAmoled;
        DarkVariant = o.DarkVariant;
        LightVariant = o.LightVariant;
        UseSystemAccent = o.UseSystemAccent;
        Greetings = o.Greetings;
        ShowHints = o.ShowHints;
        TerminalFontSize = o.TerminalFontSize;
        TerminalFont = o.TerminalFont;
        TerminalScrollback = o.TerminalScrollback;
        TerminalCopyOnSelect = o.TerminalCopyOnSelect;
        LockOnMinimize = o.LockOnMinimize;
        LockOnSessionLock = o.LockOnSessionLock;
        LockOnSleep = o.LockOnSleep;
        ReauthToReveal = o.ReauthToReveal;
        ExportWithMasterPassword = o.ExportWithMasterPassword;
        ConfirmConnect = o.ConfirmConnect;
        ShowStatusBar = o.ShowStatusBar;
        Animations = o.Animations;
        AnimationSpeed = o.AnimationSpeed;
        UiScale = o.UiScale;
        AutoReconnect = o.AutoReconnect;
        ReconnectAttempts = o.ReconnectAttempts;
        Compact = o.Compact;
        ShowHostInTree = o.ShowHostInTree;
        AutoFolderColors = o.AutoFolderColors;
        RootSort = o.RootSort;
        FoldersFirst = o.FoldersFirst;
        OpenBehavior = o.OpenBehavior;
        ConfirmCloseSessions = o.ConfirmCloseSessions;
        ClipboardClearSeconds = o.ClipboardClearSeconds;
        AutoLockMinutes = o.AutoLockMinutes;
        BackupKeep = o.BackupKeep;
        TrashRetentionDays = o.TrashRetentionDays;
        OpenWebExternal = o.OpenWebExternal;
        FilesShowHidden = o.FilesShowHidden;
        FolderSync = o.FolderSync;
        SyncExcludes = o.SyncExcludes;
        FtpServer = new FtpServerSettings
        {
            Folder = o.FtpServer.Folder, Port = o.FtpServer.Port, User = o.FtpServer.User, Anonymous = o.FtpServer.Anonymous,
            ReadOnly = o.FtpServer.ReadOnly, Tls = o.FtpServer.Tls, PassiveFrom = o.FtpServer.PassiveFrom, PassiveTo = o.FtpServer.PassiveTo
        };
        LanSync = new LanSyncSettings
        {
            Port = o.LanSync.Port, DeviceName = o.LanSync.DeviceName,
            Peers = [.. o.LanSync.Peers.Select(x => new LanSyncPeer { DeviceId = x.DeviceId, Name = x.Name, Address = x.Address, Port = x.Port, LastSyncUtc = x.LastSyncUtc })]
        };
        Features = new Dictionary<string, bool>(o.Features);
        TreeAnimation = o.TreeAnimation;
        RailColors = o.RailColors;
        HomeSections = [.. o.HomeSections];
        HomeHidden = [.. o.HomeHidden];
        DashboardHidden = [.. o.DashboardHidden];
        DashboardListCount = o.DashboardListCount;
        DashboardFullscreen = o.DashboardFullscreen;
        LiveStatus = o.LiveStatus;
        LiveStatusSeconds = o.LiveStatusSeconds;
        TrayIcon = o.TrayIcon;
        CloseToTray = o.CloseToTray;
        UpdateCheck = o.UpdateCheck;
        ConnectionHistory = o.ConnectionHistory;
    }

    /// <summary>Home page sections in order, completed with sections added in later versions.</summary>
    public IReadOnlyList<string> HomeOrder()
    {
        var list = HomeSections.Where(HomeLayout.DefaultOrder.Contains).Distinct().ToList();
        foreach (var s in HomeLayout.DefaultOrder.Where(s => !list.Contains(s))) list.Add(s);
        return list;
    }
}

/// <summary>Theme part of the settings (.nexode kind "theme").</summary>
public sealed class ThemeExport
{
    public int ThemeNames { get; set; } = Nexode.App.Services.ThemeNames.Current;
    public ThemeMode Theme { get; set; }
    public bool DarkVariantAmoled { get; set; }
    public PaletteVariant DarkVariant { get; set; }
    public PaletteVariant LightVariant { get; set; }
    public bool UseSystemAccent { get; set; }
    public string Accent { get; set; } = Palette.DefaultAccent;
    public bool Compact { get; set; }
}

/// <summary>Stored options of the local FTP server.</summary>
public sealed class FtpServerSettings
{
    public string? Folder { get; set; }
    public int Port { get; set; } = 2121;
    public string User { get; set; } = "nexode";
    public bool Anonymous { get; set; }
    public bool ReadOnly { get; set; } = true;
    public bool Tls { get; set; } = true;
    public int PassiveFrom { get; set; } = 50000;
    public int PassiveTo { get; set; } = 50100;
}

/// <summary>LAN sync between own computers (feature "lansync"). The sync password is never stored.</summary>
public sealed class LanSyncSettings
{
    public int Port { get; set; } = 47810;
    public string? DeviceName { get; set; }
    public List<LanSyncPeer> Peers { get; set; } = [];
}

/// <summary>A computer this one has synced with.</summary>
public sealed class LanSyncPeer
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public string Address { get; set; } = "";
    public int Port { get; set; }
    public DateTime? LastSyncUtc { get; set; }
}

/// <summary>
/// Theme names changed in 0.0.6: the old "Dark" is now "Nexode" (same look) and the old "Shadow" is now "Dark" (same
/// look); "Shadow" is a new medium gray. Files without "themeNames" are translated before they are read.
/// </summary>
public static class ThemeNames
{
    public const int Current = 2;

    public static void Upgrade(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is not System.Text.Json.Nodes.JsonObject o || o["themeNames"] is not null) return;
        string? Map(string? v) => v switch { "Dark" => "Nexode", "Shadow" => "Dark", _ => v };
        string? MapVariant(string? v) => v == "Shadow" ? "Dark" : v;
        if (o["theme"] is System.Text.Json.Nodes.JsonValue t && t.TryGetValue<string>(out var theme)) o["theme"] = Map(theme);
        if (o["darkVariant"] is System.Text.Json.Nodes.JsonValue d && d.TryGetValue<string>(out var dv)) o["darkVariant"] = MapVariant(dv);
        else o["darkVariant"] = "Default"; // older files without the field: keep the Nexode look for System/Auto
        // Status dots in the tree were always on before they became an optional feature (0.0.6): keep them.
        if (o["features"] is not System.Text.Json.Nodes.JsonObject features) o["features"] = features = new System.Text.Json.Nodes.JsonObject();
        features["treestatus"] ??= true;
        o["themeNames"] = Current;
    }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(ThemeExport))]
internal sealed partial class AppJson : JsonSerializerContext;
