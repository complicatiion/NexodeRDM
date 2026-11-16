using System.Text.Json;
using Avalonia.Controls;
using Nexode.Core.Files;
using Nexode.Core.Model;
using Nexode.Core.Tree;
using Nexode.Protocols;
using Nexode.Protocols.Terminal;

namespace Nexode.App.Services;

/// <summary>
/// System information per entry (optional feature "sysinfo"): fetched on request only, kept in the personal state
/// folder of this computer (state/hostinfo.json), never in a data source or an export.
/// </summary>
public static class HostInfoService
{
    private static readonly Lock Gate = new();
    private static Dictionary<Guid, HostInfo>? _cache;

    private static string CacheFile => Path.Combine(AppPaths.StateDirectory, "hostinfo.json");

    public static event Action<Guid>? Changed;

    /// <summary>"ssh" or "winrm" for entries that can deliver system information; null otherwise.</summary>
    public static string? MethodFor(Entry entry, Effective eff)
    {
        if (string.IsNullOrWhiteSpace(eff.Host)) return null;
        return entry.Kind switch
        {
            EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp => "ssh",
            EntryKind.PowerShell => PowerShellLaunch.Transport(new ConnectParams { Host = "", Port = 0, Settings = entry.Settings }) == "ssh" ? "ssh" : "winrm",
            EntryKind.Rdp => "winrm",
            EntryKind.Host when eff.Os == OsFamily.Windows => "winrm",
            EntryKind.Host when eff.Os is OsFamily.Linux or OsFamily.MacOS or OsFamily.Unix => "ssh",
            _ => null
        };
    }

    public static HostInfo? Get(Guid id)
    {
        lock (Gate) return Load().GetValueOrDefault(id);
    }

    public static void Remove(Guid id)
    {
        lock (Gate)
        {
            if (!Load().Remove(id)) return;
            Save();
        }

        Changed?.Invoke(id);
    }

    /// <summary>Connects (credentials, jump host and host key check as for a session) and stores the result.</summary>
    public static async Task<HostInfo> FetchAsync(Entry entry, Window owner, ViewModels.EntryActions actions, CancellationToken ct)
    {
        var eff = AppHost.Workspace.Resolver.Resolve(entry);
        var method = MethodFor(entry, eff) ?? throw new InvalidOperationException(Localization.Loc.T("SysInfo.NotSupported"));
        var overrides = new Dictionary<string, string>();
        // Hosts and RDP entries: the default port of the protocol used, not the entry's own port.
        if (entry.Kind is EntryKind.Rdp or EntryKind.Host) overrides["session.port"] = method == "ssh" ? "22" : "5985";
        var p = await ConnectionBuilder.BuildAsync(entry, owner, actions, overrides) ?? throw new OperationCanceledException();
        var prompts = new SessionPrompts(() => owner);
        HostInfo info;
        if (method == "ssh")
        {
            var (tp, tunnel) = await Tunnels.ApplyAsync(entry, p, owner, actions, prompts, ct);
            using (tunnel)
            {
                info = await Task.Run(() => HostProbe.SshAsync(tp, prompts, AppHost.KnownHosts, ct), ct);
            }
        }
        else
        {
            var windows = OperatingSystem.IsWindows();
            var exe = PowerShellLaunch.FindExecutable(p, ExternalLauncher.Which, windows, windows ? Environment.SystemDirectory : null)
                      ?? throw new InvalidOperationException(Localization.Loc.T(windows ? "Ps.NoPowerShell" : "Ps.NoPwsh"));
            info = await HostProbe.WinRmAsync(p, exe, ct);
        }

        lock (Gate)
        {
            Load()[entry.Id] = info;
            Save();
        }

        AppLog.Write($"system info fetched for {entry.Id} via {info.Source}");
        Changed?.Invoke(entry.Id);
        return info;
    }

    private static Dictionary<Guid, HostInfo> Load()
    {
        if (_cache is not null) return _cache;
        try
        {
            _cache = File.Exists(CacheFile)
                ? JsonSerializer.Deserialize(File.ReadAllText(CacheFile), NexodeJson.Default.DictionaryGuidHostInfo) ?? []
                : [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("hostinfo cache unreadable: " + ex.Message);
            _cache = [];
        }

        return _cache;
    }

    private static void Save()
    {
        try
        {
            var tmp = CacheFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_cache, NexodeJson.Default.DictionaryGuidHostInfo));
            File.Move(tmp, CacheFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("hostinfo cache not written: " + ex.Message);
        }
    }
}
