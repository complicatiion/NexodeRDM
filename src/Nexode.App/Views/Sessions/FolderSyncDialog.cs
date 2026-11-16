using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.ViewModels;
using Nexode.Core.Files;

namespace Nexode.App.Views.Sessions;

/// <summary>Result of the compare dialog: what to synchronize.</summary>
public sealed record FolderSyncChoice(SyncDirection Direction, bool DeleteExtra, string Excludes);

/// <summary>Shows the differences between the local and the remote folder and lets the user pick the sync direction.</summary>
public static class FolderSyncDialog
{
    private const int MaxRows = 1500;

    public static async Task<FolderSyncChoice?> ShowAsync(Window owner, string localRoot, string remoteRoot, IReadOnlyList<SyncDiff> allDiffs, bool ignoreCase)
    {
        var excludeBox = new TextBox { Text = Services.AppHost.Settings.SyncExcludes, Classes = { "mono" }, PlaceholderText = SyncExcludes.Default };
        var diffs = new SyncExcludes(excludeBox.Text, ignoreCase).Apply(allDiffs);
        var body = new StackPanel { Spacing = 12 };

        // Both roots
        var roots = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), RowDefinitions = new RowDefinitions("Auto,Auto"), RowSpacing = 4 };
        void Root(int row, string icon, string path)
        {
            var i = new LineIcon { Icon = icon, Size = 15, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
            var t = new SelectableTextBlock { Text = path, Classes = { "mono" }, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(i, row);
            Grid.SetRow(t, row);
            Grid.SetColumn(t, 1);
            roots.Children.Add(i);
            roots.Children.Add(t);
        }

        Root(0, "laptop", localRoot);
        Root(1, "server", remoteRoot);
        body.Children.Add(roots);

        // Counts per state
        var counts = new WrapPanel { ItemSpacing = 6, LineSpacing = 6 };
        void UpdateCounts()
        {
            counts.Children.Clear();
            foreach (var state in new[] { SyncState.LocalOnly, SyncState.RemoteOnly, SyncState.LocalNewer, SyncState.RemoteNewer, SyncState.Different, SyncState.Same })
            {
                var n = diffs.Count(d => d.State == state);
                var pill = new Border { Classes = { "pill", state == SyncState.Same ? "" : "accent" }, Padding = new Thickness(8, 1) };
                pill.Child = new IconLabel { Icon = StateIcon(state), Text = $"{Loc.T("Sync.State." + state)}: {n.ToString(CultureInfo.CurrentCulture)}" };
                counts.Children.Add(pill);
            }

            var hidden = allDiffs.Count - diffs.Count;
            if (hidden > 0) counts.Children.Add(new Border { Classes = { "pill" }, Padding = new Thickness(8, 1), Child = new IconLabel { Icon = "eye-off", Text = Loc.F("Sync.Excluded", hidden) } });
        }

        UpdateCounts();
        body.Children.Add(counts);

        // Options
        var direction = new ComboBox
        {
            ItemsSource = new[] { Loc.T("Sync.Upload"), Loc.T("Sync.Download"), Loc.T("Sync.Both") },
            SelectedIndex = 0,
            Width = 260
        };
        var deleteExtra = new CheckBox { Content = Loc.T("Sync.DeleteExtra") };
        var showSame = new CheckBox { Content = Loc.T("Sync.ShowSame") };
        var options = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
        options.Children.Add(direction);
        options.Children.Add(deleteExtra);
        options.Children.Add(showSame);
        body.Children.Add(options);
        var excludeRow = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
        excludeRow.Children.Add(new IconLabel { Icon = "eye-off", Text = Loc.T("Sync.Exclude"), Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(excludeBox, 1);
        excludeRow.Children.Add(excludeBox);
        ToolTip.SetTip(excludeBox, Loc.T("Sync.ExcludeTip"));
        body.Children.Add(excludeRow);
        var warning = new Hint { Text = "", IsVisible = false };
        body.Children.Add(warning);

        // Differences
        var list = new StackPanel { Spacing = 1 };
        var scroller = new ScrollViewer { Content = list, Height = 300, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
        body.Children.Add(new Border { Classes = { "card" }, Padding = new Thickness(6), Child = scroller });

        var dlg = new DialogWindow(Loc.T("Sync.Title"), "folder-sync", 860) { Body = body };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        var run = dlg.AddButton(Loc.T("Sync.Run"), "refresh-cw", true, false, null, "ok");

        var plan = (IReadOnlyList<SyncAction>)[];
        void Update()
        {
            var dir = (SyncDirection)Math.Max(0, direction.SelectedIndex);
            deleteExtra.IsEnabled = dir != SyncDirection.Both;
            plan = FolderSync.Plan(diffs, dir, deleteExtra.IsChecked == true && dir != SyncDirection.Both);
            var byPath = plan.ToDictionary(a => a.Diff.RelativePath, a => a.Kind);
            var deletions = plan.Count(a => a.Kind is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote);
            warning.Text = Loc.F("Sync.DeleteWarning", deletions);
            warning.IsVisible = deletions > 0;
            run.IsEnabled = plan.Count > 0;
            run.Content = new IconLabel { Icon = "refresh-cw", Text = Loc.F("Sync.RunCount", plan.Count) };

            list.Children.Clear();
            var shown = 0;
            foreach (var d in diffs)
            {
                if (d.State == SyncState.Same && showSame.IsChecked != true) continue;
                if (++shown > MaxRows)
                {
                    list.Children.Add(new TextBlock { Text = Loc.F("Sync.More", diffs.Count - MaxRows), Classes = { "muted", "small" }, Margin = new Thickness(6, 4) });
                    break;
                }

                list.Children.Add(Row(d, byPath.TryGetValue(d.RelativePath, out var k) ? k : null));
            }

            if (shown == 0) list.Children.Add(new TextBlock { Text = Loc.T("Sync.NoDifferences"), Classes = { "muted" }, Margin = new Thickness(6, 8) });
        }

        excludeBox.LostFocus += (_, _) => ApplyExcludes();
        excludeBox.KeyDown += (_, e) =>
        {
            if (e.Key != Avalonia.Input.Key.Enter) return;
            ApplyExcludes();
            e.Handled = true;
        };

        void ApplyExcludes()
        {
            diffs = new SyncExcludes(excludeBox.Text, ignoreCase).Apply(allDiffs);
            UpdateCounts();
            Update();
        }

        direction.SelectionChanged += (_, _) => Update();
        deleteExtra.IsCheckedChanged += (_, _) => Update();
        showSame.IsCheckedChanged += (_, _) => Update();
        Update();

        if (await dlg.ShowDialog<object?>(owner) is not "ok" || plan.Count == 0) return null;
        return new FolderSyncChoice((SyncDirection)Math.Max(0, direction.SelectedIndex), deleteExtra.IsChecked == true && direction.SelectedIndex != (int)SyncDirection.Both,
            excludeBox.Text?.Trim() ?? "");
    }

    private static Control Row(SyncDiff d, SyncActionKind? action)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*,150,32,150"), Height = 26 };
        var icon = new LineIcon { Icon = d.IsDirectory ? "folder" : StateIcon(d.State), Size = 14, Margin = new Thickness(4, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        if (d.State != SyncState.Same) icon.Foreground = (IBrush?)Application.Current?.FindResource("B.AccentText");
        var name = new TextBlock { Text = d.RelativePath, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        ToolTip.SetTip(name, $"{d.RelativePath}\n{Loc.T("Sync.State." + d.State)}");
        var left = new TextBlock { Text = Side(d.Local), Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center, TextAlignment = TextAlignment.Right };
        var arrow = new LineIcon
        {
            Icon = action switch
            {
                SyncActionKind.Upload or SyncActionKind.CreateRemoteFolder => "arrow-right",
                SyncActionKind.Download or SyncActionKind.CreateLocalFolder => "arrow-left",
                SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote => "trash-2",
                _ => "minus"
            },
            Size = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (action is SyncActionKind.DeleteLocal or SyncActionKind.DeleteRemote) arrow.Foreground = (IBrush?)Application.Current?.FindResource("B.DangerText");
        else if (action is null) arrow.Opacity = 0.35;
        var right = new TextBlock { Text = Side(d.Remote), Classes = { "small", "muted" }, VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(name, 1);
        Grid.SetColumn(left, 2);
        Grid.SetColumn(arrow, 3);
        Grid.SetColumn(right, 4);
        grid.Children.Add(icon);
        grid.Children.Add(name);
        grid.Children.Add(left);
        grid.Children.Add(arrow);
        grid.Children.Add(right);
        return grid;
    }

    private static string Side(SyncFile? f) => f is null ? "–"
        : f.IsDirectory ? Loc.T("Files.TypeFolder")
        : $"{SettingsTabViewModel.FormatSize(f.Size)} · {f.ModifiedUtc?.ToLocalTime().ToString("g", CultureInfo.CurrentCulture) ?? "?"}";

    private static string StateIcon(SyncState s) => s switch
    {
        SyncState.LocalOnly => "laptop",
        SyncState.RemoteOnly => "server",
        SyncState.LocalNewer => "arrow-up-right",
        SyncState.RemoteNewer => "arrow-down-left",
        SyncState.Different => "equal-not",
        _ => "equal"
    };
}
