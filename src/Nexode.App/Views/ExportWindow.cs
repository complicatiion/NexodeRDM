using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Files;

namespace Nexode.App.Views;

/// <summary>Export of the data source or a selection as .nexode: secrets none / fully encrypted / secrets only.</summary>
public sealed class ExportWindow : Window
{
    private readonly IReadOnlyList<Guid> _selection;

    public ExportWindow() : this([])
    {
    }

    public ExportWindow(IReadOnlyList<Guid> selection)
    {
        _selection = selection;
        var ws = AppHost.Workspace;
        Title = Loc.T("Export.Title");
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Nexode/Assets/nexode.ico")));
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("B.BgContent");

        var all = new RadioButton { GroupName = "scope", Content = Loc.F("Export.ScopeAll", ws.Tree.Count), IsChecked = selection.Count == 0 };
        var sel = new RadioButton { GroupName = "scope", Content = Loc.F("Export.ScopeSelection", selection.Count), IsChecked = selection.Count > 0, IsEnabled = selection.Count > 0 };
        var flatten = new CheckBox { Content = Loc.T("Export.Flatten"), IsChecked = true, Margin = new Thickness(26, 0, 0, 0) };
        flatten.IsEnabled = sel.IsChecked == true;
        sel.IsCheckedChanged += (_, _) => flatten.IsEnabled = sel.IsChecked == true;

        var none = new RadioButton { GroupName = "secrets", Content = Loc.T("Export.SecretsNone") };
        var full = new RadioButton { GroupName = "secrets", Content = Loc.T("Export.SecretsAll"), IsChecked = true };
        var only = new RadioButton { GroupName = "secrets", Content = Loc.T("Export.SecretsOnly") };
        var pw1 = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("Export.Password") };
        var pw2 = new TextBox { PasswordChar = '•', PlaceholderText = Loc.T("PasswordRepeat") };
        var pwPanel = new StackPanel { Spacing = 8 };
        var useMaster = AppHost.Settings.ExportWithMasterPassword && ws.HasPassword;
        if (useMaster)
        {
            pwPanel.Children.Add(new Border
            {
                Classes = { "notice" },
                Child = new IconLabel { Icon = "key-round", Text = Loc.T("Export.UsesMaster") }
            });
        }
        else
        {
            pwPanel.Children.Add(pw1);
            pwPanel.Children.Add(pw2);
        }

        none.IsCheckedChanged += (_, _) => pwPanel.IsVisible = none.IsChecked != true;
        var error = new TextBlock { Classes = { "danger" }, IsVisible = false, TextWrapping = TextWrapping.Wrap };

        var body = new StackPanel { Spacing = 8 };
        body.Children.Add(new TextBlock { Text = Loc.T("Export.Scope"), Classes = { "section" } });
        body.Children.Add(all);
        body.Children.Add(sel);
        body.Children.Add(flatten);
        body.Children.Add(new TextBlock { Text = Loc.T("Export.Secrets"), Classes = { "section" }, Margin = new Thickness(0, 10, 0, 0) });
        body.Children.Add(none);
        body.Children.Add(full);
        body.Children.Add(only);
        body.Children.Add(pwPanel);
        body.Children.Add(new TextBlock { Text = Loc.T("Export.Hint"), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap });
        body.Children.Add(error);

        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = Loc.T("Cancel") }, IsCancel = true, MinWidth = 100 };
        cancel.Click += (_, _) => Close(false);
        var save = new Button { Content = new IconLabel { Icon = "file-up", Text = Loc.T("Export.Save") }, Classes = { "primary" }, IsDefault = true, MinWidth = 120 };
        save.Click += async (_, _) =>
        {
            var mode = none.IsChecked == true ? SecretExportMode.None : only.IsChecked == true ? SecretExportMode.SecretsOnly : SecretExportMode.EncryptAll;
            if (mode != SecretExportMode.None && useMaster)
            {
                if (Owner?.DataContext is not MainWindowViewModel mvm) return;
                var master = await Dialog.PasswordCheckAsync(this, Loc.T("Vault.ReauthTitle"), Loc.T("Export.MasterText"), p => ws.VerifyPasswordAsync(p), "key-round");
                if (master is null || !await mvm.Actions.EnsureUnlockedAsync()) return;
                pw1.Text = master;
                pw2.Text = master;
            }

            if (mode != SecretExportMode.None)
            {
                if ((pw1.Text ?? "").Length < 8)
                {
                    error.Text = Loc.F("PasswordTooShort", 8);
                    error.IsVisible = true;
                    return;
                }

                if (pw1.Text != pw2.Text)
                {
                    error.Text = Loc.T("PasswordMismatch");
                    error.IsVisible = true;
                    return;
                }

                if (ws.IsLocked && Owner?.DataContext is MainWindowViewModel vm && !await vm.Actions.EnsureUnlockedAsync()) return;
            }

            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Export.Title"),
                SuggestedFileName = $"{(ws.Info?.Name ?? "nexode").Replace(' ', '-').ToLowerInvariant()}-{DateTime.Now:yyyyMMdd}.nexode",
                DefaultExtension = "nexode",
                FileTypeChoices = [new FilePickerFileType("Nexode") { Patterns = ["*.nexode"] }]
            });
            var path = file?.TryGetLocalPath();
            if (path is null) return;

            save.IsEnabled = false;
            var roots = sel.IsChecked == true ? _selection : [];
            var options = new ExportOptions { Roots = roots, SecretMode = mode, Password = pw1.Text, FlattenInheritance = flatten.IsChecked == true };
            var tree = ws.Tree;
            var categories = ws.CategoryList;
            var name = ws.Info?.Name ?? "";
            // Secrets are read on the UI thread (vault), the key derivation runs in the background.
            var secrets = new Dictionary<(Guid, string), string?>();
            foreach (var e in tree.All)
            {
                foreach (var f in ExportBuilder.SecretFields)
                {
                    if (ws.HasSecret(e.Id)) secrets[(e.Id, f)] = ws.GetSecret(e.Id, f);
                }
            }

            await Task.Run(() => ExportBuilder.Build(tree, categories, ws.EnvironmentList, ws.Profiles, name, AppHost.Version, options, (id, f) => secrets.GetValueOrDefault((id, f))).Save(path));
            (Owner?.DataContext as MainWindowViewModel)?.ShowToast(Loc.F("Toast.Exported", Path.GetFileName(path)));
            Close(true);
        };

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(cancel);
        buttons.Children.Add(save);

        var root = new StackPanel { Margin = new Thickness(22, 18, 22, 16) };
        root.Children.Add(new IconLabel { Icon = "file-up", Text = Loc.T("Export.Title"), IconSize = 20, Margin = new Thickness(0, 0, 0, 14) });
        root.Children.Add(body);
        root.Children.Add(buttons);
        Content = root;
        Opened += (_, _) => WindowChrome.Apply(this);
    }
}
