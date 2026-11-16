using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Views.Sessions;

/// <summary>Split view: toolbar (layouts, add session) and up to four session panes with splitters.</summary>
public sealed class SplitSessionView : UserControl
{
    private readonly Grid _grid = new();
    private readonly Dictionary<SessionTabViewModel, Border> _panes = [];
    private SplitTabViewModel? _vm;

    public SplitSessionView()
    {
        var toolbar = new Border { Classes = { "sessionbar" } };
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        bar.Children.Add(new LineIcon { Icon = "layout-panel-left", Size = 16, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center,
            [!LineIcon.ForegroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("B.AccentText") });
        bar.Children.Add(new TextBlock { [!TextBlock.TextProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("S.Split.Title"), VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeight.SemiBold, Margin = new Thickness(0, 0, 14, 0) });
        foreach (var (layout, icon, tip) in new[] { ("Columns", "columns-2", "Split.Columns"), ("Rows", "rows-2", "Split.Rows"), ("Main", "layout-panel-left", "Split.Main"), ("Grid", "layout-grid", "Split.Grid") })
        {
            var b = new ToggleButton { Classes = { "icon" }, Content = new LineIcon { Icon = icon, Size = 17 }, Tag = layout };
            ToolTip.SetTip(b, Loc.T(tip));
            b.Click += (_, _) =>
            {
                if (_vm is not null) _vm.Layout = Enum.Parse<SplitLayout>(layout);
                UpdateToolbar();
            };
            bar.Children.Add(b);
        }

        var add = new Button { Classes = { "ghost", "small" }, Margin = new Thickness(12, 0, 0, 0), Content = new IconLabel { Icon = "plus", Text = Loc.T("Split.Add") } };
        add.Click += (_, _) => ShowAddMenu(add);
        bar.Children.Add(add);

        // Send input to all terminal panes (optional feature); red while active.
        _broadcastButton = new ToggleButton { Classes = { "ghost", "small", "broadcast" }, Margin = new Thickness(8, 0, 0, 0),
            Content = new IconLabel { Icon = "radio-tower", Text = Loc.T("Split.Broadcast") } };
        ToolTip.SetTip(_broadcastButton, Loc.T("Split.BroadcastTip"));
        _broadcastButton.IsCheckedChanged += (_, _) =>
        {
            if (_vm is not null && _vm.Broadcast != (_broadcastButton.IsChecked == true)) _vm.Broadcast = _broadcastButton.IsChecked == true;
        };
        bar.Children.Add(_broadcastButton);
        _broadcastInfo = new Border { Classes = { "pill", "danger" }, Margin = new Thickness(8, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, IsVisible = false };
        bar.Children.Add(_broadcastInfo);
        toolbar.Child = bar;
        _layoutButtons = bar.Children.OfType<ToggleButton>().ToList();
        _add = add;

        var dock = new DockPanel();
        DockPanel.SetDock(toolbar, Dock.Top);
        dock.Children.Add(toolbar);
        dock.Children.Add(_grid);
        Content = dock;

        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (_vm is null || e.KeyModifiers != (KeyModifiers.Control | KeyModifiers.Alt)) return;
            if (e.Key is Key.Right or Key.Down) _vm.Cycle(1);
            else if (e.Key is Key.Left or Key.Up) _vm.Cycle(-1);
            else return;
            FocusActive();
            e.Handled = true;
        }, RoutingStrategies.Tunnel);
    }

    private readonly List<ToggleButton> _layoutButtons;
    private readonly ToggleButton _broadcastButton;
    private readonly Border _broadcastInfo;
    private readonly Button _add;

    protected override void OnDataContextChanged(EventArgs e)
    {
        base.OnDataContextChanged(e);
        if (_vm is not null)
        {
            _vm.Changed -= Rebuild;
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = DataContext as SplitTabViewModel;
        if (_vm is not null)
        {
            _vm.Changed += Rebuild;
            _vm.PropertyChanged += OnVmChanged;
        }

        Rebuild();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        // Release the session views (RDP windows park until they are shown again).
        _grid.Children.Clear();
        _panes.Clear();
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (_grid.Children.Count == 0) Rebuild();
    }

    private void OnVmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(SplitTabViewModel.Active)) MarkActive();
        else if (e.PropertyName is nameof(SplitTabViewModel.Broadcast) or nameof(SplitTabViewModel.CanBroadcast)) UpdateBroadcast();
    }

    private void Rebuild()
    {
        _grid.Children.Clear();
        _grid.ColumnDefinitions.Clear();
        _grid.RowDefinitions.Clear();
        _panes.Clear();
        UpdateToolbar();
        UpdateBroadcast();
        if (_vm is null || _vm.Panes.Count == 0 || VisualRoot is null) return;

        var panes = _vm.Panes.ToList();
        var n = panes.Count;
        var layout = _vm.Layout;
        if (layout == SplitLayout.Grid && n < 4) layout = n == 3 ? SplitLayout.Main : SplitLayout.Columns;
        if (layout == SplitLayout.Main && n < 3) layout = SplitLayout.Columns;

        // Cells: (row, column, rowSpan) in a grid with splitter tracks between the cells.
        var cells = new List<(int R, int C, int RS)>();
        int cols, rows;
        switch (layout)
        {
            case SplitLayout.Rows:
                cols = 1;
                rows = n;
                for (var i = 0; i < n; i++) cells.Add((i, 0, 1));
                break;
            case SplitLayout.Main:
                cols = 2;
                rows = n - 1;
                cells.Add((0, 0, rows));
                for (var i = 1; i < n; i++) cells.Add((i - 1, 1, 1));
                break;
            case SplitLayout.Grid:
                cols = 2;
                rows = 2;
                cells.AddRange([(0, 0, 1), (0, 1, 1), (1, 0, 1), (1, 1, 1)]);
                break;
            default:
                cols = n;
                rows = 1;
                for (var i = 0; i < n; i++) cells.Add((0, i, 1));
                break;
        }

        for (var c = 0; c < cols; c++)
        {
            if (c > 0) _grid.ColumnDefinitions.Add(new ColumnDefinition(5, GridUnitType.Pixel));
            _grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star) { MinWidth = 160 });
        }

        for (var r = 0; r < rows; r++)
        {
            if (r > 0) _grid.RowDefinitions.Add(new RowDefinition(5, GridUnitType.Pixel));
            _grid.RowDefinitions.Add(new RowDefinition(1, GridUnitType.Star) { MinHeight = 120 });
        }

        for (var c = 1; c < cols; c++)
        {
            var split = new GridSplitter { Classes = { "panel" }, ResizeDirection = GridResizeDirection.Columns, [!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("B.Border") };
            Grid.SetColumn(split, c * 2 - 1);
            Grid.SetRowSpan(split, Math.Max(1, rows * 2 - 1));
            _grid.Children.Add(split);
        }

        if (rows > 1)
        {
            // Horizontal splitters only span the stacked column (Main) or all columns.
            for (var r = 1; r < rows; r++)
            {
                var split = new GridSplitter { Classes = { "panel" }, ResizeDirection = GridResizeDirection.Rows, [!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("B.Border") };
                Grid.SetRow(split, r * 2 - 1);
                if (layout == SplitLayout.Main)
                {
                    Grid.SetColumn(split, 2);
                }
                else
                {
                    Grid.SetColumnSpan(split, Math.Max(1, cols * 2 - 1));
                }

                _grid.Children.Add(split);
            }
        }

        for (var i = 0; i < n; i++)
        {
            var (r, c, rs) = cells[i];
            var pane = BuildPane(panes[i]);
            Grid.SetRow(pane, r * 2);
            Grid.SetColumn(pane, c * 2);
            Grid.SetRowSpan(pane, rs * 2 - 1);
            _grid.Children.Add(pane);
            _panes[panes[i]] = pane;
        }

        MarkActive();
        UpdateBroadcast();
    }

    private Border BuildPane(SessionTabViewModel pane)
    {
        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto"), Margin = new Thickness(8, 3, 4, 3) };
        var icon = new LineIcon { Size = 14, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, [!LineIcon.IconProperty] = new Avalonia.Data.Binding(nameof(SessionTabViewModel.Icon)) { Source = pane } };
        var title = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, FontSize = 12, [!TextBlock.TextProperty] = new Avalonia.Data.Binding(nameof(SessionTabViewModel.Title)) { Source = pane } };
        var pop = new Button { Classes = { "icon", "small" }, Content = new LineIcon { Icon = "external-link", Size = 14 } };
        ToolTip.SetTip(pop, Loc.T("Split.PopOut"));
        pop.Click += (_, _) => _vm?.PopOutCommand.Execute(pane);
        var close = new Button { Classes = { "icon", "small", "close" }, Content = new LineIcon { Icon = "x", Size = 15 } };
        ToolTip.SetTip(close, Loc.T("Split.ClosePane"));
        close.Click += (_, _) => _vm?.ClosePaneCommand.Execute(pane);
        Grid.SetColumn(title, 1);
        Grid.SetColumn(pop, 2);
        Grid.SetColumn(close, 3);
        header.Children.Add(icon);
        header.Children.Add(title);
        header.Children.Add(pop);
        header.Children.Add(close);

        var headerBorder = new Border { Classes = { "paneheader" }, Child = header };
        var content = new ContentControl { Content = pane };
        var dock = new DockPanel();
        DockPanel.SetDock(headerBorder, Dock.Top);
        dock.Children.Add(headerBorder);
        dock.Children.Add(content);
        var border = new Border { Classes = { "pane" }, Child = dock };
        border.AddHandler(PointerPressedEvent, (_, _) =>
        {
            if (_vm is not null) _vm.Active = pane;
        }, RoutingStrategies.Tunnel);
        border.AddHandler(GotFocusEvent, (_, _) =>
        {
            if (_vm is not null) _vm.Active = pane;
        }, RoutingStrategies.Bubble);
        return border;
    }

    private void UpdateBroadcast()
    {
        var on = _vm?.Broadcast == true;
        _broadcastButton.IsVisible = _vm?.CanBroadcast == true;
        _broadcastButton.IsChecked = on;
        _broadcastInfo.IsVisible = on;
        if (on) _broadcastInfo.Child = new IconLabel { Icon = "triangle-alert", Text = Loc.F("Split.BroadcastActive", _vm!.TerminalCount) };
        foreach (var (pane, border) in _panes) border.Classes.Set("broadcast", on && pane is TerminalTabViewModel);
    }

    private void MarkActive()
    {
        foreach (var (p, b) in _panes) b.Classes.Set("active", _vm?.Active == p && _panes.Count > 1);
    }

    private void FocusActive()
    {
        if (_vm?.Active is { } a && _panes.TryGetValue(a, out var b))
        {
            var target = b.GetVisualDescendants().OfType<InputElement>().FirstOrDefault(c => c.Focusable && c is not Button);
            target?.Focus();
        }
    }

    private void UpdateToolbar()
    {
        foreach (var b in _layoutButtons) b.IsChecked = _vm is not null && b.Tag as string == _vm.Layout.ToString();
        _add.IsEnabled = _vm?.CanAdd == true;
    }

    private void ShowAddMenu(Control anchor)
    {
        if (_vm?.Owner is not { } owner) return;
        var menu = new ContextMenu();
        var candidates = owner.Tabs.Items.OfType<SessionTabViewModel>().ToList();
        foreach (var s in candidates)
        {
            var item = new MenuItem { Header = s.Title, Icon = new LineIcon { Icon = s.Icon } };
            item.Click += (_, _) =>
            {
                owner.Tabs.Detach(s);
                _vm.Add(s);
                owner.Tabs.Selected = _vm;
            };
            menu.Items.Add(item);
        }

        if (candidates.Count == 0) menu.Items.Add(new MenuItem { Header = Loc.T("Split.NoSessions"), IsEnabled = false });
        menu.Open(anchor);
    }
}

file static class VisualExtensions
{
    public static IEnumerable<Visual> GetVisualDescendants(this Visual v) => Avalonia.VisualTree.VisualExtensions.GetVisualDescendants(v);
}
