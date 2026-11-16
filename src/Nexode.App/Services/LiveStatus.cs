using System.Collections.Concurrent;
using Avalonia.Threading;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.Services;

/// <summary>Result of the last reachability check of an entry.</summary>
public sealed record StatusInfo(ReachState State, int? Millis, DateTime CheckedUtc);

/// <summary>
/// Live status: checks the connections of the active data source in the background (TCP connect to the service port,
/// ping for hosts without a port) and reports changes for the status dots in the tree and on the dashboard.
/// Entries behind a jump host are not checked (not reachable directly).
/// </summary>
public static class LiveStatus
{
    private static readonly ConcurrentDictionary<Guid, StatusInfo> Results = new();
    private static CancellationTokenSource? _cts;
    private static DispatcherTimer? _timer;
    private static bool _running;

    /// <summary>Raised on the UI thread after a round of checks.</summary>
    public static event Action? Changed;

    public static DateTime? LastRunUtc { get; private set; }

    /// <summary>Live status switched on and the monitoring feature enabled.</summary>
    public static bool IsActive => AppHost.Settings.LiveStatus && Features.On(Features.Monitoring);

    public static StatusInfo? Get(Guid id) => Results.TryGetValue(id, out var s) ? s : null;

    public static IReadOnlyDictionary<Guid, StatusInfo> All => Results;

    /// <summary>Host and port to check for an entry (null = not checkable).</summary>
    public static (string Host, int? Port)? Target(Entry e)
    {
        if (e.Kind is EntryKind.Folder or EntryKind.Credential or EntryKind.Note or EntryKind.Command or EntryKind.Serial) return null;
        if (Tunnels.JumpHostOf(e) is not null) return null;
        var eff = AppHost.Workspace.Resolver.Resolve(e);
        if (EntryKinds.HasUrl(e.Kind))
        {
            return Uri.TryCreate(eff.Url, UriKind.Absolute, out var u) && u.Host.Length > 0 ? (u.Host, u.Port) : null;
        }

        if (string.IsNullOrWhiteSpace(eff.Host)) return null;
        var port = e.Kind == EntryKind.Host ? null : eff.Port ?? EntryKinds.DefaultPort(e.Kind);
        if (e.Kind == EntryKind.PowerShell && e.Port is null)
            port = Nexode.Protocols.Terminal.PowerShellLaunch.DefaultPort(e.Setting(SettingKeys.PsTransport) ?? "winrm");
        return (eff.Host.Trim(), port);
    }

    public static void Start()
    {
        _timer?.Stop();
        if (!IsActive)
        {
            Stop();
            return;
        }

        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(Math.Clamp(AppHost.Settings.LiveStatusSeconds, 15, 3600)) };
        _timer.Tick += (_, _) => _ = RunAsync();
        _timer.Start();
        _ = RunAsync();
    }

    public static void Stop()
    {
        _timer?.Stop();
        _timer = null;
        _cts?.Cancel();
        Results.Clear();
        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }

    /// <summary>Data source switched: forget old results, check again.</summary>
    public static void Reset()
    {
        Results.Clear();
        if (IsActive) _ = RunAsync();
        else Changed?.Invoke();
    }

    public static async Task RunAsync()
    {
        if (_running || !IsActive || AppHost.Workspace?.Tree is null) return;
        _running = true;
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        try
        {
            var targets = AppHost.Workspace.Tree.All.Select(e => (e.Id, Target: Target(e))).Where(t => t.Target is not null).ToList();
            // Same host:port once, however many entries point to it.
            var unique = targets.Select(t => t.Target!.Value).Distinct().ToList();
            var answers = new ConcurrentDictionary<(string, int?), (ReachState, int?)>();
            using var gate = new SemaphoreSlim(16);
            await Task.WhenAll(unique.Select(async t =>
            {
                await gate.WaitAsync(cts.Token).ConfigureAwait(false);
                try
                {
                    answers[t] = await StatusProbe.CheckAsync(t.Host, t.Port, TimeSpan.FromSeconds(3), cts.Token).ConfigureAwait(false);
                }
                finally
                {
                    gate.Release();
                }
            })).ConfigureAwait(false);

            var now = DateTime.UtcNow;
            var ids = new HashSet<Guid>();
            foreach (var (id, target) in targets)
            {
                if (!answers.TryGetValue(target!.Value, out var a) || a.Item1 == ReachState.Unknown) continue;
                Results[id] = new StatusInfo(a.Item1, a.Item2, now);
                ids.Add(id);
            }

            foreach (var gone in Results.Keys.Where(k => !ids.Contains(k)).ToList()) Results.TryRemove(gone, out _);
            LastRunUtc = now;
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _running = false;
        }

        Dispatcher.UIThread.Post(() => Changed?.Invoke());
    }
}
