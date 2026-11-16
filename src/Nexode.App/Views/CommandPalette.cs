using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Avalonia.Media;
using Avalonia.Threading;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Model;
using Nexode.Core.Search;

namespace Nexode.App.Views;

/// <summary>One result of the command palette.</summary>
public sealed record PaletteItem(string Title, string? Subtitle, string Icon, string Group, int Score, Action Run, IBrush? Tint = null);

/// <summary>
/// Command palette (Ctrl+K): connections, profiles, actions and settings pages in one search. Switched-off features
/// do not appear. Up/Down select, Enter runs, Esc closes; the window closes as soon as it loses focus.
/// </summary>
public sealed class CommandPalette : Window
{
    private const int MaxResults = 40;
    private readonly MainWindowViewModel _vm;
    private readonly TextBox _search;
    private readonly StackPanel _list = new() { Spacing = 1 };
    private readonly ScrollViewer _scroller;
    private readonly TextBlock _empty;
    private List<PaletteItem> _items = [];
    private int _selected;
    private bool _closing;

    public CommandPalette(MainWindowViewModel vm)
    {
        _vm = vm;
        Title = Loc.T("Palette.Title");
        Width = 640;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        ShowInTaskbar = false;
        WindowDecorations = WindowDecorations.None;
        WindowStartupLocation = WindowStartupLocation.Manual;
        Background = Application.Current?.FindResource("B.BgPanel") as IBrush;

        _search = new TextBox { Classes = { "palette" }, PlaceholderText = Loc.T("Palette.Watermark"), InnerLeftContent = new LineIcon { Icon = "search", Size = 17, Margin = new Thickness(10, 0, 4, 0), [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.TextSecondary") } };
        _search.TextChanged += (_, _) => Update();
        _scroller = new ScrollViewer { Content = _list, MaxHeight = 420, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        _empty = new TextBlock { Text = Loc.T("Palette.Nothing"), Classes = { "muted" }, Margin = new Thickness(14, 10), IsVisible = false };
        var hint = new TextBlock { Text = Loc.T("Palette.Keys"), Classes = { "muted", "small" }, Margin = new Thickness(14, 6, 14, 8) };

        var root = new DockPanel();
        var top = new Border { Padding = new Thickness(10, 10, 10, 8), Child = _search };
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        root.Children.Add(top);
        root.Children.Add(hint);
        var middle = new StackPanel();
        middle.Children.Add(_empty);
        middle.Children.Add(_scroller);
        root.Children.Add(middle);
        Content = new Border { Classes = { "palettebox" }, Child = root };

        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        Deactivated += (_, _) => SafeClose();
        Opened += (_, _) =>
        {
            if (Owner is Window owner)
            {
                var scale = owner.RenderScaling;
                var x = owner.Position.X + (int)((owner.Bounds.Width - Width) / 2 * scale);
                var y = owner.Position.Y + (int)(90 * scale);
                Position = new PixelPoint(Math.Max(owner.Position.X, x), y);
            }

            Dispatcher.UIThread.Post(() => _search.Focus(), DispatcherPriority.Input);
        };
        Update();
    }

    public static void Show(MainWindow owner, MainWindowViewModel vm)
    {
        if (!Features.On(Features.Palette)) return;
        new CommandPalette(vm).Show(owner);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }

    private void OnKey(object? sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                SafeClose();
                break;
            case Key.Down:
                Select(_selected + 1);
                break;
            case Key.Up:
                Select(_selected - 1);
                break;
            case Key.PageDown:
                Select(_selected + 8);
                break;
            case Key.PageUp:
                Select(_selected - 8);
                break;
            case Key.Enter:
                Run(_selected);
                break;
            default:
                return;
        }

        e.Handled = true;
    }

    private void Run(int index)
    {
        if (index < 0 || index >= _items.Count) return;
        var item = _items[index];
        SafeClose();
        Dispatcher.UIThread.Post(item.Run, DispatcherPriority.Background);
    }

    private void Select(int index)
    {
        if (_items.Count == 0) return;
        _selected = Math.Clamp(index, 0, _items.Count - 1);
        for (var i = 0; i < _list.Children.Count; i++) _list.Children[i].Classes.Set("selected", i == _selected);
        if (_list.Children.ElementAtOrDefault(_selected) is { } row) row.BringIntoView();
    }

    private void Update()
    {
        _items = Build(_search.Text ?? "").Take(MaxResults).ToList();
        _list.Children.Clear();
        for (var i = 0; i < _items.Count; i++) _list.Children.Add(Row(_items[i], i));
        _empty.IsVisible = _items.Count == 0;
        _scroller.IsVisible = _items.Count > 0;
        _selected = 0;
        Select(0);
    }

    private Control Row(PaletteItem item, int index)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), Margin = new Thickness(12, 6, 12, 6) };
        var icon = new LineIcon { Icon = item.Icon, Size = 17, Margin = new Thickness(0, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, Tint = item.Tint,
            [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.TextSecondary") };
        var texts = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        texts.Children.Add(new TextBlock { Text = item.Title, TextTrimming = TextTrimming.CharacterEllipsis });
        if (!string.IsNullOrEmpty(item.Subtitle)) texts.Children.Add(new TextBlock { Text = item.Subtitle, Classes = { "muted", "small" }, TextTrimming = TextTrimming.CharacterEllipsis });
        // Same width for every group pill, so the column lines up.
        var group = new Border
        {
            Classes = { "pill" }, Padding = new Thickness(6, 0), Width = 124, VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock { Text = item.Group, FontSize = 10.5, HorizontalAlignment = HorizontalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis }
        };
        Grid.SetColumn(texts, 1);
        Grid.SetColumn(group, 2);
        grid.Children.Add(icon);
        grid.Children.Add(texts);
        grid.Children.Add(group);
        var row = new Border { Classes = { "paletterow" }, Child = grid };
        row.PointerPressed += (_, _) => Run(index);
        row.PointerEntered += (_, _) => Select(index);
        return row;
    }

    // ------------------------------------------------------------------ sources

    private IEnumerable<PaletteItem> Build(string text)
    {
        var query = text.Trim();
        var results = new List<PaletteItem>();
        var ws = AppHost.Workspace;
        var a = _vm.Actions;

        // Connections and other entries (the same scoring as the tree search).
        if (!ws.IsLocked)
        {
            var groupEntries = Loc.T("Palette.Entries");
            IEnumerable<(Entry Entry, int Score)> entries;
            if (query.Length == 0)
            {
                entries = ws.Tree.All.Where(e => !e.IsContainer && e.LastUsedUtc is not null).OrderByDescending(e => e.LastUsedUtc).Take(8).Select(e => (e, 50));
            }
            else
            {
                var q = SearchQuery.Parse(query);
                entries = ws.Tree.All.Where(e => e.Kind != EntryKind.Folder).Select(e => (e, q.Score(e, ws.Resolver))).Where(x => x.Item2 > 0);
            }

            foreach (var (e, score) in entries)
            {
                if (!Features.KindEnabled(e.Kind)) continue;
                var eff = ws.Resolver.Resolve(e);
                var sub = string.Join(" · ", new[] { Loc.Kind(e.Kind), eff.Host ?? eff.Url, string.Join(" / ", ws.Tree.Ancestors(e).Reverse().Select(x => x.Name)) }.Where(s => !string.IsNullOrEmpty(s)));
                var tint = eff.ColorHue is { } hue ? AppHost.Theme.FolderBrush(hue) : null;
                results.Add(new PaletteItem(e.Name, sub, eff.Icon, groupEntries, score + 100, () => a.Open(e), tint));
            }

            var groupProfiles = Loc.T("Rail.Profiles");
            foreach (var p in ws.Profiles)
            {
                var score = query.Length == 0 ? 40 : Fuzzy.Score(query.ToLowerInvariant(), p.Name);
                if (score > 0) results.Add(new PaletteItem(p.Name, Loc.T("Palette.LaunchProfile"), "layers", groupProfiles, score + 90, () => _ = a.OpenProfileAsync(p)));
            }
        }

        foreach (var c in Commands())
        {
            var score = query.Length == 0 ? 10 : Math.Max(Fuzzy.Score(query.ToLowerInvariant(), c.Title), c.Keywords.Select(k => Fuzzy.Score(query.ToLowerInvariant(), k)).DefaultIfEmpty(0).Max());
            if (score > 0) results.Add(c.Item with { Score = score });
        }

        return results.OrderByDescending(r => r.Score).ThenBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase);
    }

    private sealed record Command(PaletteItem Item, string Title, string[] Keywords);

    private IEnumerable<Command> Commands()
    {
        var vm = _vm;
        var groupAction = Loc.T("Palette.Actions");
        var groupSettings = Loc.T("Tab.Settings");
        Command Act(string key, string icon, Action run, params string[] keywords) =>
            new(new PaletteItem(Loc.T(key), null, icon, groupAction, 0, run), Loc.T(key), keywords);
        Command Page(string page, string key, string icon, params string[] keywordKeys) =>
            new(new PaletteItem(Loc.T(key), Loc.T("Palette.SettingsPage"), icon, groupSettings, 0, () => vm.OpenSettingsPage(page)), Loc.T(key),
                [page, .. keywordKeys.Select(Loc.T)]);

        yield return Act("Menu.NewTab", "plus", () => vm.Tabs.OpenStart(), "tab", "home", "start");
        yield return Act("Menu.NewWindow", "app-window", () => AppHost.Windows.Open(), "window");
        yield return Act("Tabs.Reopen", "undo-2", () => vm.Tabs.Reopen());
        foreach (var k in Features.CreatableKinds())
        {
            var kind = k;
            yield return new Command(new PaletteItem(Loc.F("Palette.NewKind", Loc.Kind(kind)), null, EntryKinds.Icon(kind), groupAction, 0,
                () => _ = vm.Actions.NewEntryAsync(kind, null)), Loc.F("Palette.NewKind", Loc.Kind(kind)), ["new", Loc.Kind(kind)]);
        }

        yield return Act("Menu.Import", "file-down", () => _ = vm.Actions.ImportAsync(null), "import");
        yield return Act("Import.Foreign", "file-down", () => _ = vm.Actions.ImportForeignAsync(), "putty", "winscp", "mremoteng", "rdcman", "mobaxterm", "remmina");
        if (Features.On(Features.Monitoring)) yield return Act("Tab.Dashboard", "layout-dashboard", () => vm.OpenDashboardCommand.Execute(null), "status", "monitoring");
        if (Features.On(Features.FtpServer)) yield return Act("FtpServer.Title", "server-cog", () => vm.Tabs.OpenOrFocus("ftpserver", () => new FtpServerTabViewModel()), "ftp", "server", "share", "lan");
        if (Features.On(Features.LanSync)) yield return Act("LanSync.Title", "arrow-left-right", () => vm.Tabs.OpenOrFocus("lansync", () => new LanSyncTabViewModel()), "sync", "lan", "computer", "devices");
        yield return Act("Tab.History", "history", () => vm.OpenHistoryCommand.Execute(null), "history", "log");
        yield return Act("Tab.Trash", "trash-2", () => vm.OpenTrashCommand.Execute(null), "trash", "deleted");
        if (vm.HasPassword) yield return Act(vm.IsLocked ? "Vault.Unlock" : "Vault.Lock", vm.IsLocked ? "lock-open" : "lock", () => vm.ToggleLockCommand.Execute(null), "lock", "vault");
        if (Features.On(Features.SshKeys)) yield return Act("Keys.Generate", "key-round", () => _ = SshKeyDialog.ShowAsync(vm), "ssh-keygen", "ed25519", "rsa", "key");

        yield return Page("appearance", "Settings.Appearance", "palette", "Settings.Theme", "Settings.Accent", "Settings.Language", "Theme.Dark", "Theme.Light");
        yield return Page("behavior", "Settings.Behavior", "mouse-pointer-click", "Settings.Terminal", "Settings.Sessions", "Settings.Shortcuts");
        yield return Page("startup", "Settings.Startup", "rocket", "Start.Autostart");
        yield return Page("home", "Settings.Home", "house");
        yield return Page("features", "Settings.Features", "toggle-right", "Feature.Minimal");
        if (Features.On(Features.Monitoring)) yield return Page("dashboard", "Tab.Dashboard", "layout-dashboard", "Status.Title");
        yield return Page("security", "Settings.Security", "shield", "Vault.MasterPassword", "Security.KnownHosts", "Keys.Title");
        yield return Page("data", "Settings.Data", "database", "Backup.Title", "Settings.ExportConnections", "Reset.Title");
        yield return Page("categories", "Settings.Categories", "tags");
        yield return Page("environments", "Settings.Environments", "flag");
        yield return Page("profiles", "Rail.Profiles", "layers");
        yield return Page("integration", "Settings.Integration", "link", "Integration.DataFolder", "Testing.Title");
        yield return Page("diagnostics", "Settings.Diagnostics", "stethoscope", "Diag.Log", "Diag.SshAgent", "Diag.Mica");
        yield return Page("about", "Tab.About", "info", "About.Licenses");
    }
}
