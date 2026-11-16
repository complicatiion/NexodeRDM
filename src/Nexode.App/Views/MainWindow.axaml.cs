using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.App.ViewModels.Sessions;
using Nexode.Core.Model;
using static Nexode.App.Views.MenuBuilder;

namespace Nexode.App.Views;

public partial class MainWindow : Window
{
    private static readonly DataFormat<string> DragFormat = DataFormat.CreateStringApplicationFormat("nexode-entries");
    private static readonly DataFormat<string> PinFormat = DataFormat.CreateStringApplicationFormat("nexode-pin");
    private PointerPressedEventArgs? _treePress;
    private PointerPressedEventArgs? _pinPress;
    private Point? _pinDragStart;
    private PinItem? _pinDrag;
    private bool _closeConfirmed;
    private Point? _dragStart;
    private TreeRow? _dragRow;
    private TreeRow? _dropRow;
    private List<Guid> _keepSelection = [];
    private readonly DispatcherTimer _hoverExpand = new() { Interval = TimeSpan.FromMilliseconds(700) };

    private DispatcherTimer? _treeTween;

    public MainWindow()
    {
        InitializeComponent();
        InitPlacement();
        ModeHeader.SizeChanged += (_, _) => AlignEmptyStates();
        TabContent.PageTransition = new TabTransition();

        TreeList.AddHandler(PointerPressedEvent, TreePointerPressed, RoutingStrategies.Tunnel);
        TreeList.AddHandler(PointerMovedEvent, TreePointerMoved, RoutingStrategies.Tunnel);
        TreeList.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
        TreeList.DoubleTapped += TreeDoubleTapped;
        TreeList.KeyDown += TreeKeyDown;
        TreeList.ContextRequested += TreeContextRequested;
        TreeList.AddHandler(DragDrop.DragOverEvent, TreeDragOver);
        TreeList.AddHandler(DragDrop.DragLeaveEvent, (_, _) => ClearDrop());
        TreeList.AddHandler(DragDrop.DropEvent, TreeDrop);
        AddHandler(DragDrop.DropEvent, WindowDrop);
        AddHandler(KeyDownEvent, WindowKeyDown, RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => App.NoteActivity(), RoutingStrategies.Tunnel);
        _hoverExpand.Tick += (_, _) =>
        {
            _hoverExpand.Stop();
            if (_dropRow is { HasChildren: true, IsExpanded: false } row && Vm is { } vm) vm.Tree.SetExpanded(row.Entry, true);
        };

        PinArea.AddHandler(DragDrop.DragOverEvent, PinDragOver);
        PinArea.AddHandler(DragDrop.DragLeaveEvent, (_, _) => PinHint.IsVisible = false);
        PinArea.AddHandler(DragDrop.DropEvent, PinDrop);
        PropertyChanged += (_, e) =>
        {
            if (e.Property == WindowStateProperty && WindowState == WindowState.Minimized && AppHost.Settings.LockOnMinimize && Ws.HasPassword && !Ws.IsLocked)
            {
                Ws.Lock();
            }
        };
        Closing += OnClosingWindow;

        Ws.Changed += OnWorkspaceChangedForColumn;
        Closed += (_, _) => Ws.Changed -= OnWorkspaceChangedForColumn;
        TreeSplitter.DragCompleted += (_, _) =>
        {
            AppHost.Settings.TreeWidth = Columns.ColumnDefinitions[1].ActualWidth;
            AppHost.Settings.Save();
        };

        Opened += (_, _) =>
        {
            ApplyTreeColumn();
            ApplyMotion();
            WindowChrome.Apply(this);
        };
        DataContextChanged += (_, _) =>
        {
            if (Vm is null) return;
            Vm.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(MainWindowViewModel.TreeVisible)) ApplyTreeColumn(true);
            };
            Vm.Tabs.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName != nameof(TabsViewModel.Selected) || Vm?.Tabs.Selected is not { } selected) return;
                // Keep the active tab visible in the scrolling tab strip.
                Dispatcher.UIThread.Post(() =>
                {
                    var tab = this.GetVisualDescendants().OfType<Border>().FirstOrDefault(b => b.Classes.Contains("tab") && b.DataContext == selected);
                    tab?.BringIntoView();
                }, DispatcherPriority.Background);
            };
            Vm.Tree.Refreshing += () => _keepSelection = SelectedIds.ToList();
            Vm.Tree.Refreshed += RestoreSelection;
            Vm.Tree.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(TreeViewModel.Mode)) UpdateModeTitle();
            };
        };
    }

    private MainWindowViewModel? Vm => DataContext as MainWindowViewModel;

    /// <summary>Entries of the open tabs (for "restore last tabs" at the next start).</summary>
    private void RememberTabs()
    {
        if (Vm is not { } vm) return;
        var keys = new List<string>();
        foreach (var t in vm.Tabs.Items)
        {
            switch (t)
            {
                case SplitTabViewModel split:
                    keys.AddRange(split.Panes.Select(p => "session:" + p.EntryId));
                    break;
                case SessionTabViewModel session:
                    keys.Add("session:" + session.EntryId);
                    break;
                case EntryTabViewModel entry:
                    keys.Add("entry:" + entry.EntryId);
                    break;
                case DashboardTabViewModel:
                    keys.Add("dashboard");
                    break;
            }
        }

        AppHost.Settings.LastTabs = keys.Distinct().Take(30).ToList();
        AppHost.Settings.Save();
    }

    private WindowState _beforeKiosk = WindowState.Normal;

    /// <summary>Kiosk: fullscreen with only the tab content (no rail, tree, tab strip or status bar).</summary>
    public void ApplyKiosk(bool on)
    {
        TreePanel.IsVisible = !on;
        TreeSplitter.IsVisible = !on && Vm?.TreeVisible == true;
        if (on)
        {
            _beforeKiosk = WindowState == WindowState.FullScreen ? WindowState.Normal : WindowState;
            Columns.ColumnDefinitions[1].MinWidth = 0;
            Columns.ColumnDefinitions[1].Width = new GridLength(0);
            WindowState = WindowState.FullScreen;
        }
        else
        {
            WindowState = _beforeKiosk;
            ApplyTreeColumn(false);
        }
    }

    /// <summary>Rail width animation follows the animation settings.</summary>
    public void ApplyMotion() =>
        RailBorder.Transitions = Motion.Enabled
            ? [new Avalonia.Animation.DoubleTransition { Property = WidthProperty, Duration = Motion.Duration(180), Easing = new Avalonia.Animation.Easings.CubicEaseOut() }]
            : null;

    private void OnWorkspaceChangedForColumn()
    {
        if (Vm?.TreeVisible == true && _treeTween?.IsEnabled != true) Columns.ColumnDefinitions[1].MinWidth = TreeMinWidth();
    }
    private static Workspace Ws => AppHost.Workspace;

    private void ApplyTreeColumn() => ApplyTreeColumn(false);

    /// <summary>Shows or hides the tree column (animated); the splitter cannot squeeze it below the widest top-level name.</summary>
    private void ApplyTreeColumn(bool animate)
    {
        var column = Columns.ColumnDefinitions[1];
        var visible = Vm?.TreeVisible ?? true;
        var min = TreeMinWidth();
        var target = visible ? Math.Clamp(AppHost.Settings.TreeWidth, min, 640) : 0;
        _treeTween?.Stop();
        var from = column.ActualWidth;
        var duration = Motion.Duration(170);
        if (!animate || duration <= TimeSpan.Zero || Math.Abs(from - target) < 2 || !IsLoaded)
        {
            column.MinWidth = visible ? min : 0;
            column.Width = new GridLength(target);
            TreePanel.IsVisible = visible;
            return;
        }

        column.MinWidth = 0;
        TreePanel.IsVisible = true;
        var started = DateTime.UtcNow;
        _treeTween = new DispatcherTimer(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(12) };
        _treeTween.Tick += (_, _) =>
        {
            var t = Math.Min(1, (DateTime.UtcNow - started).TotalMilliseconds / duration.TotalMilliseconds);
            var eased = 1 - Math.Pow(1 - t, 3);
            column.Width = new GridLength(from + (target - from) * eased);
            if (t < 1) return;
            _treeTween?.Stop();
            column.MinWidth = visible ? min : 0;
            TreePanel.IsVisible = visible;
        };
        _treeTween.Start();
    }

    /// <summary>Width of the widest top-level name plus icons and badges (between 220 and 360).</summary>
    private double TreeMinWidth()
    {
        var widest = 0.0;
        var typeface = new Typeface(FontFamily, FontStyle.Normal, FontWeight.Normal);
        foreach (var e in Ws.Tree.Children(null).Where(e => e.IsContainer).Take(400))
        {
            var text = new FormattedText(e.Name, System.Globalization.CultureInfo.CurrentCulture, FlowDirection.LeftToRight, typeface, 13, null);
            widest = Math.Max(widest, text.Width);
        }

        // Stripe, expander, icon, count, heart and paddings.
        return Math.Clamp(widest + 120, 220, 360);
    }

    private void UpdateModeTitle()
    {
        if (Vm is null) return;
        // No title (the rail shows the mode); the row only holds the tools of profiles and recent.
        ModeHeader.IsVisible = Vm.Tree.Mode is TreeMode.Profiles or TreeMode.Recent;
        Dispatcher.UIThread.Post(AlignEmptyStates, DispatcherPriority.Render);
    }

    /// <summary>
    /// Empty states sit at the same height in every sidebar mode: the tool row of profiles and recent pushes the
    /// content down, so their top margin shrinks by its height.
    /// </summary>
    private void AlignEmptyStates()
    {
        var shift = ModeHeader.IsVisible ? ModeHeader.Bounds.Height + ModeHeader.Margin.Top + ModeHeader.Margin.Bottom : 0;
        static Thickness Top(Thickness m, double top) => new(m.Left, top, m.Right, m.Bottom);
        EmptyTree.Margin = Top(EmptyTree.Margin, EmptyTop - shift);
        NoResults.Margin = Top(NoResults.Margin, EmptyTop - shift);
        EmptyProfiles.Margin = Top(EmptyProfiles.Margin, ProfilesEmptyTop - shift);
    }

    private const double EmptyTop = 32;
    private const double ProfilesEmptyTop = 24;

    // ------------------------------------------------------------------ selection

    public IReadOnlyList<Guid> SelectedIds => TreeList.SelectedItems?.OfType<TreeRow>().Select(r => r.Id).ToList() ?? [];

    public Guid? SelectedEntryId => (TreeList.SelectedItem as TreeRow)?.Id;

    /// <summary>The selected folder/host, or the parent of the selected entry – where new entries go.</summary>
    public Guid? SelectedEntryContainer()
    {
        if (TreeList.SelectedItem is not TreeRow row) return null;
        return row.Entry.IsContainer ? row.Id : row.Entry.ParentId;
    }

    private List<Entry> SelectedEntries() => TreeList.SelectedItems?.OfType<TreeRow>().Select(r => r.Entry).ToList() ?? [];

    private void RestoreSelection()
    {
        if (_keepSelection.Count == 0) return;
        var rows = Vm!.Tree.Rows.Where(r => _keepSelection.Contains(r.Id)).ToList();
        TreeList.SelectedItems?.Clear();
        foreach (var r in rows) TreeList.SelectedItems?.Add(r);
    }

    public void SelectAndReveal(Guid id)
    {
        if (Vm is null) return;
        Vm.TreeVisible = true;
        Vm.Tree.Reveal(id);
        Dispatcher.UIThread.Post(() =>
        {
            var row = Vm.Tree.Rows.FirstOrDefault(r => r.Id == id);
            if (row is null) return;
            TreeList.SelectedItems?.Clear();
            TreeList.SelectedItem = row;
            TreeList.ScrollIntoView(row);
        }, DispatcherPriority.Background);
    }

    // ------------------------------------------------------------------ tree interaction

    private static TreeRow? RowFrom(object? source) =>
        (source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TreeRow;

    private TreeRow? RowAt(Point pointInList)
    {
        var hit = TreeList.InputHitTest(pointInList) as Control;
        return hit?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as TreeRow;
    }

    private void TreePointerPressed(object? sender, PointerPressedEventArgs e)
    {
        var point = e.GetCurrentPoint(TreeList);
        var row = RowFrom(e.Source);
        if (point.Properties.IsLeftButtonPressed)
        {
            _dragStart = point.Position;
            _dragRow = row;
            _treePress = e;
        }
        else if (point.Properties.IsRightButtonPressed && row is not null && TreeList.SelectedItems?.Contains(row) != true)
        {
            TreeList.SelectedItems?.Clear();
            TreeList.SelectedItem = row;
        }
        else if (point.Properties.IsMiddleButtonPressed && row is not null && Vm is { } vm)
        {
            vm.Actions.Open(row.Entry, forceNewTab: true);
            e.Handled = true;
        }
    }

    private async void TreePointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || _dragRow is null || !e.GetCurrentPoint(TreeList).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(TreeList);
        if (Math.Abs(pos.X - start.X) < 6 && Math.Abs(pos.Y - start.Y) < 6) return;
        _dragStart = null;

        var ids = TreeList.SelectedItems?.Contains(_dragRow) == true ? SelectedIds : [_dragRow.Id];
        var names = ids.Select(id => Ws.Tree.Get(id)).OfType<Entry>().Select(x => Ws.Resolver.Resolve(x).Host ?? x.Name);
        var item = new DataTransferItem();
        item.Set(DragFormat, string.Join(';', ids));
        item.SetText(string.Join(Environment.NewLine, names));
        var data = new DataTransfer();
        data.Add(item);
        if (_treePress is { } press) await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move | DragDropEffects.Copy);
        ClearDrop();
    }

    private void ClearDrop()
    {
        if (_dropRow is not null) _dropRow.DropZone = DropZone.None;
        _dropRow = null;
        _hoverExpand.Stop();
    }

#pragma warning disable CS0618
    private static List<Guid>? DraggedIds(DragEventArgs e) =>
        e.DataTransfer.TryGetValue(DragFormat) is string s ? s.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(Guid.Parse).ToList() : null;
#pragma warning restore CS0618

    private (DropZone Zone, DropTarget Target)? ComputeDrop(DragEventArgs e, out TreeRow? row)
    {
        var pos = e.GetPosition(TreeList);
        row = RowAt(pos);
        if (row is null) return (DropZone.None, DropTarget.Into(null));
        var item = TreeList.ContainerFromItem(row) as Control;
        var y = item is null ? 0.5 : e.GetPosition(item).Y / Math.Max(1, item.Bounds.Height);
        var container = row.Entry.IsContainer && Vm?.Tree.IsSearching != true && Vm?.Tree.Mode == TreeMode.All;
        DropZone zone = container
            ? y < 0.28 ? DropZone.Before : y > 0.72 && !row.IsExpanded ? DropZone.After : DropZone.Into
            : y < 0.5 ? DropZone.Before : DropZone.After;
        var target = zone switch
        {
            DropZone.Into => DropTarget.Into(row.Id),
            DropZone.Before => new DropTarget(row.Entry.ParentId, row.Id, true),
            _ => new DropTarget(row.Entry.ParentId, row.Id, false)
        };
        return (zone, target);
    }

    private void TreeDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        if (e.DataTransfer.Contains(PinFormat))
        {
            e.DragEffects = DragDropEffects.Move;
            e.Handled = true;
            return;
        }
#pragma warning restore CS0618
        var ids = DraggedIds(e);
        if (ids is null)
        {
            // Files or text from outside.
#pragma warning disable CS0618
            e.DragEffects = e.DataTransfer.Contains(DataFormat.File) || e.DataTransfer.Contains(DataFormat.Text) ? DragDropEffects.Copy : DragDropEffects.None;
#pragma warning restore CS0618
            return;
        }

        var drop = ComputeDrop(e, out var row);
        if (row != _dropRow)
        {
            ClearDrop();
            _dropRow = row;
            if (row is { HasChildren: true, IsExpanded: false }) _hoverExpand.Start();
        }

        var copy = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        if (drop is not { } d || ids.Contains(row?.Id ?? Guid.Empty) || (!copy && !Ws.CanDrop(ids, d.Target)))
        {
            e.DragEffects = DragDropEffects.None;
            if (row is not null) row.DropZone = DropZone.None;
        }
        else
        {
            e.DragEffects = copy ? DragDropEffects.Copy : DragDropEffects.Move;
            if (row is not null) row.DropZone = d.Zone;
        }

        AutoScroll(e);
        e.Handled = true;
    }

    private void AutoScroll(DragEventArgs e)
    {
        if (TreeList.Scroll is not ScrollViewer sv) return;
        var y = e.GetPosition(TreeList).Y;
        if (y < 24) sv.Offset = sv.Offset.WithY(Math.Max(0, sv.Offset.Y - 12));
        else if (y > TreeList.Bounds.Height - 24) sv.Offset = sv.Offset.WithY(sv.Offset.Y + 12);
    }

    private async void TreeDrop(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        if (e.DataTransfer.TryGetValue(PinFormat) is string pinned && Guid.TryParse(pinned, out var pinId))
        {
            Ws.SetPinned([pinId], false);
            e.Handled = true;
            return;
        }
#pragma warning restore CS0618
        var ids = DraggedIds(e);
        var drop = ComputeDrop(e, out var row);
        ClearDrop();
        e.Handled = true;
        if (Vm is not { } vm || drop is not { } d) return;

        if (ids is null)
        {
            await DropExternal(e, d.Target.Parent ?? row?.Entry.ParentId);
            return;
        }

        if (row is not null && ids.Contains(row.Id)) return;
        if (e.KeyModifiers.HasFlag(KeyModifiers.Control))
        {
            Ws.Copy(ids, d.Target, markAsCopy: false);
        }
        else if (Ws.CanDrop(ids, d.Target))
        {
            var before = ids.Select(id => Ws.Resolver.Resolve(Ws.Tree.Get(id)!).CredentialFrom).ToList();
            Ws.Move(ids, d.Target);
            var after = ids.Select(id => Ws.Tree.Get(id) is { } x ? Ws.Resolver.Resolve(x) : null).ToList();
            // Inheritance hint: the moved entry now takes its credentials from somewhere else.
            for (var i = 0; i < ids.Count; i++)
            {
                if (after[i] is { } eff && eff.CredentialFrom != before[i] && eff.CredentialFrom is { } from && Ws.Tree.Get(from) is { } src
                    && Ws.Tree.Get(ids[i])?.CredentialMode == CredentialMode.Inherit)
                {
                    vm.ShowToast(Loc.F("Toast.InheritsNow", src.Name));
                    break;
                }
            }
        }
        else
        {
            vm.ShowToast(Loc.T("Toast.CannotMoveInside"), true);
        }
    }

    private async void WindowDrop(object? sender, DragEventArgs e)
    {
        if (e.Handled || DraggedIds(e) is not null) return;
        await DropExternal(e, SelectedEntryContainer());
    }

    private async Task DropExternal(DragEventArgs e, Guid? parent)
    {
        if (Vm is not { } vm) return;
#pragma warning disable CS0618
        if (e.DataTransfer.TryGetFiles() is { } files)
        {
            foreach (var f in files)
            {
                if (f.TryGetLocalPath() is { } path && path.EndsWith(".nexode", StringComparison.OrdinalIgnoreCase))
                {
                    await vm.Actions.ImportAsync(path);
                    return;
                }
            }

            vm.ShowToast(Loc.T("Toast.DropUnsupported"), true);
            return;
        }

        if (e.DataTransfer.TryGetText() is { Length: > 0 } text) await vm.Actions.CreateFromTextAsync(text, parent);
#pragma warning restore CS0618
    }

    private void TreeDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (RowFrom(e.Source) is not { } row || Vm is not { } vm) return;
        if (row.Entry.IsContainer && row.HasChildren) vm.Tree.SetExpanded(row.Entry, !row.IsExpanded);
        else vm.Actions.Open(row.Entry);
    }

    private void ToggleExpand(object? sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row) Vm?.Tree.SetExpanded(row.Entry, !row.IsExpanded);
        e.Handled = true;
    }

    private void ToggleFavorite(object? sender, RoutedEventArgs e)
    {
        if (RowFrom(sender) is { } row) Vm?.Actions.ToggleFavorite([row.Id]);
        e.Handled = true;
    }

    private void ClearSearch(object? sender, RoutedEventArgs e)
    {
        if (Vm is not null) Vm.Tree.SearchText = "";
        SearchBox.Focus();
    }

    private async void TreeKeyDown(object? sender, KeyEventArgs e)
    {
        if (Vm is not { } vm) return;
        var row = TreeList.SelectedItem as TreeRow;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        switch (e.Key)
        {
            case Key.Enter when row is not null:
                vm.Actions.Open(row.Entry, ctrl);
                break;
            case Key.Space when row is not null:
                ShowDetailsFlyout(row);
                break;
            case Key.F2 when row is not null:
                await vm.Actions.RenameAsync(row.Entry);
                break;
            case Key.Delete when SelectedIds.Count > 0:
                await vm.Actions.DeleteAsync(SelectedIds);
                break;
            case Key.Right when row is { HasChildren: true, IsExpanded: false }:
                vm.Tree.SetExpanded(row.Entry, true);
                break;
            case Key.Left when row is { HasChildren: true, IsExpanded: true }:
                vm.Tree.SetExpanded(row.Entry, false);
                break;
            case Key.Left when row?.Entry.ParentId is { } parent:
                SelectAndReveal(parent);
                break;
            case Key.C when ctrl && SelectedIds.Count > 0:
                vm.Actions.CopyEntries(SelectedIds, cut: false);
                break;
            case Key.X when ctrl && SelectedIds.Count > 0:
                vm.Actions.CopyEntries(SelectedIds, cut: true);
                break;
            case Key.V when ctrl:
                await vm.Actions.PasteAsync(row?.Id);
                break;
            case Key.D when ctrl && SelectedIds.Count > 0:
                vm.Actions.Duplicate(SelectedIds);
                break;
            case Key.E when ctrl && SelectedIds.Count > 1:
                await vm.Actions.BulkEditAsync(SelectedIds);
                break;
            case Key.E when ctrl && row is not null:
                await vm.Actions.EditAsync(row.Entry);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ShowPalette(object? sender, RoutedEventArgs e)
    {
        if (Vm is { } vm) CommandPalette.Show(this, vm);
    }

    private void WindowKeyDown(object? sender, KeyEventArgs e)
    {
        App.NoteActivity();
        if (e.Key == Key.F11 && Vm is { } kvm)
        {
            kvm.IsKiosk = !kvm.IsKiosk;
            e.Handled = true;
            return;
        }

        if (Vm is not { } vm || !e.KeyModifiers.HasFlag(KeyModifiers.Control)) return;
        switch (e.Key)
        {
            case Key.F:
                vm.TreeVisible = true;
                SearchBox.Focus();
                SearchBox.SelectAll();
                break;
            case Key.K when e.KeyModifiers.HasFlag(KeyModifiers.Shift) || !Features.On(Features.Palette):
                vm.Tabs.OpenStart();
                break;
            case Key.K:
                CommandPalette.Show(this, vm);
                break;
            case Key.N when !e.KeyModifiers.HasFlag(KeyModifiers.Shift):
                ShowNewMenu(AddButton, new RoutedEventArgs());
                break;
            case >= Key.D1 and <= Key.D9:
                vm.Tabs.SelectIndex(e.Key - Key.D1);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    // ------------------------------------------------------------------ menus

    private void ShowFlyoutMenu(Control anchor, IEnumerable<Control> items, PlacementMode placement = PlacementMode.BottomEdgeAlignedLeft)
    {
        var flyout = new MenuFlyout { Placement = placement };
        foreach (var i in items) flyout.Items.Add(i);
        flyout.ShowAt(anchor);
    }

    private void ShowNewMenu(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var parent = SelectedEntryContainer();
        var items = new List<Control>(NewItems(k => vm.Actions.NewEntryAsync(k, parent)));
        items.Insert(2, new Separator());
        items.Add(new Separator());
        items.Add(TemplateMenu(t => vm.Actions.NewFromTemplateAsync(t, parent)));
        items.Add(Item("Menu.FromClipboard", "clipboard-paste", () => _ = vm.Actions.PasteAsync(SelectedEntryId)));
        items.Add(Item("Menu.Import", "file-down", () => _ = vm.Actions.ImportAsync(null), "Ctrl+O"));
        ShowFlyoutMenu(sender as Control ?? AddButton, items, PlacementMode.BottomEdgeAlignedRight);
    }

    private void ShowSortMenu(object? sender, RoutedEventArgs e)
    {
        var s = AppHost.Settings;
        void Set(SortMode m)
        {
            s.RootSort = m;
            s.Save();
            Ws.Rebuild();
        }

        var items = new List<Control>
        {
            Check(Loc.T("Sort.Manual"), s.RootSort == SortMode.Manual, () => Set(SortMode.Manual)),
            Check(Loc.T("Sort.Name"), s.RootSort == SortMode.Name, () => Set(SortMode.Name)),
            Check(Loc.T("Sort.Kind"), s.RootSort == SortMode.Kind, () => Set(SortMode.Kind)),
            Check(Loc.T("Sort.LastUsed"), s.RootSort == SortMode.LastUsed, () => Set(SortMode.LastUsed)),
            new Separator(),
            Check(Loc.T("Sort.FoldersFirst"), s.FoldersFirst, () =>
            {
                s.FoldersFirst = !s.FoldersFirst;
                s.Save();
                Ws.Rebuild();
            })
        };
        ShowFlyoutMenu((Control)sender!, items, PlacementMode.TopEdgeAlignedLeft);
    }

    private void ShowSourceMenu(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var items = new List<Control>();
        foreach (var info in AppHost.Settings.DataSources)
        {
            var active = info.Id == Ws.Info?.Id;
            items.Add(Check(info.Name, active, () =>
            {
                if (active) return;
                _ = SwitchSource(info);
            }, new LineIcon { Icon = active ? "check" : "database", Size = 15 }));
        }

        items.Add(new Separator());
        items.Add(Item("Source.Manage", "settings", () => vm.OpenSettingsCommand.Execute(null)));
        ShowFlyoutMenu((Control)sender!, items, PlacementMode.TopEdgeAlignedLeft);
    }

    private async Task SwitchSource(DataSourceInfo info)
    {
        try
        {
            await DataSources.OpenAsync(this, info, isNew: false);
            Vm?.ShowToast(Loc.F("Toast.SourceOpened", info.Name));
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException or UnauthorizedAccessException)
        {
            await Dialog.InfoAsync(this, Loc.T("Source.Open"), Loc.F("Source.OpenFailed", ex.Message), "circle-alert");
        }
    }

    private void ShowMoreMenu(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        ShowFlyoutMenu((Control)sender!,
        [
            Item("Menu.ExpandAll", "chevron-down", () => vm.Tree.ExpandAll(true)),
            Item("Menu.CollapseAll", "chevron-right", () => vm.Tree.ExpandAll(false)),
            new Separator(),
            Item("Menu.Undo", "undo-2", () => vm.UndoCommand.Execute(null), "Ctrl+Z", Ws.CanUndo),
            Item("Tab.Trash", "trash-2", () => vm.OpenTrashCommand.Execute(null)),
            new Separator(),
            Item("Menu.BackupNow", "archive", () =>
            {
                try { vm.ShowToast(Loc.F("Toast.BackupDone", Path.GetFileName(Ws.BackupNow()))); }
                catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException) { vm.ShowToast(ex.Message, true); }
            }),
        ], PlacementMode.TopEdgeAlignedRight);
    }

    private void ShowWindowMenu(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm) return;
        ShowFlyoutMenu((Control)sender!,
        [
            Item("Menu.NewTab", "plus", () => vm.Tabs.OpenStart(), "Ctrl+T"),
            Item("Menu.NewWindow", "app-window", () => AppHost.Windows.Open(), "Ctrl+Shift+N"),
            Item("Tabs.Reopen", "undo-2", () => vm.Tabs.Reopen(), "Ctrl+Shift+T"),
            new Separator(),
            Item("Tab.Dashboard", "layout-dashboard", () => vm.OpenDashboardCommand.Execute(null), enabled: vm.ShowDashboard),
            .. Features.On(Features.FtpServer) ? new Control[] { Item("FtpServer.Title", "server-cog", () => vm.Tabs.OpenOrFocus("ftpserver", () => new FtpServerTabViewModel())) } : [],
            .. Features.On(Features.LanSync) ? new Control[] { Item("LanSync.Title", "arrow-left-right", () => vm.Tabs.OpenOrFocus("lansync", () => new LanSyncTabViewModel())) } : [],
            Item("Tab.Settings", "settings", () => vm.OpenSettingsCommand.Execute(null), "Ctrl+OemComma"),
            Item("Tab.About", "info", () => vm.OpenAboutCommand.Execute(null)),
            new Separator(),
            Item(vm.IsLocked ? "Vault.Unlock" : "Vault.Lock", vm.IsLocked ? "lock-open" : "lock", () => vm.ToggleLockCommand.Execute(null), "Ctrl+L", vm.HasPassword),
            new Separator(),
            Item("Menu.Exit", "power", () => _ = ExitAppAsync())
        ], PlacementMode.BottomEdgeAlignedRight);
    }

    private void TreeContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        if (Vm is not { } vm) return;
        var row = RowFrom(e.Source);
        var selected = row is null ? [] : SelectedEntries();
        var menu = new ContextMenu();
        foreach (var item in BuildEntryMenu(vm, selected)) menu.Items.Add(item);
        menu.Open(e.Source as Control ?? TreeList);
        e.Handled = true;
    }

    private List<Control> BuildEntryMenu(MainWindowViewModel vm, List<Entry> selected)
    {
        var a = vm.Actions;
        var ids = selected.Select(s => s.Id).ToList();
        var items = new List<Control>();

        if (selected.Count == 0)
        {
            items.Add(Sub("Menu.New", "plus", NewItems(k => a.NewEntryAsync(k, null))));
            items.Add(TemplateMenu(t => a.NewFromTemplateAsync(t, null)));
            items.Add(Item("Menu.Paste", "clipboard-paste", () => _ = a.PasteAsync(null), "Ctrl+V"));
            items.Add(new Separator());
            items.Add(Item("Menu.ExpandAll", "chevron-down", () => vm.Tree.ExpandAll(true)));
            items.Add(Item("Menu.CollapseAll", "chevron-right", () => vm.Tree.ExpandAll(false)));
            items.Add(Item("Menu.Import", "file-down", () => _ = a.ImportAsync(null), "Ctrl+O"));
            return items;
        }

        var e = selected[0];
        var single = selected.Count == 1;
        var eff = Ws.Resolver.Resolve(e);

        if (single)
        {
            items.Add(Item(e.IsContainer ? "Menu.ShowDetails" : "Menu.Open",
                e.IsContainer ? "info" : e.Kind == EntryKind.Web ? "globe" : "plug-zap", () => a.Open(e), "Enter", bold: true));
            if (e.Kind == EntryKind.Web) items.Add(Item("Menu.OpenBrowser", "external-link", () => a.OpenInBrowser(e)));
            if (!e.IsContainer)
            {
                var openWith = new List<Control>
                {
                    Item("Menu.OpenNewTab", "plus", () => a.Open(e, forceNewTab: true), "Ctrl+Enter"),
                    Item("Menu.OpenNewWindow", "app-window", () => a.OpenInNewWindow(e))
                };
                if (EntryKinds.IsConnection(e.Kind) && e.Kind is not (EntryKind.Web or EntryKind.Command or EntryKind.Database))
                {
                    openWith.Add(new Separator());
                    openWith.Add(Item("Menu.OpenEmbedded", "monitor-play", () => a.Open(e, forceNewTab: true)));
                    openWith.Add(Item("Menu.OpenExternal", "external-link", () => _ = a.OpenExternalAsync(e)));
                    openWith.Add(Item("Menu.OpenWithParams", "sliders-horizontal", () => _ = a.OpenWithParametersAsync(e)));
                    if (e.Kind is EntryKind.Ssh) openWith.Add(Item("Menu.OpenSftp", "folder-lock", () => a.OpenSftpFor(e)));
                }
                else
                {
                    openWith.Add(Item("Menu.OpenExternal", "external-link", () => _ = a.OpenExternalAsync(e)));
                }

                items.Add(Sub("Menu.OpenWith", "app-window", openWith));
            }

            var copy = new List<Control>
            {
                Item("Copy.Name", "type", () => _ = a.CopyFieldAsync(e, CopyField.Name)),
                Item("Copy.Host", "server", () => _ = a.CopyFieldAsync(e, CopyField.Host), enabled: eff.Host is not null),
                Item("Copy.Url", "globe", () => _ = a.CopyFieldAsync(e, CopyField.Url), enabled: eff.Url is not null),
                new Separator(),
                Item("Copy.Username", "user", () => _ = a.CopyFieldAsync(e, CopyField.Username), enabled: eff.Username is not null),
                Item("Copy.Password", "key-round", () => _ = a.CopyFieldAsync(e, CopyField.Password),
                    enabled: eff.SecretOwner is { } so && Ws.HasSecret(so)),
                Item("Copy.Domain", "building-2", () => _ = a.CopyFieldAsync(e, CopyField.Domain), enabled: eff.Domain is not null),
                Item("Copy.DomainUser", "users", () => _ = a.CopyFieldAsync(e, CopyField.DomainUser), enabled: eff.Domain is not null && eff.Username is not null),
                Item("Copy.Upn", "at-sign", () => _ = a.CopyFieldAsync(e, CopyField.Upn), enabled: eff.Upn is not null),
                new Separator(),
                Item("Copy.Command", "square-terminal", () => _ = a.CopyFieldAsync(e, CopyField.Command), enabled: EntryKinds.IsConnection(e.Kind))
            };
            items.Add(Sub("Menu.CopyField", "clipboard-copy", copy));
            items.Add(Item("Menu.Details", "info", () => ShowDetailsFlyout(vm.Tree.Rows.FirstOrDefault(r => r.Id == e.Id)), "Space"));
        }
        else
        {
            items.Add(Item("Menu.OpenAll", "plug-zap", () =>
            {
                foreach (var s in selected.Where(s => !s.IsContainer)) a.Open(s, forceNewTab: true);
            }, bold: true));
        }

        var allFav = selected.All(s => s.IsFavorite);
        items.Add(Item(allFav ? "Menu.Unfavorite" : "Menu.Favorite", "heart", () => a.ToggleFavorite(ids)));
        var allPinned = selected.All(s => s.IsPinned);
        items.Add(Item(allPinned ? "Menu.Unpin" : "Menu.Pin", allPinned ? "pin-off" : "pin", () => Ws.SetPinned(ids, !allPinned)));
        if (selected.Any(s => EntryKinds.IsConnection(s.Kind))) items.Add(Item("Menu.AddToProfile", "layers", () => _ = a.AddToProfileAsync(ids)));
        if (single && Features.On(Features.WakeOnLan) && (e.Setting(SettingKeys.MacAddress) is not null || Ws.Tree.Ancestors(e).Any(x => x.Setting(SettingKeys.MacAddress) is not null)))
        {
            items.Add(Sub("Menu.Wake", "power", [
                Item("Menu.WakeOnly", "power", () => _ = a.WakeAsync(e, false)),
                Item("Menu.WakeConnect", "plug-zap", () => _ = a.WakeAsync(e, true), enabled: EntryKinds.IsConnection(e.Kind))
            ]));
        }

        items.Add(new Separator());

        if (single)
        {
            var parent = e.IsContainer ? e.Id : e.ParentId;
            items.Add(Sub("Menu.Add", "plus", NewItems(k => a.NewEntryAsync(k, parent))));
            items.Add(TemplateMenu(t => a.NewFromTemplateAsync(t, parent)));
            items.Add(Item("Menu.Edit", "pencil", () => _ = a.EditAsync(e), "Ctrl+E"));
            items.Add(Item("Menu.Rename", "text-cursor-input", () => _ = a.RenameAsync(e), "F2"));
            if (EntryKinds.IsConnection(e.Kind)) items.Add(Item("Menu.History", "history", () => vm.Tabs.OpenOrFocus("history:" + e.Id, () => new HistoryTabViewModel(e.Id))));
            if (EntryKinds.IsConnection(e.Kind)) items.Add(Item("Template.SaveMenu", "layout-template", () => _ = a.SaveAsTemplateAsync(e)));
            if (e.Kind == EntryKind.Rdp && OperatingSystem.IsWindows())
                items.Add(Item("Shadow.Menu", "eye", () => _ = RdpShadowDialog.ShowAsync(vm, e)));
            if (e.Kind is EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp && Features.On(Features.SshKeys))
                items.Add(Item("Keys.InstallMenu", "key-round", () => _ = SshKeyDialog.ShowAsync(vm, e)));
        }
        else
        {
            items.Add(Item("Menu.BulkEdit", "square-pen", () => _ = a.BulkEditAsync(ids), "Ctrl+E", bold: true));
        }

        items.Add(Item("Menu.Duplicate", "copy-plus", () => a.Duplicate(ids), "Ctrl+D"));
        items.Add(Item("Menu.Cut", "scissors", () => a.CopyEntries(ids, cut: true), "Ctrl+X"));
        items.Add(Item("Menu.Copy", "copy", () => a.CopyEntries(ids, cut: false), "Ctrl+C"));
        items.Add(Item("Menu.Paste", "clipboard-paste", () => _ = a.PasteAsync(e.Id), "Ctrl+V"));
        items.Add(new Separator());

        items.Add(Sub("Menu.Color", "palette", ColorItems(single ? e.ColorHue : null, hue => a.SetColor(ids, hue))));
        items.Add(Item("Menu.Icon", "shapes", () => _ = a.PickIconAsync(ids)));
        var categories = new List<Control> { Check(Loc.T("Category.None"), single && e.CategoryId is null, () => a.SetCategory(ids, null)) };
        categories.AddRange(Ws.CategoryList.Select(c => (Control)Check(CategoryName(c), single && e.CategoryId == c.Id, () => a.SetCategory(ids, c.Id),
            new LineIcon { Icon = c.Icon, Size = 15 })));
        items.Add(Sub("Menu.Category", "tag", categories));

        var envs = new List<Control> { Check(Loc.T("Env.Inherit"), single && e.EnvironmentId is null, () => a.SetEnvironment(ids, null)) };
        foreach (var env in Ws.EnvironmentList)
        {
            var id = env.Id;
            envs.Add(Check(Envs.Name(env), single && e.EnvironmentId == id, () => a.SetEnvironment(ids, id), Dot(Envs.Brush(id)!)));
        }

        envs.Add(Check(Loc.T("Env.None"), single && e.EnvironmentId == "", () => a.SetEnvironment(ids, "")));

        items.Add(Sub("Menu.Environment", "flag", envs));

        if (single && e.IsContainer)
        {
            var sorts = new List<Control> { Check(Loc.T("Sort.Inherit"), e.SortMode is null, () => a.SetSort(e, null)) };
            foreach (var m in Enum.GetValues<SortMode>())
            {
                sorts.Add(Check(Loc.T("Sort." + m), e.SortMode == m, () => a.SetSort(e, m)));
            }

            items.Add(Sub("Menu.Sort", "arrow-up-down", sorts));
        }

        if (selected.Any(s => s.NeedsReview))
        {
            items.Add(Item("Menu.Approve", "badge-check", () => a.ApproveReview(ids)));
        }

        items.Add(Item("Menu.Export", "file-up", () => _ = a.ExportAsync(ids)));
        var others = AppHost.Settings.DataSources.Where(d => d.Id != Ws.Info?.Id && File.Exists(d.Path)).ToList();
        if (others.Count > 0)
        {
            items.Add(Sub("Menu.ToSource", "database", others.SelectMany(d => new Control[]
            {
                Item(Loc.F("Menu.MoveTo", d.Name), "arrow-right-left", () => _ = a.TransferAsync(ids, d, move: true), raw: true),
                Item(Loc.F("Menu.CopyTo", d.Name), "copy", () => _ = a.TransferAsync(ids, d, move: false), raw: true)
            }).ToList()));
        }
        items.Add(new Separator());
        items.Add(Item("Menu.Delete", "trash-2", () => _ = a.DeleteAsync(ids), "Delete", danger: true));
        return items;
    }

    private static string CategoryName(Core.Model.Category c) => CategoryNames.Of(c);

    // ------------------------------------------------------------------ details card

    private void ShowDetailsFlyout(TreeRow? row)
    {
        if (row is null || Vm is not { } vm) return;
        var container = TreeList.ContainerFromItem(row) as Control ?? TreeList;
        var card = new EntryCard(row.Entry, vm);
        var flyout = new Flyout { Content = card, Placement = PlacementMode.RightEdgeAlignedTop, ShowMode = FlyoutShowMode.Transient };
        card.CloseRequested += () => flyout.Hide();
        flyout.ShowAt(container);
    }

    // ------------------------------------------------------------------ tabs

    private static TabViewModel? TabFrom(object? sender) => (sender as Control)?.DataContext as TabViewModel;

    private void TabPressed(object? sender, PointerPressedEventArgs e)
    {
        if (TabFrom(sender) is not { } tab || Vm is not { } vm) return;
        var p = e.GetCurrentPoint(this).Properties;
        if (p.IsLeftButtonPressed) vm.Tabs.Selected = tab;
    }

    private void TabReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (e.InitialPressMouseButton == MouseButton.Middle && TabFrom(sender) is { } tab) Vm?.Tabs.Close(tab);
    }

    private void TabCloseClick(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) Vm?.Tabs.Close(tab);
        e.Handled = true;
    }

    private void TabMenuClose(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) Vm?.Tabs.Close(tab);
    }

    private void TabMenuCloseOthers(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) Vm?.Tabs.CloseOthers(tab);
    }

    private void TabMenuCloseRight(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is { } tab) Vm?.Tabs.CloseRight(tab);
    }

    private void TabMenuMoveWindow(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is not { } tab || Vm is not { } vm) return;
        vm.Tabs.Detach(tab);
        AppHost.Windows.Open(tab.ForTransfer());
    }

    private void TabMenuReopen(object? sender, RoutedEventArgs e) => Vm?.Tabs.Reopen();

    private void TabMenuSplit(object? sender, RoutedEventArgs e)
    {
        if (TabFrom(sender) is SessionTabViewModel s) Vm?.Actions.AddToSplit(s);
    }

    // ------------------------------------------------------------------ connection profiles

    private static ProfileItem? ProfileFrom(object? sender) => (sender as Control)?.DataContext as ProfileItem;

    private void ProfileDoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ProfileFrom(sender) is { } p) _ = Vm?.Actions.OpenProfileAsync(p.Profile);
    }

    private void ProfilePlay(object? sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } p) _ = Vm?.Actions.OpenProfileAsync(p.Profile);
        e.Handled = true;
    }

    private void ProfileMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } p) _ = Vm?.Actions.OpenProfileAsync(p.Profile);
    }

    private void ProfileMenuCloseSessions(object? sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } p) EntryActions.CloseProfileSessions(p.Profile);
    }

    private void ProfileMenuEdit(object? sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } p) _ = Vm?.Actions.EditProfileAsync(p.Profile);
    }

    private void ProfileMenuDelete(object? sender, RoutedEventArgs e)
    {
        if (ProfileFrom(sender) is { } p) _ = Vm?.Actions.DeleteProfileAsync(p.Profile);
    }

    // ------------------------------------------------------------------ pinned rail entries

    private static PinItem? PinFrom(object? sender) => (sender as Control)?.DataContext as PinItem;

    private void PinPressed(object? sender, PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _pinDragStart = e.GetPosition(this);
        _pinDrag = PinFrom(sender);
        _pinPress = e;
    }

    private async void PinMoved(object? sender, PointerEventArgs e)
    {
        if (_pinDragStart is not { } start || _pinDrag is not { } pin || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(this);
        if (Math.Abs(pos.X - start.X) < 6 && Math.Abs(pos.Y - start.Y) < 6) return;
        _pinDragStart = null;
        var item = new DataTransferItem();
        item.Set(PinFormat, pin.Id.ToString());
        var data = new DataTransfer();
        data.Add(item);
        if (_pinPress is not { } press) return;
        var result = await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Move);
        // Dropped outside the rail and the tree: unpin.
        if (result == DragDropEffects.None && !PinArea.IsPointerOver) Ws.SetPinned([pin.Id], false);
        _pinDrag = null;
    }

    private void PinReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (_pinDragStart is not null && e.InitialPressMouseButton == MouseButton.Left && PinFrom(sender) is { } pin) Vm?.OpenPinCommand.Execute(pin);
        _pinDragStart = null;
        _pinDrag = null;
    }

    private PinItem? PinAt(DragEventArgs e) =>
        (PinArea.InputHitTest(e.GetPosition(PinArea)) as Control)?.FindAncestorOfType<Border>(includeSelf: true) is { DataContext: PinItem p } ? p : null;

    private void PinDragOver(object? sender, DragEventArgs e)
    {
#pragma warning disable CS0618
        var ok = e.DataTransfer.Contains(PinFormat) || e.DataTransfer.Contains(DragFormat);
#pragma warning restore CS0618
        e.DragEffects = ok ? DragDropEffects.Move : DragDropEffects.None;
        PinHint.IsVisible = ok && Vm?.HasPins != true;
        e.Handled = true;
    }

    private void PinDrop(object? sender, DragEventArgs e)
    {
        PinHint.IsVisible = false;
        e.Handled = true;
        var before = PinAt(e)?.Id;
#pragma warning disable CS0618
        if (e.DataTransfer.TryGetValue(PinFormat) is string s && Guid.TryParse(s, out var id))
        {
            if (before != id) Ws.SetPinned([id], true, before);
            return;
        }
#pragma warning restore CS0618
        if (DraggedIds(e) is { Count: > 0 } ids)
        {
            Ws.SetPinned(ids, true, before);
            Vm?.ShowToast(Loc.F("Toast.Pinned", ids.Count));
        }
    }

    private void PinMenuOpen(object? sender, RoutedEventArgs e)
    {
        if (PinFrom(sender) is { } pin) Vm?.OpenPinCommand.Execute(pin);
    }

    private void PinMenuReveal(object? sender, RoutedEventArgs e)
    {
        if (PinFrom(sender) is { } pin) SelectAndReveal(pin.Id);
    }

    private void PinMenuOpening(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        if (sender is not ContextMenu menu || menu.DataContext is not PinItem pin) return;
        var target = menu.Items.OfType<MenuItem>().FirstOrDefault(m => m.Classes.Contains("pincolor"));
        if (target is null) return;
        target.Items.Clear();
        foreach (var c in MenuBuilder.PinColorItems(pin.Entry.PinHue, hue => Ws.SetPinHue(pin.Id, hue))) target.Items.Add(c);
    }

    private void PinMenuUnpin(object? sender, RoutedEventArgs e)
    {
        if (PinFrom(sender) is { } pin) Ws.SetPinned([pin.Id], false);
    }

    // ------------------------------------------------------------------ closing

    /// <summary>
    /// Quits Nexode completely (all windows, tray icon, background services) – also when "close to tray" is on.
    /// Open sessions are confirmed once for all windows.
    /// </summary>
    public async Task ExitAppAsync()
    {
        var sessions = AppHost.Windows.ViewModels.Sum(v => v.SessionCount);
        if (sessions > 0 && AppHost.Settings.ConfirmCloseSessions
            && !await Dialog.ConfirmAsync(this, Loc.T("Menu.ExitTitle"), Loc.F("Menu.ExitText", sessions), Loc.T("Menu.Exit"), danger: true, icon: "power"))
        {
            return;
        }

        foreach (var w in AppHost.Windows.Windows) w._closeConfirmed = true;
        RememberTabs();
        RememberPlacement();
        TrayService.Quit();
    }

    private async void OnClosingWindow(object? sender, WindowClosingEventArgs e)
    {
        var last = AppHost.Windows.Windows.Count(w => w.IsVisible) <= 1;
        // Last window with "close to tray": hide instead of quitting (sessions keep running).
        if (last && AppHost.Settings.CloseToTray && TrayService.IsActive && !TrayService.IsQuitting && e.CloseReason == WindowCloseReason.WindowClosing)
        {
            e.Cancel = true;
            Hide();
            return;
        }

        if (last)
        {
            RememberTabs();
            RememberPlacement();
        }
        if (_closeConfirmed || Vm is not { SessionCount: > 0 } vm || !AppHost.Settings.ConfirmCloseSessions) return;
        e.Cancel = true;
        TrayService.IsQuitting = false;
        if (await Dialog.ConfirmAsync(this, Loc.T("Session.CloseWindowTitle"), Loc.F("Session.CloseWindowText", vm.SessionCount), Loc.T("Session.CloseAll"), danger: true, icon: "unplug"))
        {
            _closeConfirmed = true;
            Close();
        }
    }
}
