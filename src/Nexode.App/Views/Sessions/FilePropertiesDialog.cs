using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.ViewModels;
using Nexode.App.ViewModels.Sessions;
using Nexode.Protocols.Files;

namespace Nexode.App.Views.Sessions;

/// <summary>Properties of one or more files; Unix permissions (rwx for owner, group, others) can be changed where supported.</summary>
public static class FilePropertiesDialog
{
    /// <summary>New permission bits, or null when nothing was changed.</summary>
    public static async Task<int?> ShowAsync(Window owner, IReadOnlyList<FileRow> rows, bool canChange)
    {
        var first = rows[0];
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("130,*"), RowSpacing = 6 };
        var row = 0;
        void Line(string label, string? value, bool mono = false)
        {
            if (string.IsNullOrEmpty(value)) return;
            grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var l = new TextBlock { Text = label, Classes = { "label" }, VerticalAlignment = VerticalAlignment.Top };
            var v = new SelectableTextBlock { Text = value, TextWrapping = TextWrapping.Wrap };
            if (mono) v.Classes.Add("mono");
            Grid.SetRow(l, row);
            Grid.SetRow(v, row);
            Grid.SetColumn(v, 1);
            grid.Children.Add(l);
            grid.Children.Add(v);
            row++;
        }

        if (rows.Count == 1)
        {
            Line(Loc.T("Field.Name"), first.Name);
            Line(Loc.T("Files.Path"), first.FullPath, mono: true);
            Line(Loc.T("Files.ColType"), first.TypeText + (first.IsLink ? " · " + Loc.T("Files.Link") : ""));
            Line(Loc.T("Files.ColSize"), first.IsDirectory ? null : $"{SettingsTabViewModel.FormatSize(first.Size)} ({first.Size:N0} B)");
            Line(Loc.T("Files.ColModified"), first.ModifiedText);
        }
        else
        {
            Line(Loc.T("Files.Selection"), Loc.F("Files.SelectionCount", rows.Count));
            Line(Loc.T("Files.ColSize"), SettingsTabViewModel.FormatSize(rows.Where(r => !r.IsDirectory).Sum(r => r.Size)));
        }

        var body = new StackPanel { Spacing = 14 };
        body.Children.Add(grid);

        var initial = Permissions.Parse(first.Permissions);
        var boxes = new CheckBox[9];
        TextBox? octal = null;
        if (initial is not null || canChange)
        {
            var mode = initial ?? Convert.ToInt32("644", 8);
            var perm = new Grid { ColumnDefinitions = new ColumnDefinitions("130,70,70,70"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto,Auto"), RowSpacing = 4 };
            string[] heads = [Loc.T("Files.Read"), Loc.T("Files.Write"), Loc.T("Files.Execute")];
            for (var c = 0; c < 3; c++)
            {
                var h = new TextBlock { Text = heads[c], Classes = { "muted", "small" }, HorizontalAlignment = HorizontalAlignment.Center };
                Grid.SetColumn(h, c + 1);
                perm.Children.Add(h);
            }

            string[] who = [Loc.T("Files.Owner"), Loc.T("Files.Group"), Loc.T("Files.Others")];
            var updating = false;
            for (var r = 0; r < 3; r++)
            {
                var label = new TextBlock { Text = who[r], Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
                Grid.SetRow(label, r + 1);
                perm.Children.Add(label);
                for (var c = 0; c < 3; c++)
                {
                    var bit = 8 - (r * 3 + c);
                    var box = new CheckBox { IsChecked = (mode & (1 << bit)) != 0, HorizontalAlignment = HorizontalAlignment.Center, IsEnabled = canChange };
                    boxes[r * 3 + c] = box;
                    Grid.SetRow(box, r + 1);
                    Grid.SetColumn(box, c + 1);
                    perm.Children.Add(box);
                }
            }

            int Current()
            {
                var m = 0;
                for (var i = 0; i < 9; i++)
                {
                    if (boxes[i].IsChecked == true) m |= 1 << (8 - i);
                }

                return m;
            }

            octal = new TextBox { Text = Permissions.Octal(mode), Classes = { "mono" }, Width = 70, HorizontalAlignment = HorizontalAlignment.Left, IsEnabled = canChange, MaxLength = 3 };
            var octLabel = new TextBlock { Text = Loc.T("Files.Octal"), Classes = { "label" }, VerticalAlignment = VerticalAlignment.Center };
            Grid.SetRow(octLabel, 4);
            Grid.SetRow(octal, 4);
            Grid.SetColumn(octal, 1);
            Grid.SetColumnSpan(octal, 2);
            perm.Children.Add(octLabel);
            perm.Children.Add(octal);
            foreach (var b in boxes)
            {
                b.IsCheckedChanged += (_, _) =>
                {
                    if (updating) return;
                    updating = true;
                    octal.Text = Permissions.Octal(Current());
                    updating = false;
                };
            }

            octal.TextChanged += (_, _) =>
            {
                if (updating || octal.Text is not { Length: 3 } t || t.Any(ch => ch is < '0' or > '7')) return;
                var m = Convert.ToInt32(t, 8);
                updating = true;
                for (var i = 0; i < 9; i++) boxes[i].IsChecked = (m & (1 << (8 - i))) != 0;
                updating = false;
            };

            body.Children.Add(new TextBlock { Text = Loc.T("Files.PermSection"), Classes = { "section" } });
            body.Children.Add(perm);
            if (!canChange) body.Children.Add(new Hint { Text = Loc.T("Files.NoChmod") });
        }

        var dlg = new DialogWindow(rows.Count == 1 ? first.Name : Loc.T("Files.Properties"), first.IsDirectory ? "folder" : "file", 500) { Body = body };
        if (canChange && octal is not null)
        {
            dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
            dlg.AddButton(Loc.T("Files.Apply"), "check", true, false, () => octal.Text is { Length: 3 } t && t.All(ch => ch is >= '0' and <= '7'), "ok");
        }
        else
        {
            dlg.AddButton(Loc.T("Ok"), "check", true, false, null, null);
        }

        if (await dlg.ShowDialog<object?>(owner) is not "ok" || octal?.Text is not { Length: 3 } text) return null;
        var result = Convert.ToInt32(text, 8);
        return result == initial && rows.Count == 1 ? null : result;
    }
}
