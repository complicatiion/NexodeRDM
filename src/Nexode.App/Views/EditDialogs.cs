using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Markup.Xaml.MarkupExtensions;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Model;
using Nexode.Core.Theming;

namespace Nexode.App.Views;

/// <summary>Name + icon editor for categories.</summary>
public static class CategoryEditDialog
{
    public static async Task<(string Name, string Icon)?> ShowAsync(Window owner, Category? category)
    {
        var icon = category?.Icon ?? "tag";
        var name = new TextBox { Text = category is null ? "" : CategoryNames.Of(category), PlaceholderText = Loc.T("Field.Name") };
        var preview = new LineIcon { Icon = icon, Size = 18 };
        var pick = new Button { Content = preview, Width = 40, Height = 34, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(pick, Loc.T("Menu.Icon"));
        pick.Click += async (_, _) =>
        {
            var chosen = await new IconPickerWindow(icon).ShowDialog<string?>(owner);
            if (!string.IsNullOrEmpty(chosen))
            {
                icon = chosen;
                preview.Icon = chosen;
            }
        };

        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(name, 1);
        name.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(pick);
        row.Children.Add(name);
        var dlg = new DialogWindow(Loc.T(category is null ? "Category.Add" : "Category.Edit"), "tag") { Body = row };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Save"), "check", true, false, () => !string.IsNullOrWhiteSpace(name.Text), "ok");
        dlg.Opened += (_, _) =>
        {
            name.Focus();
            name.SelectAll();
        };
        return await dlg.ShowDialog<object?>(owner) is "ok" ? (name.Text!.Trim(), icon) : null;
    }
}

/// <summary>Name, icon and color of a data source.</summary>
public static class SourceEditDialog
{
    public static async Task<bool> ShowAsync(Window owner, DataSourceInfo info)
    {
        var icon = string.IsNullOrEmpty(info.Icon) ? "database" : info.Icon;
        var hue = info.ColorHue;
        var name = new TextBox { Text = info.Name, PlaceholderText = Loc.T("Field.Name") };
        var preview = new LineIcon { Icon = icon, Size = 18, Tint = hue is { } h0 ? AppHost.Theme.FolderBrush(h0) : null };
        var pick = new Button { Content = preview, Width = 40, Height = 34, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        pick.Click += async (_, _) =>
        {
            var chosen = await new IconPickerWindow(icon).ShowDialog<string?>(owner);
            if (!string.IsNullOrEmpty(chosen))
            {
                icon = chosen;
                preview.Icon = chosen;
            }
        };

        var swatches = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, Margin = new Thickness(0, 12, 0, 0) };
        var buttons = new List<(Button B, double? H)>();
        void Mark()
        {
            foreach (var (b, h) in buttons) b.Classes.Set("selected", h == hue);
            preview.Tint = hue is { } x ? AppHost.Theme.FolderBrush(x) : null;
        }

        foreach (var h in new double?[] { null }.Concat(FolderColors.Swatches.Select(x => (double?)x)))
        {
            var b = new Button { Classes = { "swatch" }, Background = h is { } v ? AppHost.Theme.FolderBrush(v) : AppHost.Theme.Brush("SurfaceRaised") };
            if (h is null) b.Content = new LineIcon { Icon = "x", Size = 12 };
            b.Click += (_, _) =>
            {
                hue = h;
                Mark();
            };
            buttons.Add((b, h));
            swatches.Children.Add(b);
        }

        Mark();
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(name, 1);
        name.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(pick);
        row.Children.Add(name);
        var shared = new ToggleSwitch { IsChecked = info.Shared, OnContent = "", OffContent = "", HorizontalAlignment = HorizontalAlignment.Right };
        var sharedRow = new SettingRow { Label = Loc.T("Shared.New"), Icon = "users-round", Info = Loc.T("Shared.NewTip"), Content = shared, Margin = new Thickness(0, 10, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(row);
        panel.Children.Add(swatches);
        panel.Children.Add(sharedRow);
        var dlg = new DialogWindow(Loc.T("Source.Edit"), "database") { Body = panel };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Save"), "check", true, false, () => !string.IsNullOrWhiteSpace(name.Text), "ok");
        if (await dlg.ShowDialog<object?>(owner) is not "ok") return false;
        info.Name = name.Text!.Trim();
        info.Icon = icon;
        info.ColorHue = hue;
        info.Shared = shared.IsChecked == true;
        return true;
    }
}

/// <summary>"Open with parameters": user, port, resolution and admin session for one connection, embedded or external.</summary>
public static class OpenParametersDialog
{
    public static async Task<(Dictionary<string, string> Overrides, bool External)?> ShowAsync(Window owner, Entry entry)
    {
        var eff = AppHost.Workspace.Resolver.Resolve(entry);
        var user = new TextBox { Text = eff.Username ?? "", PlaceholderText = Loc.T("Field.Username") };
        var password = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("Params.PasswordKeep") };
        var port = new NumericUpDown { Value = eff.Port ?? EntryKinds.DefaultPort(entry.Kind) ?? 0, Minimum = 1, Maximum = 65535, FormatString = "0", Increment = 1 };
        var resolution = new ComboBox
        {
            ItemsSource = new[] { Loc.T("Rdp.Fit"), "1280x720", "1366x768", "1600x900", "1920x1080", "2560x1440" },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var admin = new ToggleSwitch { IsChecked = entry.Setting(SettingKeys.RdpAdmin) == "1", OnContent = "", OffContent = "" };
        var external = new ToggleSwitch { IsChecked = entry.Kind == EntryKind.Rdp && !OperatingSystem.IsWindows(), OnContent = "", OffContent = "", IsEnabled = !(entry.Kind == EntryKind.Rdp && !OperatingSystem.IsWindows()) };

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("150,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto,Auto") };
        var row = 0;
        void Add(string label, Control control, bool visible = true)
        {
            if (!visible) return;
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 10) };
            control.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(l, row);
            Grid.SetRow(control, row);
            Grid.SetColumn(control, 1);
            grid.Children.Add(l);
            grid.Children.Add(control);
            row++;
        }

        var isRdp = entry.Kind == EntryKind.Rdp;
        Add(Loc.T("Field.Username"), user);
        Add(Loc.T("Password"), password);
        Add(Loc.T("Field.Port"), port);
        Add(Loc.T("Rdp.Resolution"), resolution, isRdp);
        Add(Loc.T("Rdp.Admin"), admin, isRdp);
        Add(Loc.T("Params.External"), external);

        var dlg = new DialogWindow(Loc.F("Params.Title", entry.Name), "sliders-horizontal", 500) { Body = grid };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Session.Connect"), "plug-zap", true, false, null, "ok");
        if (await dlg.ShowDialog<object?>(owner) is not "ok") return null;

        var o = new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(user.Text) && user.Text != eff.Username) o["session.user"] = user.Text.Trim();
        if (!string.IsNullOrEmpty(password.Text)) o["session.password"] = password.Text;
        if (port.Value is { } pv) o["session.port"] = ((int)pv).ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (isRdp)
        {
            o[SettingKeys.RdpResolution] = resolution.SelectedIndex <= 0 ? "fit" : (string)resolution.SelectedItem!;
            o[SettingKeys.RdpAdmin] = admin.IsChecked == true ? "1" : "0";
        }

        return (o, external.IsChecked == true);
    }
}

/// <summary>Name, short badge text and color of an environment.</summary>
public static class EnvironmentEditDialog
{
    public static readonly string[] Colors =
        ["#E5484D", "#F59E0B", "#EAB308", "#22A06B", "#14B8A6", "#3B82F6", "#6366F1", "#A855F7", "#EC4899", "#8B5E3C", "#64748B", "#71717A"];

    public static async Task<(string Name, string Short, string Color)?> ShowAsync(Window owner, EnvironmentDef? env)
    {
        var color = env?.Color ?? "#3B82F6";
        var name = new TextBox { Text = env is null ? "" : Envs.Name(env), PlaceholderText = Loc.T("Field.Name") };
        var shortText = new TextBox { Text = env?.Short ?? "", PlaceholderText = Loc.T("Env.ShortWatermark"), MaxLength = 8 };
        var preview = new Border { Classes = { "pill" }, Padding = new Thickness(7, 1), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        var previewText = new TextBlock { FontSize = 10.5, FontWeight = FontWeight.SemiBold };
        preview.Child = previewText;
        var swatches = new WrapPanel { ItemSpacing = 8, LineSpacing = 8 };
        var buttons = new List<(Button B, string C)>();

        void Update()
        {
            var c = Color.TryParse(color, out var parsed) ? parsed : Avalonia.Media.Colors.Gray;
            preview.Background = new SolidColorBrush(c, 0.18);
            previewText.Foreground = new SolidColorBrush(c);
            previewText.Text = string.IsNullOrWhiteSpace(shortText.Text) ? (name.Text ?? "").ToUpperInvariant() : shortText.Text.ToUpperInvariant();
            foreach (var (b, hex) in buttons) b.Classes.Set("selected", string.Equals(hex, color, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var hex in Colors)
        {
            var b = new Button { Classes = { "swatch" }, Background = new SolidColorBrush(Color.Parse(hex)) };
            b.Click += (_, _) =>
            {
                color = hex;
                Update();
            };
            buttons.Add((b, hex));
            swatches.Children.Add(b);
        }

        name.TextChanged += (_, _) => Update();
        shortText.TextChanged += (_, _) => Update();
        Update();

        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto") };
        void Row(int r, string label, Control c)
        {
            var l = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 10) };
            c.Margin = new Thickness(0, 0, 0, 10);
            Grid.SetRow(l, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            grid.Children.Add(l);
            grid.Children.Add(c);
        }

        Row(0, Loc.T("Field.Name"), name);
        Row(1, Loc.T("Env.Short"), shortText);
        Row(2, Loc.T("Env.Color"), swatches);
        Row(3, Loc.T("Env.Preview"), preview);

        var dlg = new DialogWindow(Loc.T(env is null ? "Env.Add" : "Env.Edit"), "flag", 480) { Body = grid };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Save"), "check", true, false, () => !string.IsNullOrWhiteSpace(name.Text), "ok");
        dlg.Opened += (_, _) => name.Focus();
        return await dlg.ShowDialog<object?>(owner) is "ok" ? (name.Text!.Trim(), (shortText.Text ?? "").Trim().ToUpperInvariant(), color) : null;
    }
}

/// <summary>Connection profile: name, icon, color, how it opens and its connections (ordered).</summary>
public static class ProfileEditDialog
{
    public static async Task<ConnectionProfile?> ShowAsync(Window owner, ConnectionProfile? profile, IReadOnlyList<Guid>? preselected = null)
    {
        var ws = AppHost.Workspace;
        var p = profile is null
            ? new ConnectionProfile { Id = "p-" + Guid.NewGuid().ToString("N")[..10], Entries = [.. preselected ?? []] }
            : new ConnectionProfile
            {
                Id = profile.Id, Name = profile.Name, Icon = profile.Icon, ColorHue = profile.ColorHue, Entries = [.. profile.Entries], Layout = profile.Layout,
                Sequential = profile.Sequential, DelayMs = profile.DelayMs, Order = profile.Order
            };

        var name = new TextBox { Text = p.Name, PlaceholderText = Loc.T("Profile.NameWatermark") };
        var preview = new LineIcon { Icon = p.Icon, Size = 18, Tint = p.ColorHue is { } h0 ? AppHost.Theme.FolderBrush(h0) : null };
        var pick = new Button { Content = preview, Width = 40, Height = 34, Padding = new Thickness(0), HorizontalContentAlignment = HorizontalAlignment.Center };
        ToolTip.SetTip(pick, Loc.T("Menu.Icon"));
        pick.Click += async (_, _) =>
        {
            var chosen = await new IconPickerWindow(p.Icon).ShowDialog<string?>(owner);
            if (!string.IsNullOrEmpty(chosen))
            {
                p.Icon = chosen;
                preview.Icon = chosen;
            }
        };

        var swatches = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        var swatchButtons = new List<(Button B, double? H)>();
        void MarkSwatches()
        {
            foreach (var (b, h) in swatchButtons) b.Classes.Set("selected", h == p.ColorHue);
            preview.Tint = p.ColorHue is { } x ? AppHost.Theme.FolderBrush(x) : null;
        }

        foreach (var h in new double?[] { null }.Concat(FolderColors.Swatches.Select(x => (double?)x)))
        {
            var b = new Button { Classes = { "swatch" }, Background = h is { } v ? AppHost.Theme.FolderBrush(v) : AppHost.Theme.Brush("SurfaceRaised") };
            if (h is null) b.Content = new LineIcon { Icon = "x", Size = 12 };
            b.Click += (_, _) =>
            {
                p.ColorHue = h;
                MarkSwatches();
            };
            swatchButtons.Add((b, h));
            swatches.Children.Add(b);
        }

        MarkSwatches();

        var layouts = new[] { (ProfileLayout.Tabs, "Profile.LayoutTabs"), (ProfileLayout.NewWindow, "Profile.LayoutWindow"), (ProfileLayout.Split, "Profile.LayoutSplit") };
        var layout = new ComboBox
        {
            ItemsSource = layouts.Select(l => Loc.T(l.Item2)).ToList(), SelectedIndex = Array.FindIndex(layouts, l => l.Item1 == p.Layout),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var sequential = new ToggleSwitch { IsChecked = p.Sequential, OnContent = "", OffContent = "" };

        // Selected connections (ordered) and the picker.
        var list = new StackPanel { Spacing = 4 };
        var search = new TextBox { PlaceholderText = Loc.T("Profile.AddSearch"), Classes = { "search" } };
        var results = new StackPanel { Spacing = 2 };
        var resultScroll = new ScrollViewer { Content = results, MaxHeight = 170, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };

        Control EntryRow(Entry e, int index)
        {
            var eff = ws.Resolver.Resolve(e);
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto,Auto,Auto") };
            var icon = new LineIcon { Icon = eff.Icon, Size = 15, Margin = new Thickness(0, 0, 8, 0), [!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.TextSecondary") };
            var text = new TextBlock { Text = string.IsNullOrEmpty(e.Name) ? Loc.T("Unnamed") : e.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
            ToolTip.SetTip(text, ws.Tree.Path(e));
            Button Small(string ic, string tip, Action a, bool enabled = true)
            {
                var b = new Button { Classes = { "icon", "small" }, Content = new LineIcon { Icon = ic, Size = 14 }, IsEnabled = enabled };
                ToolTip.SetTip(b, Loc.T(tip));
                b.Click += (_, _) =>
                {
                    a();
                    Refresh();
                };
                return b;
            }

            var up = Small("arrow-up", "Profile.Up", () => (p.Entries[index - 1], p.Entries[index]) = (p.Entries[index], p.Entries[index - 1]), index > 0);
            var down = Small("chevron-down", "Profile.Down", () => (p.Entries[index + 1], p.Entries[index]) = (p.Entries[index], p.Entries[index + 1]), index < p.Entries.Count - 1);
            var remove = Small("x", "Profile.Remove", () => p.Entries.RemoveAt(index));
            Grid.SetColumn(text, 1);
            Grid.SetColumn(up, 2);
            Grid.SetColumn(down, 3);
            Grid.SetColumn(remove, 4);
            g.Children.Add(icon);
            g.Children.Add(text);
            g.Children.Add(up);
            g.Children.Add(down);
            g.Children.Add(remove);
            return new Border { Classes = { "tile" }, Padding = new Thickness(10, 3, 4, 3), Child = g };
        }

        void Refresh()
        {
            list.Children.Clear();
            p.Entries.RemoveAll(id => ws.Tree.Get(id) is null);
            for (var i = 0; i < p.Entries.Count; i++) list.Children.Add(EntryRow(ws.Tree.Get(p.Entries[i])!, i));
            if (p.Entries.Count == 0) list.Children.Add(new TextBlock { Text = Loc.T("Profile.NoEntries"), Classes = { "muted" }, Margin = new Thickness(2, 4) });
            RefreshResults();
        }

        void RefreshResults()
        {
            results.Children.Clear();
            var q = Core.Search.SearchQuery.Parse(search.Text);
            var matches = ws.Tree.All.Where(e => EntryKinds.IsConnection(e.Kind) && !p.Entries.Contains(e.Id) && (q.IsEmpty || q.Score(e, ws.Resolver) > 0))
                .OrderBy(e => ws.Tree.Path(e), StringComparer.CurrentCultureIgnoreCase).Take(60);
            foreach (var e in matches)
            {
                var b = new Button { Classes = { "ghost" }, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(8, 4) };
                var g = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto") };
                var icon = new LineIcon { Icon = ws.Resolver.Resolve(e).Icon, Size = 14, Margin = new Thickness(0, 0, 8, 0) };
                var t = new TextBlock { Text = e.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
                var path = new TextBlock { Text = ws.Tree.Path(e), Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0), MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis };
                Grid.SetColumn(t, 1);
                Grid.SetColumn(path, 2);
                g.Children.Add(icon);
                g.Children.Add(t);
                g.Children.Add(path);
                b.Content = g;
                var id = e.Id;
                b.Click += (_, _) =>
                {
                    p.Entries.Add(id);
                    Refresh();
                };
                results.Children.Add(b);
            }
        }

        search.TextChanged += (_, _) => RefreshResults();
        Refresh();

        var nameRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        Grid.SetColumn(name, 1);
        name.Margin = new Thickness(8, 0, 0, 0);
        nameRow.Children.Add(pick);
        nameRow.Children.Add(name);

        var options = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        var l1 = new TextBlock { Text = Loc.T("Profile.Layout"), VerticalAlignment = VerticalAlignment.Center };
        layout.Width = 220;
        Grid.SetColumn(layout, 1);
        var l2 = new TextBlock { Text = Loc.T("Profile.Sequential"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
        sequential.Margin = new Thickness(0, 10, 0, 0);
        sequential.HorizontalAlignment = HorizontalAlignment.Right;
        Grid.SetRow(l2, 1);
        Grid.SetRow(sequential, 1);
        Grid.SetColumn(sequential, 1);
        options.Children.Add(l1);
        options.Children.Add(layout);
        options.Children.Add(l2);
        options.Children.Add(sequential);

        var body = new StackPanel { Spacing = 6 };
        body.Children.Add(nameRow);
        body.Children.Add(swatches);
        body.Children.Add(options);
        body.Children.Add(new TextBlock { Text = Loc.T("Profile.Entries"), Classes = { "section" }, Margin = new Thickness(0, 14, 0, 2) });
        body.Children.Add(new ScrollViewer { Content = list, MaxHeight = 220 });
        body.Children.Add(new Hint { Text = Loc.T("Profile.SplitHint") });
        body.Children.Add(search);
        body.Children.Add(resultScroll);

        var dlg = new DialogWindow(Loc.T(profile is null ? "Profile.New" : "Profile.Edit"), "layers", 560) { Body = body };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Save"), "check", true, false, () => !string.IsNullOrWhiteSpace(name.Text), "ok");
        dlg.Opened += (_, _) => name.Focus();
        if (await dlg.ShowDialog<object?>(owner) is not "ok") return null;
        p.Name = name.Text!.Trim();
        p.Layout = layouts[Math.Max(0, layout.SelectedIndex)].Item1;
        p.Sequential = sequential.IsChecked == true;
        return p;
    }
}
