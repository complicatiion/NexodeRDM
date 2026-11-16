using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.ViewModels;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.Views;

/// <summary>
/// SSH keys: generate a key pair (Ed25519 or RSA, optional passphrase) and save it, or pick an existing private key;
/// then install the public key on a server (appended to ~/.ssh/authorized_keys over an SSH connection, like
/// ssh-copy-id) and optionally use it for that connection. Result: path of the private key (null = nothing chosen).
/// </summary>
public static class SshKeyDialog
{
    public static async Task<string?> ShowAsync(MainWindowViewModel vm, Entry? target = null, Window? owner = null)
    {
        owner ??= vm.Window;
        if (owner is null) return null;
        string? keyPath = null;
        string? publicLine = null;

        var body = new StackPanel { Spacing = 12 };

        // ---- 1: key
        var type = new ComboBox { ItemsSource = new[] { "Ed25519", "RSA 4096", "RSA 3072" }, SelectedIndex = 0, Width = 220 };
        var comment = new TextBox { Text = $"{Environment.UserName}@{Environment.MachineName}".ToLowerInvariant(), Width = 220 };
        var pass1 = new TextBox { PasswordChar = '•', Width = 220, PlaceholderText = Loc.T("Keys.Optional") };
        var pass2 = new TextBox { PasswordChar = '•', Width = 220 };
        var generate = new Button { Classes = { "w220", "primary" }, Content = new IconLabel { Icon = "key-round", Text = Loc.T("Keys.GenerateSave") } };
        var existing = new Button { Classes = { "w220" }, Content = new IconLabel { Icon = "folder-open", Text = Loc.T("Keys.UseExisting") } };
        var keyCard = new StackPanel();
        keyCard.Children.Add(new TextBlock { Classes = { "cardtitle" }, Text = Loc.T("Keys.Step1") });
        keyCard.Children.Add(Row("Keys.Type", "shapes", type));
        keyCard.Children.Add(Row("Keys.Comment", "tag", comment));
        keyCard.Children.Add(Row("Keys.Passphrase", "lock", pass1));
        keyCard.Children.Add(Row("Keys.Repeat", "lock", pass2));
        var keyButtons = new WrapPanel { ItemSpacing = 8, LineSpacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        keyButtons.Children.Add(existing);
        keyButtons.Children.Add(generate);
        keyCard.Children.Add(keyButtons);
        body.Children.Add(new Border { Classes = { "card" }, Child = keyCard });

        // ---- result: public key
        var pubText = new SelectableTextBlock { Classes = { "mono", "small" }, TextWrapping = TextWrapping.Wrap };
        var fingerprint = new TextBlock { Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap };
        var copy = new Button { Classes = { "ghost", "small" }, Content = new IconLabel { Icon = "copy", Text = Loc.T("Keys.CopyPublic") } };
        var resultCard = new StackPanel { Spacing = 6 };
        var resultHead = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        resultHead.Children.Add(new TextBlock { Classes = { "cardtitle" }, Text = Loc.T("Keys.PublicKey") });
        Grid.SetColumn(copy, 1);
        resultHead.Children.Add(copy);
        resultCard.Children.Add(resultHead);
        resultCard.Children.Add(new Border { Classes = { "logbox" }, Child = pubText });
        resultCard.Children.Add(fingerprint);
        var result = new Border { Classes = { "card" }, Child = resultCard, IsVisible = false };
        body.Children.Add(result);

        // ---- 2: install on a server
        var candidates = AppHost.Workspace.Tree.All.Where(e => e.Kind is EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp && !string.IsNullOrWhiteSpace(e.Host))
            .OrderBy(e => e.Name, Core.Tree.NaturalComparer.Instance).ToList();
        var server = new ComboBox { ItemsSource = candidates.Select(e => e.Name).ToList(), Width = 220, PlaceholderText = Loc.T("Keys.ChooseServer") };
        if (target is not null && candidates.IndexOf(target) is var ti and >= 0) server.SelectedIndex = ti;
        var useKey = new CheckBox { Content = Loc.T("Keys.UseForEntry"), IsChecked = true };
        var install = new Button { Classes = { "w220" }, Content = new IconLabel { Icon = "upload", Text = Loc.T("Keys.Install") }, IsEnabled = false };
        var statusText = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var status = new Border { Classes = { "notice" }, IsVisible = false, Margin = new Thickness(0, 8, 0, 0), Child = statusText };
        var installCard = new StackPanel();
        installCard.Children.Add(new TextBlock { Classes = { "cardtitle" }, Text = Loc.T("Keys.Step2") });
        installCard.Children.Add(new TextBlock { Text = Loc.T("Keys.InstallInfo"), Classes = { "muted", "small" }, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 4) });
        installCard.Children.Add(Row("Keys.Server", "server", server));
        installCard.Children.Add(useKey);
        var installRow = new StackPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) };
        installRow.Children.Add(install);
        installCard.Children.Add(installRow);
        installCard.Children.Add(status);
        body.Children.Add(new Border { Classes = { "card" }, Child = installCard });

        var dlg = new DialogWindow(Loc.T("Keys.Title"), "key-round", 640) { Body = new ScrollViewer { Content = body, MaxHeight = 720 } };
        dlg.AddButton(Loc.T("Close"), "x", false, false, null, null);

        void ShowKey(string path, string line)
        {
            keyPath = path;
            publicLine = line;
            pubText.Text = line;
            fingerprint.Text = $"{SshKeyGen.FingerprintOfLine(line)} · {path}";
            result.IsVisible = true;
            install.IsEnabled = server.SelectedIndex >= 0;
        }

        server.SelectionChanged += (_, _) => install.IsEnabled = publicLine is not null && server.SelectedIndex >= 0;

        generate.Click += async (_, _) =>
        {
            if (pass1.Text != pass2.Text)
            {
                await Dialog.InfoAsync(dlg, Loc.T("Keys.Title"), Loc.T("Keys.Mismatch"), "triangle-alert");
                return;
            }

            var keyType = type.SelectedIndex switch { 1 => SshKeyType.Rsa4096, 2 => SshKeyType.Rsa3072, _ => SshKeyType.Ed25519 };
            var suggested = keyType == SshKeyType.Ed25519 ? "id_ed25519" : "id_rsa";
            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            Directory.CreateDirectory(sshDir);
            var start = await dlg.StorageProvider.TryGetFolderFromPathAsync(sshDir);
            var file = await dlg.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = Loc.T("Keys.SaveTitle"),
                SuggestedFileName = File.Exists(Path.Combine(sshDir, suggested)) ? suggested + "_nexode" : suggested,
                SuggestedStartLocation = start,
                ShowOverwritePrompt = true
            });
            if (file?.TryGetLocalPath() is not { } path) return;

            generate.IsEnabled = false;
            try
            {
                var text = comment.Text ?? "";
                var pass = string.IsNullOrEmpty(pass1.Text) ? null : pass1.Text;
                var pair = await Task.Run(() => SshKeyGen.Generate(keyType, text, pass));
                WritePrivate(path, pair.PrivateKey);
                await File.WriteAllTextAsync(path + ".pub", pair.PublicKey + "\n");
                AppLog.Write($"ssh key generated: {keyType}, {pair.Fingerprint}");
                ShowKey(path, pair.PublicKey);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                await Dialog.InfoAsync(dlg, Loc.T("Keys.Title"), ex.Message, "triangle-alert");
            }
            finally
            {
                generate.IsEnabled = true;
                pass1.Text = pass2.Text = "";
            }
        };

        existing.Click += async (_, _) =>
        {
            var sshDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh");
            var start = Directory.Exists(sshDir) ? await dlg.StorageProvider.TryGetFolderFromPathAsync(sshDir) : null;
            var files = await dlg.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = Loc.T("Keys.UseExisting"), SuggestedStartLocation = start });
            if (files.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
            try
            {
                // A .pub next to it: no passphrase needed; otherwise the key is opened (passphrase asked).
                var pub = path.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? path : path + ".pub";
                if (File.Exists(pub) && SshKeyGen.FingerprintOfLine(File.ReadAllText(pub)) is not null)
                {
                    ShowKey(pub == path ? path[..^4] : path, File.ReadAllText(pub).Trim());
                    return;
                }

                string? pass = null;
                while (true)
                {
                    try
                    {
                        ShowKey(path, SshKeyGen.PublicKeyOf(path, pass, comment.Text ?? ""));
                        return;
                    }
                    catch (Exception ex) when (ex is Renci.SshNet.Common.SshPassPhraseNullOrEmptyException or Renci.SshNet.Common.SshException && pass is null)
                    {
                        pass = await Dialog.PasswordAsync(dlg, Loc.T("Keys.Title"), Loc.F("Keys.PassphraseFor", Path.GetFileName(path)), confirm: false, minLength: 1, icon: "key-round");
                        if (pass is null) return;
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Renci.SshNet.Common.SshException or InvalidOperationException or NotSupportedException)
            {
                await Dialog.InfoAsync(dlg, Loc.T("Keys.Title"), Loc.F("Keys.NotAKey", ex.Message), "triangle-alert");
            }
        };

        copy.Click += async (_, _) =>
        {
            if (publicLine is not null && dlg.Clipboard is { } cb) await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, publicLine);
        };

        install.Click += async (_, _) =>
        {
            if (publicLine is null || server.SelectedIndex < 0) return;
            var entry = candidates[server.SelectedIndex];
            install.IsEnabled = false;
            status.IsVisible = true;
            statusText.Text = Loc.F("Keys.Installing", entry.Name);
            try
            {
                var p = await ConnectionBuilder.BuildAsync(entry, dlg, vm.Actions);
                if (p is null) return;
                var error = await SshKeyGen.DeployAsync(p, new SessionPrompts(() => dlg), AppHost.KnownHosts, publicLine, CancellationToken.None);
                if (error is not null)
                {
                    statusText.Text = Loc.F("Keys.InstallFailed", error);
                    return;
                }

                AppLog.Write($"ssh key installed on entry {entry.Id}: {SshKeyGen.FingerprintOfLine(publicLine)}");
                statusText.Text = Loc.F("Keys.Installed", entry.Name);
                if (useKey.IsChecked == true && keyPath is not null && !keyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase))
                {
                    var copyOf = entry.Clone();
                    copyOf.SetSetting(SettingKeys.SshKeyFile, keyPath);
                    AppHost.Workspace.Save(copyOf);
                    statusText.Text += " " + Loc.T("Keys.Assigned");
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                statusText.Text = Loc.F("Keys.InstallFailed", Nexode.Protocols.NetErrors.Describe(ex));
            }
            catch (OperationCanceledException)
            {
                status.IsVisible = false;
            }
            finally
            {
                install.IsEnabled = true;
            }
        };

        await dlg.ShowDialog<object?>(owner);
        return keyPath is not null && !keyPath.EndsWith(".pub", StringComparison.OrdinalIgnoreCase) ? keyPath : null;
    }

    private static Control Row(string labelKey, string icon, Control control) =>
        new SettingRow { Label = Loc.T(labelKey), Icon = icon, Content = control };

    /// <summary>Private key file readable only by the user (0600 on Linux/macOS; the user's profile ACLs on Windows).</summary>
    private static void WritePrivate(string path, string text)
    {
        if (OperatingSystem.IsWindows())
        {
            File.WriteAllText(path, text);
            return;
        }

        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write, UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite };
        using (var stream = new FileStream(path, options))
        using (var writer = new StreamWriter(stream))
        {
            writer.Write(text);
        }

        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
}
