using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Model;
using Nexode.Core.Rdp;

namespace Nexode.App.Views;

/// <summary>
/// Shadow a user's session on an RDP server (Windows): lists the sessions (qwinsta with the current Windows logon), or
/// takes a session ID typed in, then starts mstsc /shadow with "control" and optionally without the consent prompt.
/// </summary>
public static class RdpShadowDialog
{
    public static async Task ShowAsync(MainWindowViewModel vm, Entry entry)
    {
        if (vm.Window is not { } owner) return;
        var eff = AppHost.Workspace.Resolver.Resolve(entry);
        var host = eff.Host ?? "";

        var body = new StackPanel { Spacing = 10 };
        body.Children.Add(new TextBlock { Text = Loc.F("Shadow.Intro", host), TextWrapping = TextWrapping.Wrap });
        var status = new TextBlock { Text = Loc.T("Shadow.Loading"), Classes = { "muted" }, TextWrapping = TextWrapping.Wrap };
        body.Children.Add(status);
        var list = new ListBox { MaxHeight = 260, IsVisible = false, SelectionMode = SelectionMode.Single };
        body.Children.Add(list);
        var manual = new TextBox { Width = 120, PlaceholderText = "2", HorizontalAlignment = HorizontalAlignment.Left };
        var manualRow = new SettingRow { Label = Loc.T("Shadow.SessionId"), Icon = "hash", Content = manual };
        body.Children.Add(manualRow);
        var control = new CheckBox { Content = Loc.T("Shadow.Control"), IsChecked = true };
        var noConsent = new CheckBox { Content = Loc.T("Shadow.NoConsent") };
        body.Children.Add(control);
        body.Children.Add(noConsent);
        body.Children.Add(new Hint { Text = Loc.T("Shadow.Hint"), Always = true });

        var dlg = new DialogWindow(Loc.T("Shadow.Title"), "eye", 560) { Body = body };
        dlg.AddButton(Loc.T("Cancel"), "x", false, false, null, null);
        var start = dlg.AddButton(Loc.T("Shadow.Start"), "eye", true, false, null, "ok");
        start.IsEnabled = false;

        var sessions = new List<RdpUserSession>();
        void Validate() => start.IsEnabled = list.SelectedIndex >= 0 || int.TryParse(manual.Text, NumberStyles.None, CultureInfo.InvariantCulture, out _);
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedIndex >= 0) manual.Text = sessions[list.SelectedIndex].Id.ToString(CultureInfo.InvariantCulture);
            Validate();
        };
        manual.TextChanged += (_, _) => Validate();

        dlg.Opened += async (_, _) =>
        {
            var (found, error) = await ExternalLauncher.QueryRdpSessionsAsync(host);
            sessions.AddRange(found);
            if (found.Count > 0)
            {
                list.ItemsSource = found.Select(s => new IconLabel
                {
                    Icon = s.IsCurrent ? "user-check" : "user",
                    Text = $"{s.User}  ·  ID {s.Id}  ·  {s.State}" + (s.SessionName is { } n ? $"  ·  {n}" : "")
                }).ToList();
                list.IsVisible = true;
                status.Text = Loc.F("Shadow.Found", found.Count);
            }
            else
            {
                status.Text = Loc.F("Shadow.QueryFailed", error ?? "");
            }
        };

        if (await dlg.ShowDialog<object?>(owner) is not "ok") return;
        if (!int.TryParse(manual.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var id)) return;
        var p = await ConnectionBuilder.BuildAsync(entry, owner, vm.Actions);
        if (p is null) return;
        if (ExternalLauncher.Shadow(p, id, control.IsChecked == true, noConsent.IsChecked == true) is { } err)
        {
            vm.ShowToast(err, true);
            return;
        }

        AppLog.Write($"rdp shadow: entry {entry.Id}, session {id}, control {control.IsChecked == true}");
    }
}
