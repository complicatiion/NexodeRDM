using Avalonia.Markup.Xaml.MarkupExtensions;
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

/// <summary>Quick details card (Space in the tree): key facts and copy buttons.</summary>
public sealed class EntryCard : UserControl
{
    public event Action? CloseRequested;

    public EntryCard(Entry entry, MainWindowViewModel vm)
    {
        var ws = AppHost.Workspace;
        var eff = ws.Resolver.Resolve(entry);
        var root = new StackPanel { Spacing = 10, Width = 320, Margin = new Thickness(4) };

        var head = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        var icon = new LineIcon { Icon = eff.Icon, Size = 22, Margin = new Thickness(0, 0, 12, 0) };
        icon[!LineIcon.ForegroundProperty] = new DynamicResourceExtension("B.AccentText");
        if (eff.ColorHue is { } hue && entry.Kind == EntryKind.Folder) icon.Tint = AppHost.Theme.FolderBrush(hue);
        head.Children.Add(icon);
        var titles = new StackPanel { Spacing = 1 };
        titles.Children.Add(new TextBlock { Text = entry.Name, Classes = { "h3" }, TextTrimming = TextTrimming.CharacterEllipsis });
        titles.Children.Add(new TextBlock { Text = $"{Loc.Kind(entry.Kind)} · {(ws.Tree.Path(entry) is { Length: > 0 } p ? p : Loc.T("Root"))}", Classes = { "muted", "small" }, TextTrimming = TextTrimming.CharacterEllipsis });
        Grid.SetColumn(titles, 1);
        head.Children.Add(titles);
        root.Children.Add(head);

        void Row(string label, string? value, CopyField? copy, bool mono = true)
        {
            if (string.IsNullOrEmpty(value)) return;
            var g = new Grid { ColumnDefinitions = new ColumnDefinitions("92,*,Auto") };
            g.Children.Add(new TextBlock { Text = label, Classes = { "muted", "small" }, VerticalAlignment = VerticalAlignment.Center });
            var v = new TextBlock { Text = value, TextTrimming = TextTrimming.CharacterEllipsis, VerticalAlignment = VerticalAlignment.Center };
            if (mono) v.Classes.Add("mono");
            Grid.SetColumn(v, 1);
            g.Children.Add(v);
            if (copy is { } field)
            {
                var b = new Button { Classes = { "icon", "small" }, Content = new LineIcon { Icon = "copy", Size = 13 } };
                ToolTip.SetTip(b, Loc.T("Copy." + field));
                b.Click += async (_, _) => await vm.Actions.CopyFieldAsync(entry, field);
                Grid.SetColumn(b, 2);
                g.Children.Add(b);
            }

            root.Children.Add(g);
        }

        Row(Loc.T("Field.Host"), eff.Host, CopyField.Host);
        Row(Loc.T("Field.Url"), eff.Url, CopyField.Url);
        if (eff.Port is { } port && !entry.IsContainer && entry.Kind != EntryKind.Web) Row(Loc.T("Field.Port"), port.ToString(System.Globalization.CultureInfo.InvariantCulture), null);
        Row(Loc.T("Field.Username"), eff.Username, CopyField.Username);
        Row(Loc.T("Field.Domain"), eff.Domain, CopyField.Domain);
        if (eff.SecretOwner is { } so && ws.HasSecret(so)) Row(Loc.T("Field.Password"), "••••••••", CopyField.Password);
        if (Envs.Name(eff.Environment) is { } envName) Row(Loc.T("Field.Environment"), envName, null, false);
        if (!string.IsNullOrWhiteSpace(entry.Description)) root.Children.Add(new TextBlock { Text = entry.Description, Classes = { "muted" }, TextWrapping = TextWrapping.Wrap, MaxLines = 3 });

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 4, 0, 0) };
        var edit = new Button { Content = new IconLabel { Icon = "pencil", Text = Loc.T("Menu.Edit") } };
        edit.Click += async (_, _) =>
        {
            CloseRequested?.Invoke();
            await vm.Actions.EditAsync(entry);
        };
        var open = new Button { Classes = { "primary" }, Content = new IconLabel { Icon = entry.Kind == EntryKind.Web ? "globe" : "info", Text = Loc.T(entry.Kind == EntryKind.Web ? "Menu.Open" : "Menu.ShowDetails") } };
        open.Click += (_, _) =>
        {
            CloseRequested?.Invoke();
            if (entry.Kind == EntryKind.Web) vm.Actions.Open(entry);
            else vm.Actions.ShowDetails(entry);
        };
        buttons.Children.Add(edit);
        if (entry.Kind == EntryKind.Web)
        {
            // Quick button: the same panel in the default browser.
            var external = new Button { Classes = { "icon" }, Content = new LineIcon { Icon = "external-link", Size = 16 } };
            ToolTip.SetTip(external, Loc.T("Menu.OpenBrowser"));
            external.Click += (_, _) =>
            {
                CloseRequested?.Invoke();
                vm.Actions.OpenInBrowser(entry);
            };
            buttons.Children.Add(external);
        }

        buttons.Children.Add(open);
        root.Children.Add(buttons);
        Content = root;
    }
}
