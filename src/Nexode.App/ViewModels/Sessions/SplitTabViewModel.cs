using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;

namespace Nexode.App.ViewModels.Sessions;

public enum SplitLayout
{
    /// <summary>Side by side.</summary>
    Columns,
    /// <summary>One above the other.</summary>
    Rows,
    /// <summary>One large pane left, the others stacked right.</summary>
    Main,
    /// <summary>2 × 2.</summary>
    Grid
}

/// <summary>Two to four sessions side by side in one tab (sessions keep running when they move in or out).</summary>
public sealed partial class SplitTabViewModel : TabViewModel
{
    public const int MaxPanes = 4;
    private readonly Guid _instance = Guid.NewGuid();
    private MainWindowViewModel? _owner;

    public SplitTabViewModel(IEnumerable<SessionTabViewModel>? panes = null, SplitLayout layout = SplitLayout.Columns)
    {
        _layout = layout;
        Icon = "layout-panel-left";
        Panes.CollectionChanged += (_, _) =>
        {
            UpdateHeader();
            OnPropertyChanged(nameof(CanAdd));
            OnPropertyChanged(nameof(Count));
            RefreshFeatures();
        };
        foreach (var p in panes ?? []) Add(p);
        UpdateHeader();
    }

    public ObservableCollection<SessionTabViewModel> Panes { get; } = [];

    /// <summary>"Send input to all terminals": typed input of one terminal pane goes to every terminal pane.</summary>
    [ObservableProperty]
    private bool _broadcast;

    public bool CanBroadcast => Services.Features.On(Services.Features.Broadcast) && Panes.OfType<TerminalTabViewModel>().Count() >= 2;

    public int TerminalCount => Panes.OfType<TerminalTabViewModel>().Count();

    /// <summary>Feature switches or panes changed: broadcasting ends when it is no longer possible.</summary>
    public void RefreshFeatures()
    {
        if (!CanBroadcast) Broadcast = false;
        OnPropertyChanged(nameof(CanBroadcast));
        OnPropertyChanged(nameof(TerminalCount));
    }

    public void SendToOthers(TerminalTabViewModel from, byte[] data)
    {
        if (!Broadcast) return;
        foreach (var t in Panes.OfType<TerminalTabViewModel>())
        {
            if (t != from) t.SendMirrored(data);
        }
    }
    public int Count => Panes.Count;
    public bool CanAdd => Panes.Count < MaxPanes;
    public override string Key => $"split:{_instance}";
    public override bool IsSession => true;
    public override bool IsValid => Panes.Count > 0;
    public override TabViewModel ForTransfer() => this;

    public override TabViewModel Recreate() =>
        new SplitTabViewModel(Panes.Select(p => p.Recreate()).OfType<SessionTabViewModel>(), Layout);

    [ObservableProperty]
    private SplitLayout _layout;

    [ObservableProperty]
    private SessionTabViewModel? _active;

    /// <summary>Raised when the panes or the layout changed (the view rebuilds its grid).</summary>
    public event Action? Changed;

    partial void OnLayoutChanged(SplitLayout value) => Changed?.Invoke();

    /// <summary>Owner propagates to the panes (dialogs, actions of the window the split lives in).</summary>
    public override MainWindowViewModel? Owner
    {
        get => _owner;
        set
        {
            _owner = value;
            foreach (var p in Panes) p.Owner = value;
        }
    }

    public bool Add(SessionTabViewModel pane)
    {
        if (!CanAdd || Panes.Contains(pane)) return false;
        pane.Split = this;
        pane.Owner = Owner;
        pane.IsSelected = true;
        Panes.Add(pane);
        pane.PropertyChanged += OnPanePropertyChanged;
        Active ??= pane;
        Changed?.Invoke();
        return true;
    }

    /// <summary>Removes a pane; <paramref name="close"/> ends its session. The split closes with its last pane.</summary>
    public void Remove(SessionTabViewModel pane, bool close)
    {
        if (!Panes.Remove(pane)) return;
        pane.PropertyChanged -= OnPanePropertyChanged;
        pane.Split = null;
        if (close) pane.OnClosed();
        if (Active == pane) Active = Panes.FirstOrDefault();
        Changed?.Invoke();
        if (Panes.Count == 0) Owner?.Tabs.Close(this);
    }

    /// <summary>Moves a pane into its own tab next to the split.</summary>
    [RelayCommand]
    private void PopOut(SessionTabViewModel pane)
    {
        if (Owner is not { } owner) return;
        var index = owner.Tabs.Items.IndexOf(this) + 1;
        Remove(pane, close: false);
        owner.Tabs.Add(pane, true, index);
    }

    [RelayCommand]
    private void ClosePane(SessionTabViewModel pane) => Remove(pane, close: true);

    [RelayCommand]
    private void SetLayout(string layout) => Layout = Enum.TryParse<SplitLayout>(layout, out var l) ? l : SplitLayout.Columns;

    /// <summary>Ctrl+Alt+arrows: next / previous pane.</summary>
    public void Cycle(int delta)
    {
        if (Panes.Count == 0) return;
        var i = Active is null ? 0 : Panes.IndexOf(Active);
        Active = Panes[((i + delta) % Panes.Count + Panes.Count) % Panes.Count];
    }

    private void OnPanePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(Title) or nameof(SessionTabViewModel.State)) UpdateHeader();
    }

    private void UpdateHeader()
    {
        Title = Panes.Count == 0 ? Loc.T("Split.Title") : string.Join(" | ", Panes.Select(p => p.Title));
        StripeBrush = Panes.Select(p => p.StripeBrush).FirstOrDefault(b => b is not null);
        EnvBrush = Panes.Select(p => p.EnvBrush).FirstOrDefault(b => b is not null);
    }

    public override void Refresh()
    {
        foreach (var p in Panes) p.Refresh();
        UpdateHeader();
    }

    public override void OnThemeChanged()
    {
        foreach (var p in Panes) p.OnThemeChanged();
    }

    public override void OnClosed()
    {
        foreach (var p in Panes.ToList())
        {
            p.PropertyChanged -= OnPanePropertyChanged;
            p.OnClosed();
        }
    }
}
