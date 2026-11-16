using System.Text.Json;
using Avalonia.Threading;
using Nexode.Core.Files;
using Nexode.Core.Sync;
using Nexode.Protocols.LanSync;

namespace Nexode.App.Services;

/// <summary>One line of the LAN sync log.</summary>
public sealed record LanSyncEvent(DateTime Utc, string Kind, string Peer, string Detail);

/// <summary>Outcome of one sync, from this computer's point of view.</summary>
public sealed record LanSyncOutcome(string PeerName, int Added, int Updated, int Deleted, int Received, int Sent);

/// <summary>
/// LAN sync between own computers (optional feature "lansync"). Syncs the open personal data source with the one open
/// on the other computer; team data sources do not take part (they have their own sharing). The sync password lives
/// in memory only while "allow sync" is on; both vaults must be unlocked. A backup is made before changes are written.
/// </summary>
public static class LanSyncService
{
    public const int MinPasswordLength = 10;
    private const int MaxEvents = 200;
    private static readonly Lock Gate = new();
    private static readonly List<LanSyncEvent> EventList = [];
    private static LanSyncServer? _server;
    private static int _busy;

    static LanSyncService()
    {
        AppDomain.CurrentDomain.ProcessExit += (_, _) => Stop();
        Features.Changed += () =>
        {
            if (!Features.On(Features.LanSync)) Stop();
        };
    }

    public static event Action? Changed;

    public static bool IsListening => _server?.IsRunning == true;
    public static int Port => _server?.Port ?? AppHost.Settings.LanSync.Port;
    public static string DeviceName => AppHost.Settings.LanSync.DeviceName is { Length: > 0 } n ? n : Environment.MachineName;

    public static IReadOnlyList<LanSyncEvent> Events
    {
        get
        {
            lock (Gate) return EventList.ToList();
        }
    }

    private static void Log(string kind, string peer, string detail = "")
    {
        lock (Gate)
        {
            EventList.Insert(0, new LanSyncEvent(DateTime.UtcNow, kind, peer, detail));
            if (EventList.Count > MaxEvents) EventList.RemoveRange(MaxEvents, EventList.Count - MaxEvents);
        }

        AppLog.Write($"lan sync {kind} {peer} {detail}".TrimEnd());
        Changed?.Invoke();
    }

    /// <summary>Starts accepting syncs from other computers that know <paramref name="password"/>.</summary>
    public static void Start(string password, int port)
    {
        Stop();
        var server = new LanSyncServer(port, password, ServeAsync,
            () => new LanSyncAnnouncement { DeviceId = AppHost.Settings.DeviceId, Name = DeviceName, Port = _server?.Port ?? port });
        server.Activity += (kind, address, detail) =>
        {
            Log(kind, address, detail);
            if (kind == "stopped") Stop();
        };
        server.Start();
        _server = server;
        Log("listen", "", server.Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
    }

    public static void Stop()
    {
        var s = Interlocked.Exchange(ref _server, null);
        if (s is null) return;
        s.Dispose();
        Log("stop", "");
    }

    public static Task<IReadOnlyList<LanSyncAnnouncement>> DiscoverAsync(CancellationToken ct) =>
        LanSyncChannel.DiscoverAsync(TimeSpan.FromSeconds(2), ct);

    /// <summary>Syncs with the computer at <paramref name="address"/>; both sides end up with the merged data.</summary>
    public static async Task<LanSyncOutcome> SyncWithAsync(string address, int port, string password, CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) throw new InvalidOperationException(Localization.Loc.T("LanSync.Busy"));
        try
        {
            using var channel = await LanSyncChannel.ConnectAsync(address, port, password, ct);
            var own = await OwnSnapshotAsync();
            await channel.SendJsonAsync(own, NexodeJson.Default.LanSnapshot, ct);
            var remote = await channel.ReceiveJsonAsync(NexodeJson.Default.LanSnapshot, ct);
            if (own.Refused is { } mine) throw new InvalidOperationException(Refusal(mine, false));
            if (Check(remote, own) is { } refused) throw new InvalidOperationException(Refusal(refused, true));
            var outcome = await ApplyAsync(remote, address, port, own.Entries.Count);
            Log("synced", remote.DeviceName, Summary(outcome));
            return outcome;
        }
        catch (LanSyncAuthException)
        {
            Log("auth-failed", address);
            throw;
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Server side: receive, answer with the own snapshot, apply.</summary>
    private static async Task ServeAsync(LanSyncChannel channel, CancellationToken ct)
    {
        var remote = await channel.ReceiveJsonAsync(NexodeJson.Default.LanSnapshot, ct);
        if (Interlocked.Exchange(ref _busy, 1) == 1)
        {
            await channel.SendJsonAsync(new LanSnapshot { DeviceId = AppHost.Settings.DeviceId, DeviceName = DeviceName, Refused = "busy" }, NexodeJson.Default.LanSnapshot, ct);
            return;
        }

        try
        {
            var own = await OwnSnapshotAsync();
            await channel.SendJsonAsync(own, NexodeJson.Default.LanSnapshot, ct);
            if (own.Refused is not null || Check(remote, own) is not null)
            {
                Log("refused", remote.DeviceName, own.Refused ?? remote.Refused ?? "");
                return;
            }

            var address = (channel.Remote as System.Net.IPEndPoint)?.Address.ToString() ?? "";
            var outcome = await ApplyAsync(remote, address, null, own.Entries.Count);
            Log("synced", remote.DeviceName, Summary(outcome));
        }
        finally
        {
            Volatile.Write(ref _busy, 0);
        }
    }

    /// <summary>Reason this pair cannot sync, or null.</summary>
    private static string? Check(LanSnapshot remote, LanSnapshot own)
    {
        if (remote.Refused is { } r) return r;
        if (remote.Protocol != LanSyncMerge.ProtocolVersion) return "version";
        if (remote.DeviceId == own.DeviceId) return "self";
        return null;
    }

    private static string Refusal(string code, bool remote) =>
        Localization.Loc.T((remote ? "LanSync.RemoteRefused." : "LanSync.Refused.") + code) is var t && !t.StartsWith("LanSync.", StringComparison.Ordinal)
            ? t
            : Localization.Loc.F("LanSync.RefusedOther", code);

    private static Task<LanSnapshot> OwnSnapshotAsync() => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var (snap, refused) = AppHost.Workspace.BuildLanSnapshot(DeviceName, AppHost.Version);
        if (AppHost.Workspace.IsReadOnly) refused ??= "readonly";
        return snap ?? new LanSnapshot { DeviceId = AppHost.Settings.DeviceId, DeviceName = DeviceName, AppVersion = AppHost.Version, Refused = refused };
    }).GetTask();

    private static Task<LanSyncOutcome> ApplyAsync(LanSnapshot remote, string address, int? port, int sent) => Dispatcher.UIThread.InvokeAsync(() =>
    {
        var ws = AppHost.Workspace;
        var sourceId = ws.Store?.Id ?? "";
        var known = LoadKnown(sourceId, remote.DeviceId);
        var result = LanSyncMerge.Merge(ws.AllEntries, remote, known, ws.CategoryList, ws.EnvironmentList, ws.Profiles, ws.Templates);
        if (!result.IsEmpty)
        {
            try
            {
                ws.BackupNow();
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
            {
                AppLog.Write("lan sync backup failed: " + ex.Message);
            }

            ws.ApplyLanMerge(result);
        }

        SaveKnown(sourceId, remote.DeviceId, result.KnownIds);
        RememberPeer(remote, address, port);
        return new LanSyncOutcome(remote.DeviceName, result.Added, result.Updated, result.Deleted, remote.Entries.Count, sent);
    }).GetTask();

    private static void RememberPeer(LanSnapshot remote, string address, int? port)
    {
        var s = AppHost.Settings;
        var peer = s.LanSync.Peers.FirstOrDefault(p => p.DeviceId == remote.DeviceId);
        if (peer is null)
        {
            peer = new LanSyncPeer { DeviceId = remote.DeviceId, Port = LanSyncChannel.DefaultPort };
            s.LanSync.Peers.Add(peer);
        }

        peer.Name = remote.DeviceName;
        if (address.Length > 0) peer.Address = address;
        if (port is { } p) peer.Port = p;
        peer.LastSyncUtc = DateTime.UtcNow;
        s.Save();
    }

    public static void ForgetPeer(string deviceId)
    {
        var s = AppHost.Settings;
        s.LanSync.Peers.RemoveAll(p => p.DeviceId == deviceId);
        s.Save();
        try
        {
            foreach (var f in Directory.EnumerateFiles(KnownDirectory, "*_" + Safe(deviceId) + ".json")) File.Delete(f);
        }
        catch (IOException)
        {
        }

        Changed?.Invoke();
    }

    private static string Summary(LanSyncOutcome o) => $"+{o.Added} ~{o.Updated} -{o.Deleted}";

    // Ids both computers knew at the last sync, per data source and peer (state folder, never synced).
    private static string KnownDirectory => Directory.CreateDirectory(Path.Combine(AppPaths.StateDirectory, "lansync")).FullName;

    private static string Safe(string s) => string.Concat(s.Where(char.IsAsciiLetterOrDigit));

    private static string KnownFile(string sourceId, string peerId) => Path.Combine(KnownDirectory, $"{Safe(sourceId)}_{Safe(peerId)}.json");

    private static HashSet<Guid>? LoadKnown(string sourceId, string peerId)
    {
        try
        {
            var f = KnownFile(sourceId, peerId);
            return File.Exists(f) ? JsonSerializer.Deserialize(File.ReadAllText(f), NexodeJson.Default.ListGuid)?.ToHashSet() : null;
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void SaveKnown(string sourceId, string peerId, HashSet<Guid> ids)
    {
        try
        {
            File.WriteAllText(KnownFile(sourceId, peerId), JsonSerializer.Serialize(ids.ToList(), NexodeJson.Default.ListGuid));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("lan sync state not written: " + ex.Message);
        }
    }
}
