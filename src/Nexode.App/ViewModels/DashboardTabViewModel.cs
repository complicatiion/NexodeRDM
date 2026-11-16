using System.Globalization;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.ViewModels;

public sealed record Kpi(string Title, string Value, string Icon, string? Hint = null);

public sealed record KindCount(string Icon, string Title, int Count, double Fraction);

/// <summary>Entry with its live status (dashboard and home page lists).</summary>
public sealed record StatusRow(Entry Entry, string Icon, string Name, string? Detail, ReachState State, string LatencyText)
{
    public bool IsOnline => State == ReachState.Online;
    public bool IsOffline => State == ReachState.Offline;
    public bool HasState => State is ReachState.Online or ReachState.Offline;
}

/// <summary>Dashboard cards (ids used by the settings).</summary>
public static class DashboardCards
{
    public static readonly string[] All = ["kpis", "status", "kinds", "environments", "source", "recent", "favorites", "history"];

    public static string Icon(string id) => id switch
    {
        "kpis" => "gauge",
        "status" => "activity",
        "kinds" => "chart-column",
        "environments" => "flag",
        "source" => "database",
        "recent" => "history",
        "favorites" => "heart",
        "history" => "scroll-text",
        _ => "circle"
    };
}

/// <summary>Overview of the data source with live status; cards and list lengths follow the dashboard settings.</summary>
public sealed partial class DashboardTabViewModel : TabViewModel
{
    public DashboardTabViewModel()
    {
        Icon = "layout-dashboard";
        ConnectionHistory.Changed += OnHistoryChanged;
        Refresh();
    }

    public override string Key => "dashboard";
    public override TabViewModel Recreate() => new DashboardTabViewModel();

    public IReadOnlyList<Kpi> Kpis { get; private set; } = [];
    public IReadOnlyList<KindCount> Kinds { get; private set; } = [];
    public IReadOnlyList<KindCount> Environments { get; private set; } = [];
    public IReadOnlyList<EntryTile> Recent { get; private set; } = [];
    public IReadOnlyList<StatusRow> Favorites { get; private set; } = [];
    public IReadOnlyList<StatusRow> Offline { get; private set; } = [];
    public IReadOnlyList<HistoryRow> History { get; private set; } = [];
    public string SourceName => AppHost.Workspace.Info?.Name ?? "";
    public string SourcePath => AppHost.Workspace.Info?.Path ?? "";
    public string SourceSize { get; private set; } = "";
    public string BackupText { get; private set; } = "";
    public string VaultText { get; private set; } = "";
    public bool HasRecent => Recent.Count > 0;
    public bool HasKinds => Kinds.Count > 0;
    public bool HasEnvironments => Environments.Count > 0;
    public bool HasFavorites => Favorites.Count > 0;
    public bool HasOffline => Offline.Count > 0;
    public bool HasHistory => History.Count > 0;

    public int OnlineCount { get; private set; }
    public int OfflineCount { get; private set; }
    public int CheckedCount => OnlineCount + OfflineCount;
    public double OnlineFraction => CheckedCount == 0 ? 0 : OnlineCount / (double)CheckedCount;
    public bool StatusEnabled => LiveStatus.IsActive;
    public bool StatusDisabled => !StatusEnabled;
    public string StatusSummary => !StatusEnabled ? Loc.T("Status.Off")
        : LiveStatus.LastRunUtc is null ? Loc.T("Status.Checking")
        : Loc.F("Status.Summary", OnlineCount, OfflineCount, LiveStatus.LastRunUtc.Value.ToLocalTime().ToString("t", CultureInfo.CurrentCulture));
    public bool AllOnline => StatusEnabled && CheckedCount > 0 && OfflineCount == 0;

    private static bool Shown(string card) => !AppHost.Settings.DashboardHidden.Contains(card);
    public bool ShowKpis => Shown("kpis");
    public bool ShowStatus => Shown("status");
    public bool ShowKinds => Shown("kinds");
    public bool ShowEnvironments => Shown("environments");
    public bool ShowSource => Shown("source");
    public bool ShowRecent => Shown("recent");
    public bool ShowFavorites => Shown("favorites");
    public bool ShowHistory => Shown("history");
    public bool IsKioskWindow => Owner?.IsKiosk == true;

    private static int ListCount => Math.Clamp(AppHost.Settings.DashboardListCount, 3, 30);

    private void OnHistoryChanged() => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);

    public override void Refresh()
    {
        Title = Loc.T("Tab.Dashboard");
        var ws = AppHost.Workspace;
        var all = ws.Tree.All.ToList();
        var connections = all.Count(e => EntryKinds.IsConnection(e.Kind));
        Kpis =
        [
            new(Loc.T("Kpi.Connections"), connections.ToString(CultureInfo.CurrentCulture), "plug-zap"),
            new(Loc.T("Kpi.Hosts"), all.Count(e => e.Kind == EntryKind.Host).ToString(CultureInfo.CurrentCulture), "server"),
            new(Loc.T("Kpi.Folders"), all.Count(e => e.Kind == EntryKind.Folder).ToString(CultureInfo.CurrentCulture), "folder"),
            new(Loc.T("Kpi.Favorites"), all.Count(e => e.IsFavorite).ToString(CultureInfo.CurrentCulture), "heart"),
            new(Loc.T("Kpi.Credentials"), all.Count(e => e.Kind == EntryKind.Credential).ToString(CultureInfo.CurrentCulture), "key-round"),
            new(Loc.T("Kpi.Production"), all.Count(e => !e.IsContainer && ws.Resolver.Resolve(e).Environment == "production").ToString(CultureInfo.CurrentCulture), "flag")
        ];

        var byKind = all.Where(e => !e.IsContainer || e.Kind == EntryKind.Host).GroupBy(e => e.Kind).OrderByDescending(g => g.Count()).ToList();
        var max = Math.Max(1, byKind.Select(g => g.Count()).DefaultIfEmpty(0).Max());
        Kinds = byKind.Select(g => new KindCount(EntryKinds.Icon(g.Key), Loc.Kind(g.Key), g.Count(), g.Count() / (double)max)).ToList();

        var envs = all.Where(e => !e.IsContainer).GroupBy(e => ws.Resolver.Resolve(e).Environment).Where(g => Envs.Get(g.Key) is not null).ToList();
        var maxEnv = Math.Max(1, envs.Select(g => g.Count()).DefaultIfEmpty(0).Max());
        Environments = envs.OrderBy(g => Envs.Get(g.Key)!.Order).Select(g => new KindCount("flag", Envs.Name(g.Key)!, g.Count(), g.Count() / (double)maxEnv)).ToList();

        Recent = all.Where(e => e.LastUsedUtc is not null && !e.IsContainer).OrderByDescending(e => e.LastUsedUtc).Take(ListCount)
            .Select(e => new EntryTile(e, ws.Resolver.Resolve(e).Icon, null, e.Name, ws.Resolver.Resolve(e).Host ?? ws.Resolver.Resolve(e).Url, Loc.Kind(e.Kind), e.IsFavorite)).ToList();

        History = ConnectionHistory.All().AsEnumerable().Reverse().Take(ListCount).Select(r =>
        {
            var local = r.StartUtc.ToLocalTime();
            var text = r.Result switch { HistoryResult.Failed => Loc.T("History.Failed"), HistoryResult.External => Loc.T("History.External"), _ => r.EndUtc is null ? Loc.T("History.Open") : Loc.T("History.Ended") };
            return new HistoryRow(r, EntryKinds.Icon(r.Kind), r.Name, r.Target, local.ToString("g", CultureInfo.CurrentCulture), "", text, "", "");
        }).ToList();

        try
        {
            var info = string.IsNullOrEmpty(SourcePath) ? null : new FileInfo(SourcePath);
            SourceSize = info is { Exists: true } ? $"{info.Length / 1024.0:N0} KB" : "";
            var dir = Path.Combine(AppPaths.BackupDirectory, ws.Store?.Id ?? "-");
            var backups = Directory.Exists(dir) ? new DirectoryInfo(dir).GetFiles("*.db") : [];
            BackupText = backups.Length == 0 ? Loc.T("Dash.NoBackup")
                : Loc.F("Dash.Backups", backups.Length, backups.Max(b => b.LastWriteTime).ToString("g", CultureInfo.CurrentCulture));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            SourceSize = "";
        }

        VaultText = ws.HasPassword ? Loc.T("Dash.VaultPassword") : ws.Vault?.IsWeak == true ? Loc.T("Dash.VaultWeak") : Loc.T("Dash.VaultSystem");
        UpdateStatus(notify: false);
        OnPropertyChanged(string.Empty);
    }

    private static StatusRow Row(Entry e)
    {
        var eff = AppHost.Workspace.Resolver.Resolve(e);
        var s = LiveStatus.Get(e.Id);
        return new StatusRow(e, eff.Icon, string.IsNullOrEmpty(e.Name) ? Loc.T("Unnamed") : e.Name, EntryKinds.HasUrl(e.Kind) ? eff.Url : eff.Host,
            s?.State ?? ReachState.Unknown, s is { State: ReachState.Online, Millis: { } ms } ? ms.ToString(CultureInfo.CurrentCulture) + " ms" : "");
    }

    /// <summary>Live status changed: counts, offline list and favorites.</summary>
    public void UpdateStatus(bool notify = true)
    {
        var ws = AppHost.Workspace;
        var states = LiveStatus.All;
        OnlineCount = states.Values.Count(s => s.State == ReachState.Online);
        OfflineCount = states.Values.Count(s => s.State == ReachState.Offline);
        Offline = states.Where(p => p.Value.State == ReachState.Offline).Select(p => ws.Tree.Get(p.Key)).OfType<Entry>()
            .OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance).Take(ListCount).Select(Row).ToList();
        Favorites = ws.Tree.All.Where(e => e.IsFavorite && !e.IsContainer).OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance).Take(ListCount).Select(Row).ToList();
        if (!notify) return;
        foreach (var n in new[] { nameof(OnlineCount), nameof(OfflineCount), nameof(CheckedCount), nameof(OnlineFraction), nameof(StatusSummary), nameof(AllOnline),
                     nameof(Offline), nameof(HasOffline), nameof(Favorites), nameof(HasFavorites), nameof(StatusEnabled), nameof(StatusDisabled) })
        {
            OnPropertyChanged(n);
        }
    }

    [RelayCommand]
    private void OpenEntry(object? item)
    {
        var entry = item switch { StatusRow r => r.Entry, EntryTile t => t.Entry, HistoryRow h => AppHost.Workspace.Tree.Get(h.Record.EntryId), _ => null };
        if (entry is not null) Owner?.Actions.Open(entry);
    }

    [RelayCommand]
    private async Task CheckNow()
    {
        if (!LiveStatus.IsActive) return;
        await LiveStatus.RunAsync();
    }

    [RelayCommand]
    private void OpenSettings() => Owner?.OpenSettingsPage("dashboard");

    [RelayCommand]
    private void OpenWindow() => AppHost.Windows.OpenDashboard();

    public override void OnClosed() => ConnectionHistory.Changed -= OnHistoryChanged;
}
