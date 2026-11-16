using System.Collections.ObjectModel;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Nexode.App.ViewModels;

/// <summary>A tab of a main window (start page, entry, settings …). Sessions follow in later versions.</summary>
public abstract partial class TabViewModel : ObservableObject
{
    /// <summary>Identity for "focus existing" (e.g. "entry:{id}", "settings").</summary>
    public abstract string Key { get; }

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _icon = "app-window";

    [ObservableProperty]
    private IBrush? _iconBrush;

    [ObservableProperty]
    private IBrush? _stripeBrush;

    [ObservableProperty]
    private bool _isSelected;

    /// <summary>Environment dot next to the title (only when the stripe shows the entry color).</summary>
    [ObservableProperty]
    private IBrush? _envBrush;

    public bool HasStripe => StripeBrush is not null;
    public bool HasEnvDot => EnvBrush is not null;

    partial void OnStripeBrushChanged(IBrush? value) => OnPropertyChanged(nameof(HasStripe));
    partial void OnEnvBrushChanged(IBrush? value) => OnPropertyChanged(nameof(HasEnvDot));

    /// <summary>Stripe = entry color (own or inherited), else the environment color; the environment then shows as a dot.</summary>
    protected void SetColors(Nexode.Core.Tree.Effective eff)
    {
        var env = Nexode.App.Services.Envs.Brush(eff.Environment);
        if (eff.ColorHue is { } hue)
        {
            StripeBrush = Nexode.App.Services.AppHost.Theme.FolderBrush(hue);
            EnvBrush = env;
        }
        else
        {
            StripeBrush = env;
            EnvBrush = null;
        }
    }

    public virtual MainWindowViewModel? Owner { get; set; }

    /// <summary>Recreates the tab (Ctrl+Shift+T, move to another window).</summary>
    public abstract TabViewModel Recreate();

    /// <summary>Called when the workspace changed.</summary>
    public virtual void Refresh()
    {
    }

    /// <summary>False when the tab should close itself (e.g. its entry was deleted).</summary>
    public virtual bool IsValid => true;

    /// <summary>The tab was closed for good (disconnect sessions, unsubscribe events).</summary>
    public virtual void OnClosed()
    {
    }

    /// <summary>Instance that continues in another window (sessions move as they are, pages are recreated).</summary>
    public virtual TabViewModel ForTransfer() => Recreate();

    /// <summary>Theme or accent changed (colors of icons, terminal palette).</summary>
    public virtual void OnThemeChanged() => Refresh();

    /// <summary>Live sessions ask before closing.</summary>
    public virtual bool IsSession => false;

    /// <summary>Can be put into a split view.</summary>
    public virtual bool CanSplit => false;
}

/// <summary>Browser-like tab strip of one window.</summary>
public sealed partial class TabsViewModel : ObservableObject
{
    private readonly MainWindowViewModel _owner;
    private readonly Stack<TabViewModel> _closed = new();

    public TabsViewModel(MainWindowViewModel owner) => _owner = owner;

    public ObservableCollection<TabViewModel> Items { get; } = [];

    [ObservableProperty]
    private TabViewModel? _selected;

    partial void OnSelectedChanged(TabViewModel? oldValue, TabViewModel? newValue)
    {
        if (oldValue is not null) oldValue.IsSelected = false;
        if (newValue is not null)
        {
            newValue.IsSelected = true;
            newValue.Refresh();
        }
    }

    public void Add(TabViewModel tab, bool select = true, int? index = null)
    {
        tab.Owner = _owner;
        var at = index ?? (Selected is null ? Items.Count : Items.IndexOf(Selected) + 1);
        Items.Insert(Math.Clamp(at, 0, Items.Count), tab);
        if (select) Selected = tab;
    }

    public TabViewModel OpenOrFocus(string key, Func<TabViewModel> factory, bool forceNew = false)
    {
        if (!forceNew && Items.FirstOrDefault(t => t.Key == key) is { } existing)
        {
            Selected = existing;
            return existing;
        }

        var tab = factory();
        // Replace an untouched extra start page ("+" tab) instead of piling up tabs; the home tab (first) stays.
        if (Selected is StartTabViewModel { IsPristine: true } start && tab is not StartTabViewModel && Items.IndexOf(start) > 0)
        {
            var index = Items.IndexOf(start);
            Items.Remove(start);
            Add(tab, true, index);
            return tab;
        }

        Add(tab);
        return tab;
    }

    public void OpenStart() => Add(new StartTabViewModel(), true, Items.Count == 0 ? 0 : null);

    /// <summary>Home = the start page at the left edge: focused (moved back to the first place) or created there.</summary>
    public void GoHome()
    {
        if (Items.OfType<StartTabViewModel>().FirstOrDefault() is not { } home)
        {
            Add(new StartTabViewModel(), true, 0);
            return;
        }

        var index = Items.IndexOf(home);
        if (index > 0) Items.Move(index, 0);
        Selected = home;
    }

    public void Close(TabViewModel tab)
    {
        var index = Items.IndexOf(tab);
        if (index < 0) return;
        Items.RemoveAt(index);
        tab.OnClosed();
        _closed.Push(tab);
        if (Selected == tab)
        {
            Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        }

        if (Items.Count == 0) OpenStart();
    }

    /// <summary>Removes without remembering (moved to another window).</summary>
    public void Detach(TabViewModel tab)
    {
        var index = Items.IndexOf(tab);
        if (index < 0) return;
        Items.RemoveAt(index);
        if (Selected == tab) Selected = Items.Count == 0 ? null : Items[Math.Min(index, Items.Count - 1)];
        if (Items.Count == 0) OpenStart();
    }

    public void CloseOthers(TabViewModel keep)
    {
        foreach (var t in Items.Where(t => t != keep).ToList()) Close(t);
    }

    public void CloseRight(TabViewModel from)
    {
        var index = Items.IndexOf(from);
        foreach (var t in Items.Skip(index + 1).ToList()) Close(t);
    }

    public void Reopen()
    {
        while (_closed.Count > 0)
        {
            var tab = _closed.Pop().Recreate();
            if (!tab.IsValid) continue;
            Add(tab);
            return;
        }
    }

    public void Cycle(int delta)
    {
        if (Items.Count == 0) return;
        var index = Selected is null ? 0 : Items.IndexOf(Selected);
        Selected = Items[((index + delta) % Items.Count + Items.Count) % Items.Count];
    }

    public void SelectIndex(int index)
    {
        if (Items.Count == 0) return;
        Selected = index >= 8 || index >= Items.Count ? Items[^1] : Items[index];
    }

    public void Move(TabViewModel tab, int newIndex)
    {
        var old = Items.IndexOf(tab);
        if (old < 0) return;
        newIndex = Math.Clamp(newIndex, 0, Items.Count - 1);
        if (old != newIndex) Items.Move(old, newIndex);
    }

    /// <summary>Window closes: end all sessions.</summary>
    public void CloseAll()
    {
        foreach (var t in Items.ToList()) t.OnClosed();
        Items.Clear();
    }

    public void RefreshAll()
    {
        foreach (var t in Items.ToList())
        {
            if (!t.IsValid)
            {
                Items.Remove(t);
                t.OnClosed();
                if (Selected == t) Selected = Items.LastOrDefault();
                continue;
            }

            t.Refresh();
        }

        if (Items.Count == 0) OpenStart();
    }
}
