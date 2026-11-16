using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Model;

namespace Nexode.App.Views;

/// <summary>
/// Changes selected fields of several entries at once: each row is applied only when its checkbox is ticked
/// (fields that do not exist for an entry kind are skipped for that entry).
/// </summary>
public static class BulkEditDialog
{
    private sealed record Choice(object? Value, string Text, string? Icon = null)
    {
        public override string ToString() => Text;
    }

    public static async Task<Action<Entry>?> ShowAsync(Window owner, IReadOnlyList<Entry> entries)
    {
        var ws = AppHost.Workspace;
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,170,*"), RowSpacing = 8 };
        var applies = new List<(CheckBox Box, Action<Entry> Apply)>();
        var row = 0;

        void Row(string label, Control editor, Action<Entry> apply)
        {
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var box = new CheckBox { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 6, 0) };
            var text = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center };
            editor.IsEnabled = false;
            box.IsCheckedChanged += (_, _) => editor.IsEnabled = box.IsChecked == true;
            // Editing a field ticks its box.
            editor.GotFocus += (_, _) => box.IsChecked = true;
            Grid.SetRow(box, row);
            Grid.SetRow(text, row);
            Grid.SetRow(editor, row);
            Grid.SetColumn(text, 1);
            Grid.SetColumn(editor, 2);
            grid.Children.Add(box);
            grid.Children.Add(text);
            grid.Children.Add(editor);
            applies.Add((box, apply));
            row++;
        }

        ComboBox Combo(IEnumerable<Choice> items, object? selected = null)
        {
            var list = items.ToList();
            var c = new ComboBox { ItemsSource = list, HorizontalAlignment = HorizontalAlignment.Stretch };
            c.SelectedItem = list.FirstOrDefault(i => Equals(i.Value, selected)) ?? list.FirstOrDefault();
            return c;
        }

        object? Value(ComboBox c) => (c.SelectedItem as Choice)?.Value;

        // Category
        var category = Combo([new Choice(null, Loc.T("Category.None")), .. ws.CategoryList.Select(c => new Choice(c.Id, CategoryNames.Of(c)))]);
        Row(Loc.T("Field.Category"), category, e => e.CategoryId = (string?)Value(category));

        // Environment
        var env = Combo([new Choice(null, Loc.T("Env.Inherit")), new Choice("", Loc.T("Env.None")), .. ws.EnvironmentList.Select(x => new Choice(x.Id, Envs.Name(x)))]);
        Row(Loc.T("Field.Environment"), env, e => e.EnvironmentId = (string?)Value(env));

        // Operating system
        var os = Combo(Enum.GetValues<OsFamily>().Select(o => new Choice(o, Loc.T("Os." + o))));
        Row(Loc.T("Field.Os"), os, e =>
        {
            if (!e.IsContainer || e.Kind == EntryKind.Host) e.Os = (OsFamily)Value(os)!;
        });

        // Tags: add / remove / replace
        var tagMode = Combo([new Choice("add", Loc.T("Bulk.TagsAdd")), new Choice("remove", Loc.T("Bulk.TagsRemove")), new Choice("replace", Loc.T("Bulk.TagsReplace"))]);
        tagMode.Width = 150;
        var tags = new TextBox { PlaceholderText = Loc.T("Editor.TagsWatermark") };
        var tagPanel = new DockPanel();
        DockPanel.SetDock(tagMode, Dock.Left);
        tagMode.Margin = new Thickness(0, 0, 6, 0);
        tagPanel.Children.Add(tagMode);
        tagPanel.Children.Add(tags);
        Row(Loc.T("Field.Tags"), tagPanel, e =>
        {
            var list = (tags.Text ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(t => t.TrimStart('#')).Where(t => t.Length > 0).ToList();
            e.Tags = (string?)Value(tagMode) switch
            {
                "remove" => e.Tags.Where(t => !list.Contains(t, StringComparer.OrdinalIgnoreCase)).ToList(),
                "replace" => list,
                _ => e.Tags.Concat(list).Distinct(StringComparer.OrdinalIgnoreCase).ToList()
            };
        });

        // Credentials: mode (+ profile), user, domain
        var profiles = ws.Tree.All.Where(x => x.Kind == EntryKind.Credential).OrderBy(x => x.Name).Select(x => new Choice(x.Id, x.Name)).ToList();
        var credMode = Combo(Enum.GetValues<CredentialMode>().Where(m => m != CredentialMode.None).Select(m => new Choice(m, Loc.T("Cred." + m))));
        var profile = Combo(profiles);
        profile.IsVisible = false;
        credMode.SelectionChanged += (_, _) => profile.IsVisible = Value(credMode) is CredentialMode.Profile && profiles.Count > 0;
        var credPanel = new StackPanel { Spacing = 6 };
        credPanel.Children.Add(credMode);
        credPanel.Children.Add(profile);
        Row(Loc.T("Editor.CredentialMode"), credPanel, e =>
        {
            if (!EntryKinds.HasCredentials(e.Kind) || e.Kind == EntryKind.Credential) return;
            var m = (CredentialMode)Value(credMode)!;
            e.CredentialMode = m;
            e.CredentialId = m == CredentialMode.Profile ? (Guid?)Value(profile) : null;
        });
        var user = new TextBox { PlaceholderText = Loc.T("Bulk.EmptyClears") };
        Row(Loc.T("Field.Username"), user, e =>
        {
            if (EntryKinds.HasCredentials(e.Kind)) e.Username = string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim();
        });
        var domain = new TextBox { PlaceholderText = Loc.T("Bulk.EmptyClears") };
        Row(Loc.T("Field.Domain"), domain, e =>
        {
            if (EntryKinds.HasCredentials(e.Kind)) e.Domain = string.IsNullOrWhiteSpace(domain.Text) ? null : domain.Text.Trim();
        });

        // Port (only kinds with a port)
        var port = new NumericUpDown { Minimum = 0, Maximum = 65535, Increment = 1, FormatString = "0", Value = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        Row(Loc.T("Field.Port") + " (0 = " + Loc.T("Bulk.Default") + ")", port, e =>
        {
            if (!EntryKinds.HasHost(e.Kind) || e.Kind == EntryKind.Host) return;
            var p = (int)(port.Value ?? 0);
            e.Port = p <= 0 || p == EntryKinds.DefaultPort(e.Kind) ? null : p;
        });

        // Jump host
        var jumps = Combo([new Choice(null, Loc.T("Tunnel.Direct")), .. ws.Tree.All.Where(x => x.Kind == EntryKind.Ssh).OrderBy(x => x.Name).Select(x => new Choice(x.Id, x.Name))]);
        Row(Loc.T("Tunnel.Via"), jumps, e =>
        {
            if (!EntryKinds.CanTunnel(e.Kind)) return;
            var id = (Guid?)Value(jumps);
            e.SetSetting(SettingKeys.TunnelVia, id == e.Id ? null : id?.ToString());
        });

        // Automatic reconnect
        var reconnect = Combo([new Choice(null, Loc.T("Editor.ReconnectDefault")), new Choice("1", Loc.T("Editor.ReconnectOn")), new Choice("0", Loc.T("Editor.ReconnectOff"))]);
        Row(Loc.T("Editor.AutoReconnect"), reconnect, e =>
        {
            if (EntryKinds.IsSession(e.Kind)) e.SetSetting(SettingKeys.AutoReconnect, (string?)Value(reconnect));
        });

        // Favorite
        var fav = Combo([new Choice(true, Loc.T("Bulk.Yes")), new Choice(false, Loc.T("Bulk.No"))]);
        Row(Loc.T("Field.Favorite"), fav, e => e.IsFavorite = (bool)Value(fav)!);

        var kinds = entries.GroupBy(e => e.Kind).Select(g => $"{g.Count()} × {Loc.Kind(g.Key)}");
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(new TextBlock { Text = Loc.F("Bulk.Intro", entries.Count) + " (" + string.Join(", ", kinds) + ")", TextWrapping = TextWrapping.Wrap, Classes = { "muted" } });
        body.Children.Add(grid);
        body.Children.Add(new Hint { Text = Loc.T("Bulk.Hint") });

        var dlg = new DialogWindow(Loc.F("Bulk.Title", entries.Count), "square-pen", 640) { Body = new ScrollViewer { Content = body, MaxHeight = 620 } };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        dlg.AddButton(Loc.T("Bulk.Apply"), "check", true, false, () => applies.Any(a => a.Box.IsChecked == true), "ok");
        if (await dlg.ShowDialog<object?>(owner) is not "ok") return null;
        var chosen = applies.Where(a => a.Box.IsChecked == true).Select(a => a.Apply).ToList();
        return e =>
        {
            foreach (var apply in chosen) apply(e);
        };
    }
}
