using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.App.Views;
using Nexode.App.ViewModels.Sessions;
using Nexode.Core.Files;
using Nexode.Core.Import;
using Nexode.Core.Model;
using Nexode.Platform;

namespace Nexode.App.ViewModels;

public enum CopyField
{
    Name,
    Host,
    Url,
    Username,
    Password,
    Domain,
    DomainUser,
    Upn,
    Command
}

/// <summary>Entries copied/cut inside Nexode (not the system clipboard).</summary>
public static class EntryClipboard
{
    public static List<Guid> Ids { get; } = [];
    public static bool IsCut { get; set; }
    public static bool HasContent => Ids.Count > 0;
}

/// <summary>User actions on entries that need dialogs of one window.</summary>
public sealed class EntryActions(MainWindowViewModel owner)
{
    private static Workspace Ws => AppHost.Workspace;
    private MainWindow? Window => owner.Window;

    // ------------------------------------------------------------------ open

    public void Open(Entry entry, bool forceNewTab = false)
    {
        if (entry.IsContainer)
        {
            ShowDetails(entry);
            return;
        }

        if (FeatureOff(entry)) return;

        if (entry.Kind == EntryKind.Web)
        {
            OpenWeb(entry);
            return;
        }

        if (entry.Kind is EntryKind.Command)
        {
            _ = RunCommandAsync(entry);
            return;
        }

        if (entry.Kind is EntryKind.Database or EntryKind.Credential or EntryKind.Note)
        {
            Ws.MarkUsed(entry.Id);
            ShowDetails(entry, forceNewTab);
            return;
        }

        _ = ConnectAsync(entry, null);
    }

    /// <summary>
    /// The entry belongs to a switched-off feature (Settings → Features): a toast with a link to the switch instead of
    /// opening it. The entry itself is untouched.
    /// </summary>
    public bool FeatureOff(Entry entry)
    {
        if (Features.KindEnabled(entry.Kind)) return false;
        owner.ShowToast(Loc.F("Feature.OffToast", Loc.Kind(entry.Kind)));
        return true;
    }

    /// <summary>Web panel: built-in browser tab (or own window), default browser when switched so or not available.</summary>
    public void OpenWeb(Entry entry, bool newWindow = false)
    {
        if (FeatureOff(entry)) return;
        var mode = WebPanels.Mode(entry);
        if (mode == WebMode.External)
        {
            OpenInBrowser(entry);
            return;
        }

        _ = OpenWebAsync(entry, mode, newWindow);
    }

    private async Task OpenWebAsync(Entry entry, WebMode mode, bool newWindow)
    {
        if (!await ConfirmProductionAsync(entry)) return;
        if (AppHost.Settings.OpenBehavior == OpenBehavior.FocusExisting && !newWindow
            && owner.Tabs.Items.OfType<WebTabViewModel>().FirstOrDefault(t => t.EntryId == entry.Id && !t.IsEnded) is { } existing)
        {
            owner.Tabs.Selected = existing;
            return;
        }

        var tab = new WebTabViewModel(entry.Id, mode);
        if (newWindow) AppHost.Windows.Open(tab);
        else owner.Tabs.Add(tab);
    }

    /// <summary>Opens the address of a web panel (or any entry with a URL) in the default browser of the system.</summary>
    public void OpenInBrowser(Entry entry)
    {
        var url = Ws.Resolver.Resolve(entry).Url;
        if (url is not null && Shell.OpenWebUrl(url))
        {
            Ws.MarkUsed(entry.Id);
            ConnectionHistory.Begin(entry, url, HistoryResult.External);
            owner.ShowToast(Loc.F("Toast.OpenedBrowser", entry.Name));
        }
        else
        {
            owner.ShowToast(Loc.T("Toast.InvalidUrl"), true);
        }
    }

    /// <summary>Session tab for an entry; null when the kind does not open embedded here.</summary>
    public static SessionTabViewModel? CreateSession(Entry entry, IReadOnlyDictionary<string, string>? overrides = null) => entry.Kind switch
    {
        EntryKind.Ssh or EntryKind.Telnet or EntryKind.Serial or EntryKind.PowerShell => new TerminalTabViewModel(entry.Id, overrides),
        EntryKind.Sftp or EntryKind.Ftp or EntryKind.Scp or EntryKind.WebDav or EntryKind.S3 => new FileBrowserTabViewModel(entry.Id, overrides),
        EntryKind.Vnc => new VncTabViewModel(entry.Id, overrides),
        EntryKind.Rdp when OperatingSystem.IsWindows() => new RdpTabViewModel(entry.Id, overrides),
        _ => null
    };

    public async Task ConnectAsync(Entry entry, IReadOnlyDictionary<string, string>? overrides, bool newWindow = false, bool confirmed = false)
    {
        if (FeatureOff(entry)) return;
        if (!confirmed && !await ConfirmProductionAsync(entry)) return;
        // All monitors and RemoteApp need the system client (the embedded control shows one desktop per tab).
        if (entry.Kind == EntryKind.Rdp && (!OperatingSystem.IsWindows() || entry.Setting(SettingKeys.RdpSpan) == "1" || !string.IsNullOrEmpty(entry.Setting(SettingKeys.RdpRemoteApp))))
        {
            await OpenExternalAsync(entry, overrides, confirmed: true);
            return;
        }

        var tab = CreateSession(entry, overrides);
        if (tab is null)
        {
            ShowDetails(entry);
            return;
        }

        if (entry.Kind == EntryKind.Rdp && entry.Setting(SettingKeys.RdpFullscreen) == "1")
        {
            AppHost.Windows.OpenFullscreen(tab, int.TryParse(entry.Setting(SettingKeys.RdpMonitor), out var monitor) ? monitor : 0);
            return;
        }

        if (newWindow) AppHost.Windows.Open(tab);
        else owner.Tabs.Add(tab);
    }

    private async Task<bool> ConfirmProductionAsync(Entry entry)
    {
        if (!AppHost.Settings.ConfirmConnect || Window is null) return true;
        var env = Envs.Name(Ws.Resolver.Resolve(entry).Environment);
        return await Dialog.ConfirmAsync(Window, Loc.T("Session.ConfirmTitle"),
            env is null ? Loc.F("Session.ConfirmText", entry.Name) : Loc.F("Session.ConfirmTextEnv", entry.Name, env), Loc.T("Session.Connect"), icon: "plug-zap");
    }

    public Task OpenExternalAsync(Entry entry) => OpenExternalAsync(entry, null, false);

    public async Task OpenExternalAsync(Entry entry, IReadOnlyDictionary<string, string>? overrides, bool confirmed)
    {
        if (FeatureOff(entry)) return;
        if (entry.Kind == EntryKind.Web)
        {
            OpenInBrowser(entry);
            return;
        }

        if (Window is null || (!confirmed && !await ConfirmProductionAsync(entry))) return;
        try
        {
            var p = await ConnectionBuilder.BuildAsync(entry, Window, this, overrides);
            if (p is null) return;
            var (tp, tunnel) = await Tunnels.ApplyAsync(entry, p, Window, this, new SessionPrompts(() => Window), CancellationToken.None);
            if (tunnel is not null) Tunnels.KeepUntilExit(tunnel);
            var error = await ExternalLauncher.LaunchAsync(entry, tp);
            if (error is null)
            {
                Ws.MarkUsed(entry.Id);
                ConnectionHistory.Begin(entry, $"{tp.IdentityHost}:{tp.IdentityPort}", HistoryResult.External);
                owner.ShowToast(Loc.F("Toast.OpenedExternal", entry.Name));
            }
            else
            {
                owner.ShowToast(error, true);
            }
        }
        catch (InvalidOperationException ex)
        {
            owner.ShowToast(ex.Message, true);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (ex is Renci.SshNet.Common.SshException or System.Net.Sockets.SocketException or IOException)
        {
            owner.ShowToast(Nexode.Protocols.NetErrors.Describe(ex), true);
        }
    }

    /// <summary>One-off parameters (user, port, resolution …) without changing the entry.</summary>
    public async Task OpenWithParametersAsync(Entry entry)
    {
        if (Window is null) return;
        var result = await OpenParametersDialog.ShowAsync(Window, entry);
        if (result is null) return;
        if (result.Value.External) await OpenExternalAsync(entry, result.Value.Overrides, false);
        else await ConnectAsync(entry, result.Value.Overrides);
    }

    /// <summary>SFTP on the host of an SSH entry (same credentials).</summary>
    public void OpenSftpFor(Entry? ssh)
    {
        if (ssh is null) return;
        owner.Tabs.Add(new FileBrowserTabViewModel(ssh.Id, new Dictionary<string, string> { ["session.kind"] = "sftp" }));
    }

    private async Task RunCommandAsync(Entry entry)
    {
        if (Window is null) return;
        if (entry.NeedsReview)
        {
            owner.ShowToast(Loc.T("Toast.CommandNeedsReview"), true);
            ShowDetails(entry);
            return;
        }

        var line = entry.Setting(SettingKeys.CommandLine);
        if (string.IsNullOrWhiteSpace(line))
        {
            ShowDetails(entry);
            return;
        }

        if (!await Dialog.ConfirmAsync(Window, Loc.T("Command.RunTitle"), Loc.F("Command.RunText", line + " " + entry.Setting(SettingKeys.CommandArgs)), Loc.T("Command.Run"), icon: "square-chevron-right"))
        {
            return;
        }

        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo(line) { UseShellExecute = true };
            if (entry.Setting(SettingKeys.CommandArgs) is { } args) psi.Arguments = args;
            System.Diagnostics.Process.Start(psi);
            Ws.MarkUsed(entry.Id);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            owner.ShowToast(ex.Message, true);
        }
    }

    // ------------------------------------------------------------------ data sources

    public async Task TransferAsync(IReadOnlyList<Guid> ids, DataSourceInfo target, bool move)
    {
        if (Window is null || ids.Count == 0) return;
        if (!await EnsureUnlockedAsync()) return;
        var names = ids.Select(id => Ws.Tree.Get(id)?.Name).OfType<string>().ToList();
        var text = Loc.F(move ? "Transfer.MoveConfirm" : "Transfer.CopyConfirm", names.Count == 1 ? names[0] : Loc.F("Transfer.Entries", names.Count), target.Name);
        if (!await Dialog.ConfirmAsync(Window, Loc.T(move ? "Transfer.MoveTitle" : "Transfer.CopyTitle"), text, Loc.T(move ? "Transfer.Move" : "Transfer.Copy"), icon: "arrow-right-left"))
        {
            return;
        }

        var opened = await DataSources.OpenSecondaryAsync(Window, target);
        if (opened is not { } t)
        {
            owner.ShowToast(Loc.F("Transfer.OpenFailed", target.Name), true);
            return;
        }

        try
        {
            var count = Ws.TransferTo(t.Store, t.Vault, ids, move);
            owner.ShowToast(Loc.F(move ? "Toast.Moved" : "Toast.CopiedTo", count, target.Name));
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or Microsoft.Data.Sqlite.SqliteException)
        {
            owner.ShowToast(ex.Message, true);
        }
        finally
        {
            t.Vault.Dispose();
            t.Store.Dispose();
        }
    }

    public void ShowDetails(Entry entry, bool forceNewTab = false)
    {
        var behavior = AppHost.Settings.OpenBehavior;
        owner.Tabs.OpenOrFocus("entry:" + entry.Id, () => new EntryTabViewModel(entry.Id), forceNewTab || behavior == OpenBehavior.NewTab);
    }

    public void OpenInNewWindow(Entry entry)
    {
        if (entry.Kind == EntryKind.Web)
        {
            OpenWeb(entry, newWindow: true);
            return;
        }

        if (EntryKinds.IsConnection(entry.Kind) && entry.Kind is not (EntryKind.Web or EntryKind.Command or EntryKind.Database)) _ = ConnectAsync(entry, null, newWindow: true);
        else AppHost.Windows.Open(new EntryTabViewModel(entry.Id));
    }

    // ------------------------------------------------------------------ create / edit

    /// <summary>Changes the chosen fields of several entries at once (one undo step).</summary>
    public async Task BulkEditAsync(IReadOnlyList<Guid> ids)
    {
        if (Window is null || ids.Count == 0 || Ws.IsReadOnly) return;
        var entries = ids.Select(id => Ws.Get(id)).OfType<Entry>().ToList();
        var change = await BulkEditDialog.ShowAsync(Window, entries);
        if (change is null) return;
        Ws.Modify(ids, change);
        owner.ShowToast(Loc.F("Toast.BulkEdited", entries.Count));
    }

    public async Task NewEntryAsync(EntryKind kind, Guid? parentId, Entry? template = null)
    {
        if (Window is null) return;
        var entry = template ?? Ws.NewEntry(kind, parentId);
        if (template is not null)
        {
            var parent = Ws.Tree.Get(parentId);
            entry.ParentId = parent?.IsContainer == true ? parent.Id : parent?.ParentId;
        }

        if (kind == EntryKind.Folder && template is null)
        {
            var name = await Dialog.InputAsync(Window, Loc.T("New.Folder"), Loc.T("Field.Name"), Loc.T("New.FolderDefault"), "folder-plus");
            if (name is null) return;
            entry.Name = name;
            Ws.Save(entry);
            owner.Window?.SelectAndReveal(entry.Id);
            return;
        }

        var editor = new EntryEditorWindow(entry, isNew: true);
        if (await editor.ShowDialog<bool>(Window))
        {
            owner.Window?.SelectAndReveal(entry.Id);
        }
    }

    /// <summary>New entry with the options of a template (opens the editor; nothing is saved before OK).</summary>
    public Task NewFromTemplateAsync(EntryTemplate template, Guid? parentId)
    {
        var parent = Ws.Tree.Get(parentId);
        var entry = template.Create(parent?.IsContainer == true ? parent.Id : parent?.ParentId);
        return NewEntryAsync(entry.Kind, parentId, entry);
    }

    /// <summary>Saves the options of an entry as a template of the active data source.</summary>
    public async Task SaveAsTemplateAsync(Entry entry)
    {
        if (Window is null) return;
        var name = await Dialog.InputAsync(Window, Loc.T("Template.SaveTitle"), Loc.T("Template.Name"), entry.Name, "layout-template");
        if (string.IsNullOrWhiteSpace(name)) return;
        var template = EntryTemplate.From(entry, name.Trim());
        template.Order = Ws.Templates.Count;
        Ws.SaveTemplates([.. Ws.Templates, template]);
        owner.ShowToast(Loc.F("Template.Saved", template.Name));
    }

    public async Task EditAsync(Entry entry)
    {
        if (Window is null) return;
        var copy = entry.Clone();
        await new EntryEditorWindow(copy, isNew: false).ShowDialog<bool>(Window);
    }

    public async Task RenameAsync(Entry entry)
    {
        if (Window is null) return;
        var name = await Dialog.InputAsync(Window, Loc.T("Menu.Rename"), Loc.T("Field.Name"), entry.Name);
        if (name is null || name == entry.Name) return;
        Ws.Modify([entry.Id], e => e.Name = name);
    }

    public async Task DeleteAsync(IReadOnlyList<Guid> ids)
    {
        if (Window is null || ids.Count == 0) return;
        var entries = ids.Select(id => Ws.Tree.Get(id)).OfType<Entry>().ToList();
        if (entries.Count == 0) return;
        var children = entries.Sum(e => Ws.Tree.Descendants(e.Id).Count());
        var message = entries.Count == 1
            ? Loc.F("Confirm.DeleteOne", entries[0].Name)
            : Loc.F("Confirm.DeleteMany", entries.Count);
        if (children > 0) message += "\n\n" + Loc.F("Confirm.DeleteChildren", children);
        message += "\n\n" + Loc.T("Confirm.DeleteTrash");
        if (!await Dialog.ConfirmAsync(Window, Loc.T("Menu.Delete"), message, Loc.T("Menu.Delete"), danger: true)) return;
        Ws.Delete(ids);
        owner.ShowToast(Loc.T("Toast.Deleted"));
    }

    public void Duplicate(IReadOnlyList<Guid> ids)
    {
        var first = Ws.Tree.Get(ids.FirstOrDefault());
        if (first is null) return;
        var copies = Ws.Copy(ids, new DropTarget(first.ParentId, first.Id, false), markAsCopy: true);
        if (copies.FirstOrDefault() is { } c) owner.Window?.SelectAndReveal(c.Id);
        if (Ws.IsLocked && ids.Any(id => Ws.HasSecret(id))) owner.ShowToast(Loc.T("Toast.CopyNoSecrets"));
    }

    public void CopyEntries(IReadOnlyList<Guid> ids, bool cut)
    {
        EntryClipboard.Ids.Clear();
        EntryClipboard.Ids.AddRange(ids);
        EntryClipboard.IsCut = cut;
        var hosts = ids.Select(id => Ws.Tree.Get(id)).OfType<Entry>()
            .Select(e => Ws.Resolver.Resolve(e)).Select(e => e.Host ?? e.Url).Where(h => h is not null);
        var text = string.Join(Environment.NewLine, hosts);
        if (text.Length > 0) _ = AppHost.Clipboard.CopyAsync(Window, text, false);
        owner.ShowToast(Loc.F(cut ? "Toast.Cut" : "Toast.Copied", ids.Count));
    }

    public async Task PasteAsync(Guid? targetId)
    {
        var target = Ws.Tree.Get(targetId);
        var drop = target is null ? DropTarget.Into(null)
            : target.IsContainer ? DropTarget.Into(target.Id) : new DropTarget(target.ParentId, target.Id, false);

        if (EntryClipboard.HasContent)
        {
            var ids = EntryClipboard.Ids.Where(id => Ws.Tree.Get(id) is not null).ToList();
            if (EntryClipboard.IsCut)
            {
                if (!Ws.CanDrop(ids, drop))
                {
                    owner.ShowToast(Loc.T("Toast.CannotMoveInside"), true);
                    return;
                }

                Ws.Move(ids, drop);
                EntryClipboard.Ids.Clear();
            }
            else
            {
                Ws.Copy(ids, drop, markAsCopy: drop.Parent == Ws.Tree.Get(ids.FirstOrDefault())?.ParentId);
            }

            return;
        }

        // System clipboard: URLs / host lists become new entries.
        if (Window?.Clipboard is { } cb && await cb.TryGetTextAsync() is { Length: > 0 } text)
        {
            await CreateFromTextAsync(text, drop.Parent ?? target?.ParentId);
        }
    }

    /// <summary>"ssh://root@host", "https://panel" or several lines become entries.</summary>
    public async Task<int> CreateFromTextAsync(string text, Guid? parentId)
    {
        var lines = text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(500).ToList();
        if (lines.Count == 1 && EntryKinds.FromUri(lines[0]) is { } single)
        {
            await NewEntryAsync(single.Kind, parentId, single);
            return 1;
        }

        var created = 0;
        foreach (var line in lines)
        {
            var e = EntryKinds.FromUri(line);
            if (e is null && IsHostName(line)) e = new Entry { Kind = EntryKind.Host, Name = line, Host = line };
            if (e is null) continue;
            e.ParentId = parentId;
            Ws.Save(e);
            created++;
        }

        owner.ShowToast(created > 0 ? Loc.F("Toast.CreatedFromText", created) : Loc.T("Toast.NothingToPaste"), created == 0);
        return created;
    }

    private static bool IsHostName(string s) =>
        s.Length is > 0 and < 254 && !s.Contains(' ') && Uri.CheckHostName(s) != UriHostNameType.Unknown;

    // ------------------------------------------------------------------ attributes

    public void ToggleFavorite(IReadOnlyList<Guid> ids)
    {
        var entries = ids.Select(id => Ws.Tree.Get(id)).OfType<Entry>().ToList();
        if (entries.Count == 0) return;
        var on = !entries.All(e => e.IsFavorite);
        Ws.ModifyPersonal(ids, e => e.IsFavorite = on);
    }

    public void SetColor(IReadOnlyList<Guid> ids, double? hue) => Ws.Modify(ids, e => e.ColorHue = hue);

    public void SetEnvironment(IReadOnlyList<Guid> ids, string? env) => Ws.Modify(ids, e => e.EnvironmentId = env);

    public void SetCategory(IReadOnlyList<Guid> ids, string? category) => Ws.Modify(ids, e => e.CategoryId = category);

    public void SetSort(Entry folder, SortMode? mode) => Ws.Modify([folder.Id], e => e.SortMode = mode);

    public async Task PickIconAsync(IReadOnlyList<Guid> ids)
    {
        if (Window is null || ids.Count == 0) return;
        var current = Ws.Tree.Get(ids[0])?.Icon;
        var picker = new IconPickerWindow(current);
        var result = await picker.ShowDialog<string?>(Window);
        if (result is null) return;
        Ws.Modify(ids, e => e.Icon = result.Length == 0 ? null : result);
    }

    public void ApproveReview(IReadOnlyList<Guid> ids) => Ws.Modify(ids, e => e.NeedsReview = false);

    // ------------------------------------------------------------------ clipboard fields

    public async Task CopyFieldAsync(Entry entry, CopyField field)
    {
        var eff = Ws.Resolver.Resolve(entry);
        string? value = field switch
        {
            CopyField.Name => entry.Name,
            CopyField.Host => eff.Host,
            CopyField.Url => eff.Url,
            CopyField.Username => eff.Username,
            CopyField.Domain => eff.Domain,
            CopyField.DomainUser => eff.UserWithDomain,
            CopyField.Upn => eff.Upn,
            CopyField.Command => CommandLine(entry, eff),
            _ => null
        };

        if (field == CopyField.Password)
        {
            if (eff.SecretOwner is not { } secretOwner || !Ws.HasSecret(secretOwner))
            {
                owner.ShowToast(Loc.T("Toast.NoPassword"), true);
                return;
            }

            if (!await EnsureUnlockedAsync()) return;
            value = Ws.GetSecret(secretOwner, Workspace.PasswordField);
        }

        if (string.IsNullOrEmpty(value))
        {
            owner.ShowToast(Loc.T("Toast.NothingToCopy"), true);
            return;
        }

        await AppHost.Clipboard.CopyAsync(Window, value, field == CopyField.Password);
        owner.ShowToast(field == CopyField.Password
            ? Loc.F("Toast.PasswordCopied", AppHost.Settings.ClipboardClearSeconds)
            : Loc.F("Toast.FieldCopied", Loc.T("Copy." + field)));
    }

    public static string? CommandLine(Entry entry, Core.Tree.Effective eff)
    {
        if (eff.Host is null) return eff.Url;
        var port = eff.Port is { } p && p != EntryKinds.DefaultPort(entry.Kind) ? p : (int?)null;
        var user = string.IsNullOrEmpty(eff.Username) ? "" : eff.Username + "@";
        return entry.Kind switch
        {
            EntryKind.Ssh => $"ssh {user}{eff.Host}" + (port is null ? "" : $" -p {port}"),
            EntryKind.Sftp => $"sftp " + (port is null ? "" : $"-P {port} ") + $"{user}{eff.Host}",
            EntryKind.Rdp => $"mstsc /v:{eff.Host}" + (port is null ? "" : $":{port}"),
            EntryKind.Telnet => $"telnet {eff.Host}" + (port is null ? "" : $" {port}"),
            EntryKind.PowerShell => $"Enter-PSSession -ComputerName {eff.Host}" + (port is null ? "" : $" -Port {port}") + (string.IsNullOrEmpty(eff.Username) ? "" : $" -Credential {eff.Username}"),
            _ => $"{EntryKinds.Scheme(entry.Kind)}://{user}{eff.Host}" + (port is null ? "" : $":{port}")
        };
    }

    // ------------------------------------------------------------------ vault

    public async Task<bool> EnsureUnlockedAsync()
    {
        if (!Ws.IsLocked) return true;
        if (Ws.OpenError is not null)
        {
            owner.ShowToast(Loc.T("Vault.KeyUnavailable"), true);
            return false;
        }

        return await UnlockAsync();
    }

    public async Task<bool> UnlockAsync()
    {
        if (Window is null) return false;
        var pw = await Dialog.PasswordCheckAsync(Window, Loc.T("Vault.UnlockTitle"), Loc.F("Vault.UnlockText", Ws.Info?.Name), p => Ws.TryUnlockAsync(p));
        return pw is not null && !Ws.IsLocked;
    }

    /// <summary>Asks for the master password again before a password is shown (setting "re-authenticate to reveal").</summary>
    public async Task<bool> ReauthenticateAsync()
    {
        if (!Ws.HasPassword || !AppHost.Settings.ReauthToReveal) return await EnsureUnlockedAsync();
        if (Window is null) return false;
        if (Ws.IsLocked) return await UnlockAsync();
        return await Dialog.PasswordCheckAsync(Window, Loc.T("Vault.ReauthTitle"), Loc.T("Vault.ReauthText"), p => Ws.VerifyPasswordAsync(p), "eye") is not null;
    }

    // ------------------------------------------------------------------ import / export

    public async Task ImportAsync(string? path)
    {
        if (Window is null) return;
        if (path is null)
        {
            var files = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = Loc.T("Import.Title"),
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("Nexode") { Patterns = ["*.nexode"] },
                    new FilePickerFileType(Loc.T("Import.ForeignFiles")) { Patterns = ["*.xml", "*.rdg", "*.rdp", "config"] }
                ]
            });
            path = files.FirstOrDefault()?.TryGetLocalPath();
            if (path is null) return;
        }

        if (!path.EndsWith(".nexode", StringComparison.OrdinalIgnoreCase) && ForeignImporters.Detect(path) is { } format)
        {
            await ImportForeignFileAsync(path, format);
            return;
        }

        NexodeFile file;
        try
        {
            file = NexodeFile.Load(path);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException or UnauthorizedAccessException)
        {
            await Dialog.InfoAsync(Window, Loc.T("Import.Title"), Loc.F("Import.Failed", ex.Message), "circle-alert");
            return;
        }

        if (file.Kind == NexodeKinds.Connections)
        {
            await new ImportWindow(file, path, Window.SelectedEntryContainer()).ShowDialog<bool>(Window);
            return;
        }

        await SettingsTransfer.ImportAsync(Window, file, owner);
    }

    /// <summary>Import from other tools: choose the source (file formats, PuTTY sessions, ~/.ssh/config).</summary>
    public async Task ImportForeignAsync()
    {
        if (Window is null) return;
        var choice = await Dialog.PickAsync(Window, Loc.T("Import.Foreign"), "",
        [
            (Loc.T("Import.FromSshConfig"), "square-terminal", "~/.ssh/config"),
            (Loc.T("Import.FromPutty"), "terminal", Loc.T("Import.FromPuttyInfo")),
            (Loc.T("Import.FromWinScp"), "folder-lock", Loc.T("Import.FromWinScpInfo")),
            (Loc.T("Import.FromMobaXterm"), "monitor-cog", Loc.T("Import.FromMobaXtermInfo")),
            (Loc.T("Import.FromRemmina"), "screen-share", Loc.T("Import.FromRemminaInfo")),
            (Loc.T("Import.FromFile"), "file-down", Loc.T("Import.FromFileInfo"))
        ]);
        switch (choice)
        {
            case 0:
                if (File.Exists(PuttySessions.SshConfigPath)) await ImportForeignFileAsync(PuttySessions.SshConfigPath, ForeignFormat.SshConfig);
                else owner.ShowToast(Loc.T("Import.NoSshConfig"), true);
                break;
            case 1:
                var sessions = PuttySessions.Read();
                if (sessions.Count == 0) owner.ShowToast(Loc.T("Import.NoPutty"), true);
                else await ShowForeign(ForeignImporters.ReadPutty(sessions), "PuTTY");
                break;
            case 2:
                if (ToolSessions.WinScpRegistry() is { } reg && reg.Sessions.Count > 0) await ShowForeign(ForeignImporters.ReadWinScp(reg.Sessions, reg.MasterPassword), "WinSCP");
                else if (ToolSessions.WinScpIni() is { } ini) await ImportForeignFileAsync(ini, ForeignFormat.WinScp);
                else await PickForeignAsync("WinSCP.ini", ["*.ini"], ForeignFormat.WinScp);
                break;
            case 3:
                if (ToolSessions.MobaXtermIni() is { } moba) await ImportForeignFileAsync(moba, ForeignFormat.MobaXterm);
                else await PickForeignAsync("MobaXterm", ["*.ini", "*.mxtsessions"], ForeignFormat.MobaXterm);
                break;
            case 4:
                if (ToolSessions.Remmina() is { } rem)
                {
                    var files = Directory.GetFiles(rem.Folder, "*.remmina").Select(f => (f, File.ReadAllText(f))).ToList();
                    var pref = rem.Pref is { } pp ? await File.ReadAllTextAsync(pp) : null;
                    await ShowForeign(ForeignImporters.ReadRemmina(files, pref), "Remmina");
                }
                else
                {
                    await PickForeignAsync("Remmina", ["*.remmina"], ForeignFormat.Remmina);
                }

                break;
            case 5:
                await ImportAsync(null);
                break;
        }
    }

    private async Task PickForeignAsync(string title, string[] patterns, ForeignFormat format)
    {
        if (Window is null) return;
        var files = await Window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = format == ForeignFormat.Remmina,
            FileTypeFilter = [new FilePickerFileType(title) { Patterns = patterns }, FilePickerFileTypes.All]
        });
        if (files.Count == 0) return;
        if (format == ForeignFormat.Remmina)
        {
            var list = new List<(string, string)>();
            foreach (var f in files)
            {
                if (f.TryGetLocalPath() is { } lp) list.Add((lp, await File.ReadAllTextAsync(lp)));
            }

            var prefPath = list.Count > 0 ? Path.Combine(Path.GetDirectoryName(list[0].Item1)!, "remmina.pref") : null;
            var pref = prefPath is not null && File.Exists(prefPath) ? await File.ReadAllTextAsync(prefPath) : ToolSessions.Remmina()?.Pref is { } d ? await File.ReadAllTextAsync(d) : null;
            await ShowForeign(ForeignImporters.ReadRemmina(list, pref), "Remmina");
            return;
        }

        if (files[0].TryGetLocalPath() is { } path) await ImportForeignFileAsync(path, format);
    }

    private async Task ImportForeignFileAsync(string path, ForeignFormat format)
    {
        if (Window is null) return;
        try
        {
            var text = await File.ReadAllTextAsync(path);
            Func<byte[], byte[]?>? dpapi = OperatingSystem.IsWindows() ? Dpapi.UnprotectUser : null;
            ForeignImport result;
            switch (format)
            {
                case ForeignFormat.MRemoteNg:
                    try
                    {
                        result = ForeignImporters.ReadMRemoteNg(text);
                    }
                    catch (ForeignImporters.PasswordRequiredException)
                    {
                        result = null!;
                        string? pw = null;
                        while (result is null)
                        {
                            pw = await Dialog.PasswordAsync(Window, "mRemoteNG", Loc.T("Import.MRemotePassword"), confirm: false, minLength: 1, icon: "key-round");
                            if (pw is null) return;
                            try
                            {
                                result = ForeignImporters.ReadMRemoteNg(text, pw);
                            }
                            catch (ForeignImporters.PasswordRequiredException)
                            {
                                owner.ShowToast(Loc.T("PasswordWrong"), true);
                            }
                        }
                    }

                    break;
                case ForeignFormat.RdcMan:
                    result = ForeignImporters.ReadRdcMan(text, dpapi);
                    break;
                case ForeignFormat.RdpFile:
                    result = ForeignImporters.ReadRdpFile(text, Path.GetFileName(path), dpapi);
                    break;
                case ForeignFormat.SshConfig:
                    result = ForeignImporters.ReadSshConfig(text);
                    break;
                case ForeignFormat.WinScp:
                    result = ForeignImporters.ReadWinScpIni(text);
                    break;
                case ForeignFormat.MobaXterm:
                    result = ForeignImporters.ReadMobaXterm(text);
                    break;
                case ForeignFormat.Remmina:
                    var prefPath = Path.Combine(Path.GetDirectoryName(path)!, "remmina.pref");
                    var pref = File.Exists(prefPath) ? await File.ReadAllTextAsync(prefPath) : ToolSessions.Remmina()?.Pref is { } d ? await File.ReadAllTextAsync(d) : null;
                    result = ForeignImporters.ReadRemmina([(path, text)], pref);
                    break;
                default:
                    return;
            }

            await ShowForeign(result, Path.GetFileName(path));
        }
        catch (Exception ex) when (ex is IOException or System.Xml.XmlException or InvalidDataException or UnauthorizedAccessException)
        {
            await Dialog.InfoAsync(Window, Loc.T("Import.Title"), Loc.F("Import.Failed", ex.Message), "circle-alert");
        }
    }

    private async Task ShowForeign(ForeignImport result, string label)
    {
        if (Window is null) return;
        if (result.Entries.Count == 0)
        {
            owner.ShowToast(Loc.T("Import.NothingFound"), true);
            return;
        }

        var notes = new List<string>(result.Warnings);
        if (result.UndecryptedSecrets > 0) notes.Insert(0, Loc.F("Import.Undecrypted", result.UndecryptedSecrets));
        await new ImportWindow(result.ToPayload(), label, Loc.T("Import.Format." + result.Format), notes, Window.SelectedEntryContainer()).ShowDialog<bool>(Window);
    }

    // ------------------------------------------------------------------ connection profiles

    public async Task EditProfileAsync(ConnectionProfile? profile, IReadOnlyList<Guid>? preselected = null)
    {
        if (Window is null) return;
        var edited = await ProfileEditDialog.ShowAsync(Window, profile, preselected);
        if (edited is null) return;
        var list = Ws.Profiles.ToList();
        var index = list.FindIndex(x => x.Id == edited.Id);
        if (index >= 0)
        {
            list[index] = edited;
        }
        else
        {
            edited.Order = list.Count == 0 ? 0 : list.Max(x => x.Order) + 1;
            list.Add(edited);
        }

        Ws.SaveProfiles(list);
        owner.ShowToast(Loc.F("Toast.ProfileSaved", edited.Name));
    }

    public async Task DeleteProfileAsync(ConnectionProfile profile)
    {
        if (Window is null) return;
        if (!await Dialog.ConfirmAsync(Window, Loc.T("Profile.Delete"), Loc.F("Profile.DeleteText", profile.Name), Loc.T("Menu.Delete"), danger: true)) return;
        Ws.SaveProfiles(Ws.Profiles.Where(x => x.Id != profile.Id));
    }

    /// <summary>Adds entries to a profile chosen from a list (or a new one).</summary>
    public async Task AddToProfileAsync(IReadOnlyList<Guid> ids)
    {
        if (Window is null) return;
        var connections = ids.Where(id => Ws.Get(id) is { } e && EntryKinds.IsConnection(e.Kind)).ToList();
        if (connections.Count == 0) return;
        var profiles = Ws.Profiles;
        var options = profiles.Select(x => (x.Name, string.IsNullOrEmpty(x.Icon) ? "layers" : x.Icon, (string?)new ProfileItem(x).Summary)).ToList();
        options.Add((Loc.T("Profile.New"), "plus", null));
        var pick = await Dialog.PickAsync(Window, Loc.T("Menu.AddToProfile"), Loc.T("Profile.PickText"), options);
        if (pick is not { } index) return;
        if (index == profiles.Count)
        {
            await EditProfileAsync(null, connections);
            return;
        }

        var target = profiles[index];
        var updated = Ws.Profiles.ToList();
        var copy = updated.First(x => x.Id == target.Id);
        foreach (var id in connections.Where(id => !copy.Entries.Contains(id))) copy.Entries.Add(id);
        Ws.SaveProfiles(updated);
        owner.ShowToast(Loc.F("Toast.AddedToProfile", connections.Count, target.Name));
    }

    /// <summary>Opens all connections of a profile as tabs, in a new window or as split view.</summary>
    public async Task OpenProfileAsync(ConnectionProfile profile)
    {
        if (Window is null) return;
        var entries = Ws.ProfileEntries(profile).Where(e => !e.IsContainer && Features.KindEnabled(e.Kind)).ToList();
        if (entries.Count == 0)
        {
            owner.ShowToast(Loc.T("Profile.Empty"), true);
            return;
        }

        if (AppHost.Settings.ConfirmConnect &&
            !await Dialog.ConfirmAsync(Window, Loc.T("Session.ConfirmTitle"), Loc.F("Profile.ConfirmText", profile.Name, entries.Count), Loc.T("Session.Connect"), icon: "circle-play"))
        {
            return;
        }

        var target = owner;
        if (profile.Layout == ProfileLayout.NewWindow)
        {
            var window = AppHost.Windows.Open();
            if (window.DataContext is MainWindowViewModel vm) target = vm;
        }

        var sessions = new List<SessionTabViewModel>();
        var others = new List<Entry>();
        foreach (var e in entries)
        {
            var embedded = !(e.Kind == EntryKind.Rdp && !OperatingSystem.IsWindows()) ? CreateSession(e) : null;
            if (embedded is not null) sessions.Add(embedded);
            else others.Add(e);
        }

        if (profile.Layout == ProfileLayout.Split && sessions.Count > 1 && Features.On(Features.Split))
        {
            var inSplit = sessions.Take(SplitTabViewModel.MaxPanes).ToList();
            var split = new SplitTabViewModel(inSplit, inSplit.Count == 4 ? SplitLayout.Grid : inSplit.Count == 3 ? SplitLayout.Main : SplitLayout.Columns);
            target.Tabs.Add(split);
            foreach (var extra in sessions.Skip(SplitTabViewModel.MaxPanes)) target.Tabs.Add(extra, select: false);
            await StartAllAsync(sessions, profile);
        }
        else
        {
            foreach (var s in sessions) target.Tabs.Add(s, select: s == sessions[0]);
            await StartAllAsync(sessions, profile);
        }

        foreach (var e in others) target.Actions.Open(e);
    }

    /// <summary>Connects sessions right away (not only when their tab is shown); RDP connects once its view exists.</summary>
    private static async Task StartAllAsync(IReadOnlyList<SessionTabViewModel> sessions, ConnectionProfile profile)
    {
        var tasks = new List<Task>();
        foreach (var s in sessions)
        {
            if (s is RdpTabViewModel || s.State != Sessions.SessionState.Idle) continue;
            var t = s.StartAsync();
            if (profile.Sequential)
            {
                await t;
                await Task.Delay(Math.Clamp(profile.DelayMs, 0, 5000));
            }
            else
            {
                tasks.Add(t);
            }
        }

        await Task.WhenAll(tasks);
    }

    /// <summary>Ends all sessions of the profile's entries in every window.</summary>
    public static void CloseProfileSessions(ConnectionProfile profile)
    {
        var ids = profile.Entries.ToHashSet();
        foreach (var vm in AppHost.Windows.ViewModels.ToList())
        {
            foreach (var tab in vm.Tabs.Items.ToList())
            {
                if (tab is SessionTabViewModel s && ids.Contains(s.EntryId))
                {
                    vm.Tabs.Close(s);
                }
                else if (tab is SplitTabViewModel split)
                {
                    foreach (var p in split.Panes.Where(p => ids.Contains(p.EntryId)).ToList()) split.Remove(p, close: true);
                }
            }
        }
    }

    // ------------------------------------------------------------------ split view

    /// <summary>Puts a session tab into a split view (an existing one of this window with room, else a new one).</summary>
    public void AddToSplit(SessionTabViewModel session, SplitTabViewModel? into = null)
    {
        var tabs = owner.Tabs;
        into ??= tabs.Items.OfType<SplitTabViewModel>().FirstOrDefault(sp => sp.CanAdd);
        if (into is null)
        {
            var index = tabs.Items.IndexOf(session);
            tabs.Detach(session);
            var split = new SplitTabViewModel([session]);
            tabs.Add(split, true, index);
            return;
        }

        if (!into.CanAdd) return;
        tabs.Detach(session);
        into.Add(session);
        tabs.Selected = into;
    }

    // ------------------------------------------------------------------ Wake-on-LAN

    /// <summary>Sends the magic packet for the entry (or the host it belongs to); optionally waits for the port and connects.</summary>
    public async Task WakeAsync(Entry entry, bool thenConnect)
    {
        var host = entry.Setting(SettingKeys.MacAddress) is not null ? entry : Ws.Tree.Ancestors(entry).FirstOrDefault(a => a.Setting(SettingKeys.MacAddress) is not null);
        var mac = Nexode.Protocols.WakeOnLan.ParseMac(host?.Setting(SettingKeys.MacAddress));
        if (host is null || mac is null)
        {
            owner.ShowToast(Loc.T("Wol.NoMac"), true);
            return;
        }

        try
        {
            var port = int.TryParse(host.Setting(SettingKeys.WolPort), out var wp) && wp is > 0 and < 65536 ? wp : 9;
            await Nexode.Protocols.WakeOnLan.SendAsync(mac, host.Setting(SettingKeys.WolBroadcast), port);
            owner.ShowToast(Loc.F("Wol.Sent", host.Name, Nexode.Protocols.WakeOnLan.FormatMac(mac)));
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or InvalidOperationException)
        {
            owner.ShowToast(Loc.F("Wol.Failed", ex.Message), true);
            return;
        }

        if (!thenConnect || !EntryKinds.IsConnection(entry.Kind)) return;
        var eff = Ws.Resolver.Resolve(entry);
        if (eff.Host is not { } address || eff.Port is not { } p)
        {
            Open(entry);
            return;
        }

        owner.ShowToast(Loc.F("Wol.Waiting", entry.Name));
        using var cts = new CancellationTokenSource();
        if (await Nexode.Protocols.WakeOnLan.WaitForPortAsync(address, p, TimeSpan.FromMinutes(3), cts.Token)) Open(entry);
        else owner.ShowToast(Loc.F("Wol.Timeout", entry.Name), true);
    }

    public async Task ExportAsync(IReadOnlyList<Guid> selected)
    {
        if (Window is null) return;
        await new ExportWindow(selected).ShowDialog<bool>(Window);
    }
}
