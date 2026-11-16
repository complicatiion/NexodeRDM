using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Core.Search;

namespace Nexode.App.ViewModels;

public enum TreeMode
{
    All,
    Favorites,
    Recent,
    Profiles
}

/// <summary>Connection tree of one window: flattened rows, search, favorites and recent views.</summary>
public sealed partial class TreeViewModel : ObservableObject
{
    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };

    public TreeViewModel()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Refresh();
        };
    }

    public ObservableCollection<TreeRow> Rows { get; } = [];

    [ObservableProperty]
    private string _searchText = "";

    [ObservableProperty]
    private TreeMode _mode = TreeMode.All;

    [ObservableProperty]
    private bool _isEmpty;

    [ObservableProperty]
    private bool _noResults;

    public bool IsSearching => !string.IsNullOrWhiteSpace(SearchText);
    public bool IsSourceEmpty => AppHost.Workspace.Tree.Count == 0;

    /// <summary>Raised before rows are replaced so the view can keep the selection.</summary>
    public event Action? Refreshing;
    public event Action? Refreshed;

    partial void OnSearchTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsSearching));
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnModeChanged(TreeMode value) => Refresh();

    public void Refresh() => Refresh(null);

    /// <summary>Live status changed: update the dots of the visible rows.</summary>
    public void UpdateStatus()
    {
        foreach (var r in Rows) r.Status = LiveStatus.Get(r.Id)?.State ?? Nexode.Protocols.ReachState.Unknown;
    }

    private static bool Animate => Motion.Enabled && AppHost.Settings.TreeAnimation;

    /// <param name="appearBelow">Folder that was expanded: its new rows fade in.</param>
    private void Refresh(Guid? appearBelow)
    {
        // Rows below the expanded folder fade in (it was collapsed, so they are all new).
        var appear = appearBelow is { } below ? AppHost.Workspace.Tree.Descendants(below).Select(e => e.Id).ToHashSet() : null;
        Refreshing?.Invoke();
        var ws = AppHost.Workspace;
        var tree = ws.Tree;
        var resolver = ws.Resolver;
        var rows = new List<TreeRow>();
        var query = SearchQuery.Parse(SearchText);

        if (Mode == TreeMode.Profiles)
        {
            // Shown by the profile list of the window.
        }
        else if (Mode == TreeMode.Favorites || Mode == TreeMode.Recent)
        {
            var source = Mode == TreeMode.Favorites
                ? tree.All.Where(e => e.IsFavorite).OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance)
                : AppHost.Settings.RecentMostUsed
                    ? tree.All.Where(e => !e.IsContainer && Usage.Count(e) > 0).OrderByDescending(Usage.Count).ThenByDescending(e => e.LastUsedUtc).Take(40)
                    : tree.All.Where(e => e.LastUsedUtc is not null && !e.IsContainer).OrderByDescending(e => e.LastUsedUtc).Take(40);
            foreach (var e in source)
            {
                var eff = resolver.Resolve(e);
                if (!query.IsEmpty && query.Score(e, resolver, eff) == 0) continue;
                var hint = Mode == TreeMode.Recent && AppHost.Settings.RecentMostUsed
                    ? Localization.Loc.F("Recent.Uses", Usage.Count(e)) + " · " + tree.Path(e)
                    : tree.Path(e);
                rows.Add(new TreeRow(e, eff, 0, false, false, 0, false, hint));
            }
        }
        else if (!query.IsEmpty)
        {
            // Matches plus their ancestors, expanded.
            var matches = new HashSet<Guid>();
            var visible = new HashSet<Guid>();
            foreach (var e in tree.All)
            {
                if (query.Score(e, resolver) <= 0) continue;
                matches.Add(e.Id);
                visible.Add(e.Id);
                foreach (var a in tree.Ancestors(e)) visible.Add(a.Id);
            }

            void Walk(Guid? parent, int depth)
            {
                foreach (var c in tree.Children(parent))
                {
                    if (!visible.Contains(c.Id)) continue;
                    var hasVisibleChildren = tree.Children(c.Id).Any(x => visible.Contains(x.Id));
                    rows.Add(new TreeRow(c, resolver.Resolve(c), depth, hasVisibleChildren, hasVisibleChildren, tree.CountItems(c.Id), matches.Contains(c.Id), null));
                    Walk(c.Id, depth + 1);
                }
            }

            Walk(null, 0);
        }
        else
        {
            void Walk(Guid? parent, int depth)
            {
                foreach (var c in tree.Children(parent))
                {
                    var hasChildren = tree.HasChildren(c.Id);
                    var expanded = hasChildren && c.IsExpanded;
                    rows.Add(new TreeRow(c, resolver.Resolve(c), depth, hasChildren, expanded, c.IsContainer ? tree.CountItems(c.Id) : 0, false, null));
                    if (expanded) Walk(c.Id, depth + 1);
                }
            }

            Walk(null, 0);
        }

        if (appear is not null)
        {
            foreach (var r in rows.Where(r => appear.Contains(r.Id))) r.Appear = true;
        }

        Rows.Clear();
        foreach (var r in rows) Rows.Add(r);
        IsEmpty = tree.Count == 0;
        NoResults = !IsEmpty && rows.Count == 0;
        OnPropertyChanged(nameof(IsSourceEmpty));
        Refreshed?.Invoke();
    }

    public void SetExpanded(Entry entry, bool expanded)
    {
        if (entry.IsExpanded == expanded) return;
        entry.IsExpanded = expanded;
        AppHost.Workspace.SaveState(entry);
        if (!Animate || Mode != TreeMode.All || IsSearching)
        {
            Refresh();
            return;
        }

        if (expanded)
        {
            Refresh(entry.Id);
            return;
        }

        // Collapse: the children fade out first.
        var ids = AppHost.Workspace.Tree.Descendants(entry.Id).Select(e => e.Id).ToHashSet();
        var leaving = Rows.Where(r => ids.Contains(r.Id)).ToList();
        if (leaving.Count == 0 || leaving.Count > 400)
        {
            Refresh();
            return;
        }

        foreach (var r in leaving) r.Leaving = true;
        DispatcherTimer.RunOnce(Refresh, Motion.Duration(120));
    }

    /// <summary>Expands all ancestors so the entry becomes visible.</summary>
    public void Reveal(Guid id)
    {
        var ws = AppHost.Workspace;
        if (ws.Tree.Get(id) is not { } e) return;
        var changed = false;
        foreach (var a in ws.Tree.Ancestors(e).Where(a => !a.IsExpanded))
        {
            a.IsExpanded = true;
            ws.SaveState(a);
            changed = true;
        }

        if (Mode != TreeMode.All || IsSearching)
        {
            SearchText = "";
            Mode = TreeMode.All;
        }

        if (changed) Refresh();
    }

    public void ExpandAll(bool expand)
    {
        var ws = AppHost.Workspace;
        foreach (var e in ws.Tree.All.Where(e => e.IsContainer && e.IsExpanded != expand))
        {
            e.IsExpanded = expand;
            ws.SaveState(e);
        }

        Refresh();
    }
}
