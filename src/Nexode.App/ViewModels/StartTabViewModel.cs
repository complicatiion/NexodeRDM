using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Core.Search;

namespace Nexode.App.ViewModels;

public sealed record EntryTile(Entry Entry, string Icon, IBrush? IconBrush, string Name, string? Detail, string KindText, bool IsFavorite)
{
    public bool IsWeb => Entry.Kind == EntryKind.Web;
}

public sealed record KindTile(EntryKind Kind, string Icon, string Title);

/// <summary>New-tab page: quick connect / search, favorites, recent entries, quick actions.</summary>
public sealed partial class StartTabViewModel : TabViewModel
{
    private readonly Guid _instance = Guid.NewGuid();

    public StartTabViewModel()
    {
        Title = Loc.T("Tab.Start");
        Icon = "house";
        NewGreeting();
        Refresh();
    }

    [ObservableProperty]
    private string _greeting = "";

    /// <summary>Picks a new greeting (start, language or setting change).</summary>
    public void NewGreeting()
    {
        Greeting = AppHost.Settings.Greetings ? Greetings.Pick(DateTime.Now, AppHost.Settings.RecentGreetings) : Greetings.Plain(DateTime.Now);
        AppHost.Settings.Save();
    }

    public override string Key => "start:" + _instance;
    public override TabViewModel Recreate() => new StartTabViewModel();

    /// <summary>True until the user typed something – such a tab is replaced when an entry opens.</summary>
    public bool IsPristine => string.IsNullOrEmpty(Query);

    [ObservableProperty]
    private string _query = "";

    public ObservableCollection<EntryTile> Results { get; } = [];
    public IReadOnlyList<EntryTile> Favorites { get; private set; } = [];
    public IReadOnlyList<EntryTile> Recent { get; private set; } = [];
    public IReadOnlyList<KindTile> NewKinds { get; private set; } = [];

    private static IReadOnlyList<KindTile> BuildKinds() =>
        new[] { EntryKind.Rdp, EntryKind.Ssh, EntryKind.Sftp, EntryKind.Ftp, EntryKind.Vnc, EntryKind.Telnet, EntryKind.Web, EntryKind.PowerShell, EntryKind.Folder }
            .Where(Features.KindEnabled).Take(8).Select(k => new KindTile(k, EntryKinds.Icon(k), Loc.Kind(k))).ToList();

    public bool HasQuery => !string.IsNullOrWhiteSpace(Query);
    public bool HasResults => Results.Count > 0;
    public bool HasFavorites => Favorites.Count > 0;
    public IReadOnlyList<ProfileItem> Profiles { get; private set; } = [];
    public bool HasProfiles => Profiles.Count > 0;
    /// <summary>Tiles always reach the right edge (like the "New" grid): 2–4 columns.</summary>
    public int FavoriteColumns => Math.Clamp(Favorites.Count, 2, 4);
    public int ProfileColumns => Math.Clamp(Profiles.Count, 2, 4);
    public bool HasRecent => Recent.Count > 0;

    // ---- sections (order and visibility from the home page settings)
    private static bool Section(string id) => !AppHost.Settings.HomeHidden.Contains(id);
    public bool ShowSearch => Section("search");
    public bool ShowProfiles => Section("profiles") && HasProfiles;
    public bool ShowFavorites => Section("favorites") && HasFavorites;
    public bool ShowRecent => Section("recent");
    public bool ShowNew => Section("new");
    public bool ShowRecentOrNew => ShowRecent || ShowNew;
    public bool ShowStatus => Section("status") && LiveStatus.IsActive;
    public bool ShowDashboard => Section("dashboard") && Features.On(Features.Monitoring);

    /// <summary>Figures and live status for the optional dashboard sections.</summary>
    public DashboardTabViewModel? Dash { get; private set; }

    public void UpdateStatus() => Dash?.UpdateStatus();
    public bool IsSourceEmpty => AppHost.Workspace.Tree.Count == 0;
    public Entry? QuickEntry => HasQuery ? EntryKinds.FromUri(Query) : null;
    public bool HasQuickEntry => QuickEntry is not null;
    public string QuickText => QuickEntry is { } q ? Loc.F("Start.CreateFrom", Loc.Kind(q.Kind), q.Host ?? q.Url) : "";

    partial void OnQueryChanged(string value)
    {
        UpdateResults();
        OnPropertyChanged(nameof(HasQuery));
        OnPropertyChanged(nameof(IsPristine));
        OnPropertyChanged(nameof(QuickEntry));
        OnPropertyChanged(nameof(HasQuickEntry));
        OnPropertyChanged(nameof(QuickText));
    }

    private string _language = Loc.Language;

    public override void Refresh()
    {
        Title = Loc.T("Tab.Start");
        if (_language != Loc.Language)
        {
            _language = Loc.Language;
            NewGreeting();
        }

        NewKinds = BuildKinds();
        OnPropertyChanged(nameof(NewKinds));
        var ws = AppHost.Workspace;
        Favorites = ws.Tree.All.Where(e => e.IsFavorite).OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance).Take(24).Select(Tile).ToList();
        Recent = ws.Tree.All.Where(e => e.LastUsedUtc is not null && !e.IsContainer).OrderByDescending(e => e.LastUsedUtc).Take(8).Select(Tile).ToList();
        UpdateResults();
        Profiles = ws.Profiles.Select(p => new ProfileItem(p)).ToList();
        OnPropertyChanged(nameof(Profiles));
        OnPropertyChanged(nameof(HasProfiles));
        OnPropertyChanged(nameof(ProfileColumns));
        OnPropertyChanged(nameof(FavoriteColumns));
        OnPropertyChanged(nameof(Favorites));
        OnPropertyChanged(nameof(Recent));
        OnPropertyChanged(nameof(HasFavorites));
        OnPropertyChanged(nameof(HasRecent));
        OnPropertyChanged(nameof(IsSourceEmpty));
        if (ShowStatus || ShowDashboard)
        {
            Dash ??= new DashboardTabViewModel();
            Dash.Refresh();
        }

        foreach (var n in new[] { nameof(Dash), nameof(ShowSearch), nameof(ShowProfiles), nameof(ShowFavorites), nameof(ShowRecent), nameof(ShowNew), nameof(ShowRecentOrNew), nameof(ShowStatus), nameof(ShowDashboard) })
        {
            OnPropertyChanged(n);
        }

        LayoutChanged?.Invoke();
    }

    /// <summary>Section order or visibility changed (the view reorders its sections).</summary>
    public event Action? LayoutChanged;

    private void UpdateResults()
    {
        Results.Clear();
        if (HasQuery)
        {
            var ws = AppHost.Workspace;
            var q = SearchQuery.Parse(Query);
            foreach (var e in ws.Tree.All.Select(e => (e, s: q.Score(e, ws.Resolver))).Where(x => x.s > 0)
                         .OrderByDescending(x => x.s).ThenBy(x => x.e.Name, Core.Tree.NaturalComparer.Instance).Take(10))
            {
                Results.Add(Tile(e.e));
            }
        }

        OnPropertyChanged(nameof(HasResults));
    }

    private static EntryTile Tile(Entry e)
    {
        var ws = AppHost.Workspace;
        var eff = ws.Resolver.Resolve(e);
        IBrush? brush = eff.ColorHue is { } h && (e.Kind == EntryKind.Folder || e.ColorHue is not null) ? AppHost.Theme.FolderBrush(h) : null;
        var detail = e.Kind == EntryKind.Web ? eff.Url : eff.Host ?? ws.Tree.Path(e);
        return new EntryTile(e, eff.Icon, brush, string.IsNullOrEmpty(e.Name) ? Loc.T("Unnamed") : e.Name, detail, Loc.Kind(e.Kind), e.IsFavorite);
    }

    [RelayCommand]
    private void OpenTile(EntryTile tile) => Owner?.Actions.Open(tile.Entry);

    [RelayCommand]
    private void OpenInBrowser(EntryTile tile) => Owner?.Actions.OpenInBrowser(tile.Entry);

    [RelayCommand]
    private void OpenStatus(StatusRow row) => Owner?.Actions.Open(row.Entry);

    public override void OnClosed() => Dash?.OnClosed();

    [RelayCommand]
    private Task OpenProfile(ProfileItem profile) => Owner?.Actions.OpenProfileAsync(profile.Profile) ?? Task.CompletedTask;

    [RelayCommand]
    private void ShowTile(EntryTile tile) => Owner?.Actions.ShowDetails(tile.Entry);

    [RelayCommand]
    private async Task Submit()
    {
        if (Results.FirstOrDefault() is { } first)
        {
            Owner?.Actions.Open(first.Entry);
            return;
        }

        if (QuickEntry is { } entry && Owner is not null) await Owner.Actions.NewEntryAsync(entry.Kind, Owner.Window?.SelectedEntryContainer(), entry);
    }

    [RelayCommand]
    private async Task CreateQuick()
    {
        if (QuickEntry is { } entry && Owner is not null) await Owner.Actions.NewEntryAsync(entry.Kind, Owner.Window?.SelectedEntryContainer(), entry);
    }

    [RelayCommand]
    private async Task NewKind(KindTile tile)
    {
        if (Owner is not null) await Owner.Actions.NewEntryAsync(tile.Kind, Owner.Window?.SelectedEntryContainer());
    }

    [RelayCommand]
    private Task Import() => Owner?.Actions.ImportAsync(null) ?? Task.CompletedTask;

    [RelayCommand]
    private void LoadSample()
    {
        DemoData.Create(AppHost.Workspace);
        Owner?.ShowToast(Loc.T("Toast.SampleLoaded"));
    }
}
