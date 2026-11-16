using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Controls.Primitives;
using Avalonia.Platform.Storage;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.VisualTree;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.ViewModels.Sessions;
using static Nexode.App.Views.MenuBuilder;

namespace Nexode.App.Views.Sessions;

/// <summary>
/// One side of the file browser: path bar, icon toolbar (new, rename, delete, cut/copy/paste, selection,
/// properties, hidden files, transfer), sortable column headers, filter and the file list.
/// </summary>
public sealed class FilePaneView : UserControl
{
    private static readonly DataFormat<string> LocalFormat = DataFormat.CreateStringApplicationFormat("nexode-local-files");
    private static readonly DataFormat<string> RemoteFormat = DataFormat.CreateStringApplicationFormat("nexode-remote-files");

    private readonly FileBrowserTabViewModel _vm;
    private readonly FilePane _pane;
    private readonly ListBox _list;
    private readonly Dictionary<FileSort, Button> _headers = [];
    private readonly List<Control> _needsSelection = [];
    private readonly List<Control> _needsConnection = [];
    private readonly Button _paste;
    private PointerPressedEventArgs? _press;
    private Point? _dragStart;

    public FilePaneView(FileBrowserTabViewModel vm, FilePane pane)
    {
        _vm = vm;
        _pane = pane;
        DataContext = pane;

        // ---------------------------------------------------------- path bar
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new LineIcon { Icon = pane.IsRemote ? "server" : "laptop", Size = 15, [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.AccentText") });
        title.Children.Add(new TextBlock { Text = Loc.T(pane.IsRemote ? "Files.Remote" : "Files.Local"), FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        var path = new TextBox { Classes = { "mono" }, FontSize = 12, MinHeight = 28, [!TextBox.TextProperty] = new Binding(nameof(FilePane.PathInput)) { Mode = BindingMode.TwoWay } };
        path.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            _ = _pane.LoadAsync(_pane.PathInput);
            e.Handled = true;
        };
        var pathRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(8, 8, 8, 4) };
        pathRow.Children.Add(title);
        Grid.SetColumn(path, 1);
        pathRow.Children.Add(path);
        if (pane.IsRemote) _needsConnection.Add(path);

        // ---------------------------------------------------------- toolbar
        var tools = new WrapPanel { Orientation = Orientation.Horizontal, ItemSpacing = 1, VerticalAlignment = VerticalAlignment.Center };
        Button Tool(string icon, string tip, Action click, string? gesture = null, bool selection = false)
        {
            var b = new Button { Classes = { "icon", "small" }, Content = new LineIcon { Icon = icon, Size = 16 } };
            ToolTip.SetTip(b, gesture is null ? Loc.T(tip) : $"{Loc.T(tip)} ({gesture})");
            b.Click += (_, _) => click();
            tools.Children.Add(b);
            if (selection) _needsSelection.Add(b);
            if (pane.IsRemote) _needsConnection.Add(b);
            return b;
        }

        void Divider() => tools.Children.Add(new Border { Classes = { "vdivider" }, Margin = new Thickness(2, 5), Height = 16 });

        Tool("arrow-up", "Files.Up", () => _ = _pane.UpAsync(), "Backspace");
        Tool("refresh-cw", "Files.Refresh", () => _ = _pane.RefreshAsync(), "F5");
        if (!pane.IsRemote) Tool("house", "Files.Home", () => _ = _pane.LoadAsync(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)));
        Divider();
        Tool("folder-plus", "Files.NewFolder", () => _ = NewFolderAsync(), "F7");
        Tool("file-plus", "Files.NewFile", () => _ = NewFileAsync());
        Divider();
        Tool("pencil", "Menu.Rename", () => _ = RenameSelectedAsync(), "F2", selection: true);
        Tool("trash-2", "Menu.Delete", () => _ = DeleteAsync(), "Del", selection: true);
        Divider();
        Tool("scissors", "Menu.Cut", () => ClipRows(true), "Ctrl+X", selection: true);
        Tool("copy", "Menu.Copy", () => ClipRows(false), "Ctrl+C", selection: true);
        _paste = Tool("clipboard-paste", "Menu.Paste", () => _ = _vm.PasteAsync(_pane), "Ctrl+V");
        Divider();
        Tool("square-check", "Files.SelectAll", SelectAll, "Ctrl+A");
        Tool("square-dashed", "Files.SelectNone", () => _list!.SelectedItems?.Clear());
        Tool("info", "Files.Properties", () => _ = PropertiesAsync(), "Alt+Enter", selection: true);
        var hidden = new ToggleButton { Classes = { "icon", "small" }, Content = new LineIcon { Icon = "eye", Size = 16 }, [!ToggleButton.IsCheckedProperty] = new Binding(nameof(FilePane.ShowHidden)) { Mode = BindingMode.TwoWay } };
        ToolTip.SetTip(hidden, Loc.T("Files.ShowHidden"));
        tools.Children.Add(hidden);
        Divider();
        var transfer = Tool(pane.IsRemote ? "download" : "upload", pane.IsRemote ? "Files.Download" : "Files.Upload", Transfer, selection: true);
        transfer.Classes.Add("accent");
        if (!pane.IsRemote) _needsConnection.Add(transfer);

        var filter = new TextBox { Width = 130, MinHeight = 28, FontSize = 12, Margin = new Thickness(6, 0, 0, 0), PlaceholderText = Loc.T("Files.Filter"), [!TextBox.TextProperty] = new Binding(nameof(FilePane.Filter)) { Mode = BindingMode.TwoWay } };
        filter.InnerLeftContent = new LineIcon { Icon = "list-filter", Size = 13, Margin = new Thickness(8, 0, 2, 0), [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.TextSecondary") };
        var toolRow = new Grid { Margin = new Thickness(6, 0, 8, 4) };
        toolRow.Children.Add(tools);
        Grid.SetColumn(filter, 2);
        pathRow.Children.Add(filter);
        if (pane.IsRemote) _needsConnection.Add(filter);

        // ---------------------------------------------------------- column headers
        var header = new Grid { ColumnDefinitions = Columns(pane), Margin = new Thickness(8, 2, 8, 2) };
        void Head(FileSort sort, string key, int column, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var b = new Button { Classes = { "colhead" }, HorizontalContentAlignment = align };
            b.Click += (_, _) => _pane.SortBy(sort);
            Grid.SetColumn(b, column);
            header.Children.Add(b);
            _headers[sort] = b;
            b.Tag = Loc.T(key);
        }

        Head(FileSort.Name, "Files.ColName", 1);
        Head(FileSort.Size, "Files.ColSize", 2, HorizontalAlignment.Right);
        Head(FileSort.Type, "Files.ColType", 3);
        Head(FileSort.Modified, "Files.ColModified", 4);
        Head(FileSort.Permissions, "Files.ColPermissions", 5);
        UpdateHeaders();

        var error = new TextBlock { Classes = { "danger", "small" }, Margin = new Thickness(12, 0, 12, 4), TextWrapping = TextWrapping.Wrap };
        error.Bind(TextBlock.TextProperty, new Binding(nameof(FilePane.Error)));
        error.Bind(IsVisibleProperty, new Binding(nameof(FilePane.HasError)));

        // ---------------------------------------------------------- list
        _list = new ListBox { Classes = { "files" }, SelectionMode = SelectionMode.Multiple, ItemTemplate = RowTemplate(pane) };
        _list.Bind(ItemsControl.ItemsSourceProperty, new Binding(nameof(FilePane.Items)));
        DragDrop.SetAllowDrop(_list, true);
        _list.SelectionChanged += (_, _) => UpdateEnabled();
        _list.DoubleTapped += async (_, e) =>
        {
            if ((e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext is FileRow row) await _vm.OpenAsync(_pane, row);
        };
        _list.KeyDown += ListKeyDown;
        _list.ContextRequested += ListContextRequested;
        _list.AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (!e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
            _dragStart = e.GetPosition(_list);
            _press = e;
        }, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerMovedEvent, ListPointerMoved, RoutingStrategies.Tunnel);
        _list.AddHandler(PointerReleasedEvent, (_, _) => _dragStart = null, RoutingStrategies.Tunnel);
        _list.AddHandler(DragDrop.DragOverEvent, ListDragOver);
        _list.AddHandler(DragDrop.DropEvent, ListDrop);

        var busy = new ProgressBar { IsIndeterminate = true, VerticalAlignment = VerticalAlignment.Top, Height = 2, MinHeight = 2 };
        busy.Bind(IsVisibleProperty, new Binding(nameof(FilePane.Busy)));
        var listPanel = new Panel();
        listPanel.Children.Add(_list);
        listPanel.Children.Add(busy);

        var summary = new TextBlock { Classes = { "muted", "small" }, Margin = new Thickness(14, 2, 12, 6), TextTrimming = TextTrimming.CharacterEllipsis };
        summary.Bind(TextBlock.TextProperty, new Binding(nameof(FilePane.Summary)));

        var dock = new DockPanel();
        foreach (var (c, d) in new (Control, Dock)[] { (pathRow, Dock.Top), (toolRow, Dock.Top), (header, Dock.Top), (error, Dock.Top), (summary, Dock.Bottom) })
        {
            DockPanel.SetDock(c, d);
            dock.Children.Add(c);
        }

        dock.Children.Add(listPanel);
        Content = new Border { Classes = { "card" }, Padding = new Thickness(0), Child = dock };

        _pane.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FilePane.Sort) or nameof(FilePane.Descending)) UpdateHeaders();
        };
        _vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(FileBrowserTabViewModel.IsConnected) or nameof(FileBrowserTabViewModel.CanPaste)) UpdateEnabled();
        };
        UpdateEnabled();
    }

    public ListBox List => _list;

    private static ColumnDefinitions Columns(FilePane pane) => new("26,*,80,64,128," + (pane.IsRemote || !OperatingSystem.IsWindows() ? "86" : "0"));

    private static FuncDataTemplate<FileRow> RowTemplate(FilePane pane) => new((row, _) =>
    {
        var g = new Grid { ColumnDefinitions = Columns(pane), Height = 26, Background = Brushes.Transparent };
        if (row is null) return g;
        var icon = new LineIcon { Icon = row.Icon, Size = 15, Margin = new Thickness(4, 0, 6, 0), [!LineIcon.ForegroundProperty] = new DynamicResourceExtension(row.IsDirectory ? "B.AccentText" : "B.TextSecondary") };
        var name = new TextBlock { Text = row.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis, Opacity = row.IsHidden || row.Name.StartsWith('.') && !row.IsUp ? 0.65 : 1 };
        var size = new TextBlock { Text = row.SizeText, Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 10, 0) };
        var type = new TextBlock { Text = row.TypeText, Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var mod = new TextBlock { Text = row.ModifiedText, Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var perm = new TextBlock { Text = row.PermissionsText, Classes = { "small", "muted", "mono" }, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
        var col = 0;
        foreach (var c in new Control[] { icon, name, size, type, mod, perm })
        {
            Grid.SetColumn(c, col++);
            g.Children.Add(c);
        }

        if (row.IsLink) ToolTip.SetTip(name, Loc.T("Files.Link"));
        return g;
    }, supportsRecycling: false);

    private void UpdateHeaders()
    {
        foreach (var (sort, b) in _headers)
        {
            var active = _pane.Sort == sort;
            b.Classes.Set("sorted", active);
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 3 };
            label.Children.Add(new TextBlock { Text = (string)b.Tag!, VerticalAlignment = VerticalAlignment.Center });
            if (active) label.Children.Add(new LineIcon { Icon = _pane.Descending ? "chevron-down" : "chevron-up", Size = 11, VerticalAlignment = VerticalAlignment.Center });
            b.Content = label;
        }
    }

    private List<FileRow> Selected => _list.SelectedItems?.OfType<FileRow>().Where(r => !r.IsUp).ToList() ?? [];

    private void UpdateEnabled()
    {
        var connected = _vm.IsConnected;
        foreach (var c in _needsConnection) c.IsEnabled = connected;
        var any = Selected.Count > 0;
        foreach (var c in _needsSelection) c.IsEnabled = any && (!_pane.IsRemote || connected) && (!_needsConnection.Contains(c) || connected);
        _paste.IsEnabled = _vm.CanPaste && connected;
    }

    private void SelectAll()
    {
        _list.SelectedItems?.Clear();
        foreach (var r in _pane.Items.Where(r => !r.IsUp)) _list.SelectedItems?.Add(r);
    }

    private void ClipRows(bool cut)
    {
        var rows = Selected;
        if (rows.Count == 0) return;
        _vm.SetClipboard(_pane, rows, cut);
        _vm.Owner?.ShowToast(Loc.F(cut ? "Files.CutDone" : "Files.CopyDone", rows.Count));
    }

    private void Transfer()
    {
        var rows = Selected;
        if (rows.Count == 0 || !_vm.IsConnected) return;
        if (_pane.IsRemote) _ = _vm.DownloadAsync(rows);
        else _ = _vm.UploadAsync(rows.Select(r => r.FullPath));
    }

    private Window? Owner => TopLevel.GetTopLevel(this) as Window;

    private async Task NewFolderAsync()
    {
        if (Owner is not { } w) return;
        var name = await Dialog.InputAsync(w, Loc.T("Files.NewFolder"), Loc.T("Field.Name"), Loc.T("New.FolderDefault"), "folder-plus");
        if (name is not null) await _vm.NewFolderAsync(_pane, name);
    }

    private async Task NewFileAsync()
    {
        if (Owner is not { } w) return;
        var name = await Dialog.InputAsync(w, Loc.T("Files.NewFile"), Loc.T("Field.Name"), Loc.T("Files.NewFileDefault"), "file-plus");
        if (name is not null) await _vm.NewFileAsync(_pane, name);
    }

    private async Task RenameSelectedAsync()
    {
        if (Selected is [var row]) await RenameAsync(row);
    }

    private async Task RenameAsync(FileRow row)
    {
        if (Owner is not { } w) return;
        var name = await Dialog.InputAsync(w, Loc.T("Menu.Rename"), Loc.T("Field.Name"), row.Name);
        if (name is not null && name != row.Name) await _vm.RenameAsync(_pane, row, name);
    }

    private async Task DeleteAsync()
    {
        if (Owner is not { } w) return;
        var rows = Selected;
        if (rows.Count == 0) return;
        var message = rows.Count == 1 ? Loc.F("Files.DeleteOne", rows[0].Name) : Loc.F("Files.DeleteMany", rows.Count);
        if (!await Dialog.ConfirmAsync(w, Loc.T("Menu.Delete"), message, Loc.T("Menu.Delete"), danger: true)) return;
        await _vm.DeleteAsync(_pane, rows);
    }

    private async Task PropertiesAsync()
    {
        if (Owner is not { } w || Selected is not { Count: > 0 } rows) return;
        var mode = await FilePropertiesDialog.ShowAsync(w, rows, _vm.CanChangePermissions(_pane));
        if (mode is { } m) await _vm.SetPermissionsAsync(_pane, rows, m);
    }

    private async void ListKeyDown(object? sender, KeyEventArgs e)
    {
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control) || e.KeyModifiers.HasFlag(KeyModifiers.Meta);
        if (_pane.IsRemote && !_vm.IsConnected) return;
        switch (e.Key)
        {
            case Key.Enter when e.KeyModifiers.HasFlag(KeyModifiers.Alt):
                await PropertiesAsync();
                break;
            case Key.Enter when _list.SelectedItem is FileRow row:
                await _vm.OpenAsync(_pane, row);
                break;
            case Key.Back:
                await _pane.UpAsync();
                break;
            case Key.F5:
                await _pane.RefreshAsync();
                break;
            case Key.F7:
                await NewFolderAsync();
                break;
            case Key.Delete when Selected.Count > 0:
                await DeleteAsync();
                break;
            case Key.F2 when Selected is [var row]:
                await RenameAsync(row);
                break;
            case Key.A when ctrl:
                SelectAll();
                break;
            case Key.C when ctrl:
                ClipRows(false);
                break;
            case Key.X when ctrl:
                ClipRows(true);
                break;
            case Key.V when ctrl:
                await _vm.PasteAsync(_pane);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void ListContextRequested(object? sender, ContextRequestedEventArgs e)
    {
        var rows = Selected;
        var connected = _vm.IsConnected;
        var remoteOk = !_pane.IsRemote || connected;
        var items = new List<Control>();
        if (rows.Count > 0)
        {
            if (_pane.IsRemote) items.Add(Item("Files.Download", "download", () => _ = _vm.DownloadAsync(rows), enabled: connected, bold: true));
            else items.Add(Item("Files.Upload", "upload", () => _ = _vm.UploadAsync(rows.Select(r => r.FullPath)), enabled: connected, bold: true));
            if (rows is [var single])
            {
                items.Add(Item("Files.Open", "external-link", () => _ = _vm.OpenAsync(_pane, single), "Enter", enabled: remoteOk));
                if (_pane.IsRemote && !single.IsDirectory) items.Add(Item("Files.Edit", "file-pen", () => _ = _vm.EditAsync(single), enabled: connected));
                items.Add(Item("Menu.Rename", "pencil", () => _ = RenameAsync(single), "F2", enabled: remoteOk));
            }

            items.Add(new Separator());
            items.Add(Item("Menu.Cut", "scissors", () => ClipRows(true), "Ctrl+X", enabled: remoteOk));
            items.Add(Item("Menu.Copy", "copy", () => ClipRows(false), "Ctrl+C", enabled: remoteOk));
        }

        items.Add(Item("Menu.Paste", "clipboard-paste", () => _ = _vm.PasteAsync(_pane), "Ctrl+V", enabled: _vm.CanPaste && connected));
        if (rows.Count > 0)
        {
            items.Add(Item("Files.CopyPath", "clipboard-copy", async () =>
            {
                if (TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, string.Join(Environment.NewLine, rows.Select(r => r.FullPath)));
            }));
            items.Add(Item("Files.Properties", "info", () => _ = PropertiesAsync(), "Alt+Enter", enabled: remoteOk));
            items.Add(new Separator());
            items.Add(Item("Menu.Delete", "trash-2", () => _ = DeleteAsync(), "Delete", danger: true, enabled: remoteOk));
        }

        items.Add(new Separator());
        items.Add(Item("Files.NewFolder", "folder-plus", () => _ = NewFolderAsync(), "F7", enabled: remoteOk));
        items.Add(Item("Files.NewFile", "file-plus", () => _ = NewFileAsync(), enabled: remoteOk));
        items.Add(Item("Files.Refresh", "refresh-cw", () => _ = _pane.RefreshAsync(), "F5", enabled: remoteOk));
        if (!_pane.IsRemote) items.Add(Item("Files.ShowInExplorer", "folder-open", () => Platform.Shell.Open(_pane.Path)));
        var menu = new ContextMenu();
        foreach (var i in items) menu.Items.Add(i);
        menu.Open(_list);
        e.Handled = true;
    }

    // ------------------------------------------------------------------ drag & drop

    private async void ListPointerMoved(object? sender, PointerEventArgs e)
    {
        if (_dragStart is not { } start || !e.GetCurrentPoint(_list).Properties.IsLeftButtonPressed) return;
        var pos = e.GetPosition(_list);
        if (Math.Abs(pos.X - start.X) < 8 && Math.Abs(pos.Y - start.Y) < 8) return;
        _dragStart = null;
        var rows = Selected;
        if (rows.Count == 0) return;
        var item = new DataTransferItem();
        item.Set(_pane.IsRemote ? RemoteFormat : LocalFormat, string.Join('\n', rows.Select(r => r.FullPath)));
        var data = new DataTransfer();
        data.Add(item);
        if (_press is { } press) await DragDrop.DoDragDropAsync(press, data, DragDropEffects.Copy);
    }

    private void ListDragOver(object? sender, DragEventArgs e)
    {
        var ok = _pane.IsRemote ? e.DataTransfer.Contains(LocalFormat) || e.DataTransfer.Contains(DataFormat.File) : e.DataTransfer.Contains(RemoteFormat);
        e.DragEffects = ok && _vm.IsConnected ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void ListDrop(object? sender, DragEventArgs e)
    {
        if (!_vm.IsConnected) return;
        e.Handled = true;
        var targetRow = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>(includeSelf: true)?.DataContext as FileRow;
        var dir = targetRow is { IsDirectory: true, IsUp: false } ? targetRow.FullPath : null;
        if (_pane.IsRemote)
        {
            if (e.DataTransfer.TryGetValue(LocalFormat) is string local) await _vm.UploadAsync(local.Split('\n'), dir);
            else if (e.DataTransfer.TryGetFiles() is { } files) await _vm.UploadAsync(files.Select(f => f.TryGetLocalPath()).OfType<string>(), dir);
        }
        else if (e.DataTransfer.TryGetValue(RemoteFormat) is string remote)
        {
            var paths = remote.Split('\n').ToHashSet();
            var rows = _vm.Remote.Items.Where(r => paths.Contains(r.FullPath)).ToList();
            await _vm.DownloadAsync(rows, dir);
        }
    }
}
