using System.Globalization;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Core.Tree;

namespace Nexode.App.ViewModels;

public sealed record Pill(string Text, string Class, string? Icon = null)
{
    /// <summary>Own colors (environments); null = from the class.</summary>
    public Avalonia.Media.IBrush? Brush { get; init; }
    public Avalonia.Media.IBrush? Background { get; init; }
}

public sealed record InfoRow(string Label, string Value, string? Icon = null, CopyField? Copy = null, string? Source = null, bool Mono = false)
{
    public bool HasCopy => Copy is not null;
    public bool HasSource => !string.IsNullOrEmpty(Source);
}

public sealed record ChildRow(Entry Entry, string Icon, IBrush? IconBrush, string Name, string? Detail, string KindText);

/// <summary>Details of one entry (connection, host, folder …) – the "dashboard" of an entry.</summary>
public sealed partial class EntryTabViewModel : TabViewModel
{
    public EntryTabViewModel(Guid entryId)
    {
        EntryId = entryId;
        Refresh();
    }

    public Guid EntryId { get; }
    public override string Key => "entry:" + EntryId;
    public override bool IsValid => AppHost.Workspace.Tree.Get(EntryId) is not null;
    public override TabViewModel Recreate() => new EntryTabViewModel(EntryId);
    public override void OnClosed() => CloseSysInfo();

    [ObservableProperty]
    private Entry? _entry;

    [ObservableProperty]
    private Effective? _effective;

    [ObservableProperty]
    private bool _passwordRevealed;

    [ObservableProperty]
    private string _passwordText = "••••••••";

    public string KindText => Entry is null ? "" : Loc.Kind(Entry.Kind);
    public string PathText => Entry is null ? "" : AppHost.Workspace.Tree.Path(Entry) is { Length: > 0 } p ? p : Loc.T("Root");
    public IReadOnlyList<Pill> Pills { get; private set; } = [];
    public IReadOnlyList<InfoRow> ConnectionRows { get; private set; } = [];
    public IReadOnlyList<InfoRow> CredentialRows { get; private set; } = [];
    public IReadOnlyList<InfoRow> InfoRows { get; private set; } = [];
    public IReadOnlyList<ChildRow> Children { get; private set; } = [];
    public bool IsContainer => Entry?.IsContainer == true;
    public bool HasChildren => Children.Count > 0;
    public bool HasConnectionRows => ConnectionRows.Count > 0;
    public bool IsWeb => Entry?.Kind == EntryKind.Web;
    public bool IsConnection => Entry is not null && EntryKinds.IsConnection(Entry.Kind) && Entry.Kind is not (EntryKind.Web or EntryKind.Command or EntryKind.Database);
    public bool CanConnect => IsWeb;
    public bool HasCredentials => CredentialRows.Count > 0;
    public bool HasPassword => Effective?.SecretOwner is { } o && AppHost.Workspace.HasSecret(o);
    public bool IsLocked => AppHost.Workspace.IsLocked;
    public bool HasNotes => !string.IsNullOrWhiteSpace(Entry?.Notes);
    public bool HasDescription => !string.IsNullOrWhiteSpace(Entry?.Description);
    public bool IsFavorite => Entry?.IsFavorite == true;
    public string FavoriteIcon => "heart";
    public bool NeedsReview => Entry?.NeedsReview == true;
    public string? CredentialSource { get; private set; }
    public bool HasCredentialSource => CredentialSource is not null;

    public override void Refresh()
    {
        var ws = AppHost.Workspace;
        var entry = ws.Tree.Get(EntryId);
        Entry = entry;
        if (entry is null) return;

        var eff = ws.Resolver.Resolve(entry);
        Effective = eff;
        Title = string.IsNullOrEmpty(entry.Name) ? Loc.T("Unnamed") : entry.Name;
        Icon = eff.Icon;
        IconBrush = eff.ColorHue is { } hue && (entry.Kind == EntryKind.Folder || entry.ColorHue is not null) ? AppHost.Theme.FolderBrush(hue) : null;
        SetColors(eff);

        var pills = new List<Pill> { new(Loc.Kind(entry.Kind), "accent", EntryKinds.Icon(entry.Kind)) };
        if (Envs.Get(eff.Environment) is { } env) pills.Add(new(Envs.Name(env), "", "flag") { Brush = Envs.Brush(env.Id), Background = Envs.Background(env.Id) });
        if (eff.Os != OsFamily.Unknown) pills.Add(new(Loc.T("Os." + eff.Os), "", EntryKinds.OsIcon(eff.Os)));
        if (entry.CategoryId is { } cat && ws.Categories.GetValueOrDefault(cat) is { } c) pills.Add(new(Loc.T("Cat." + c.Id) is var t && t != "Cat." + c.Id ? t : c.Name, "", c.Icon));
        foreach (var tag in entry.Tags) pills.Add(new("#" + tag, "", null));
        if (entry.NeedsReview) pills.Add(new(Loc.T("NeedsReview"), "warning", "triangle-alert"));
        Pills = pills;

        var conn = new List<InfoRow>();
        string? Src(Guid? from) => from is { } f && f != entry.Id && ws.Tree.Get(f) is { } src ? Loc.F("InheritedFrom", src.Name) : null;
        if (eff.Host is not null) conn.Add(new(Loc.T("Field.Host"), eff.Host, "server", CopyField.Host, Src(eff.HostFrom), true));
        if (entry.Setting(SettingKeys.IpAddress) is { } ip) conn.Add(new(Loc.T("Field.Ip"), ip, "network", null, null, true));
        if (eff.Port is { } port && !entry.IsContainer && entry.Kind != EntryKind.Web) conn.Add(new(Loc.T("Field.Port"), port.ToString(CultureInfo.InvariantCulture), "plug", null, null, true));
        if (eff.Url is not null) conn.Add(new(Loc.T("Field.Url"), eff.Url, "globe", CopyField.Url, null, true));
        if (entry.Setting(SettingKeys.MacAddress) is { } mac) conn.Add(new(Loc.T("Field.Mac"), mac, "ethernet-port", null, null, true));
        if (EntryActions.CommandLine(entry, eff) is { } cmd && IsConnection) conn.Add(new(Loc.T("Field.Command"), cmd, "square-terminal", CopyField.Command, null, true));
        foreach (var (key, value) in entry.Settings.Where(s => s.Key is not (SettingKeys.IpAddress or SettingKeys.MacAddress)))
        {
            conn.Add(new(Loc.T("Setting." + key), SettingText(key, value), "settings", null, null, false));
        }

        ConnectionRows = conn;

        var cred = new List<InfoRow>();
        if (EntryKinds.HasCredentials(entry.Kind))
        {
            switch (eff.CredentialMode)
            {
                case CredentialMode.Prompt:
                    cred.Add(new(Loc.T("Field.Credentials"), Loc.T("Cred.Prompt"), "key-round"));
                    break;
                case CredentialMode.None when eff.CredentialFrom is null || entry.CredentialMode == CredentialMode.None:
                    break;
                default:
                    if (eff.Username is { } user) cred.Add(new(Loc.T("Field.Username"), user, "user", CopyField.Username, null, true));
                    if (eff.Domain is { } dom) cred.Add(new(Loc.T("Field.Domain"), dom, "building-2", CopyField.Domain, null, true));
                    if (eff.UserWithDomain is { } ud && eff.Domain is not null) cred.Add(new(Loc.T("Field.DomainUser"), ud, "users", CopyField.DomainUser, null, true));
                    break;
            }
        }

        CredentialRows = cred;
        CredentialSource = eff.CredentialFrom is { } cf && cf != entry.Id && ws.Tree.Get(cf) is { } cs
            ? (cs.Kind == EntryKind.Credential ? Loc.F("Cred.FromProfile", cs.Name) : Loc.F("InheritedFrom", cs.Name))
            : null;

        var info = new List<InfoRow>
        {
            new(Loc.T("Field.Location"), PathText, "folder-tree"),
            new(Loc.T("Field.Created"), entry.CreatedUtc.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), "clock")
        };
        if (entry.LastUsedUtc is { } used) info.Add(new(Loc.T("Field.LastUsed"), used.ToLocalTime().ToString("g", CultureInfo.CurrentCulture), "history"));
        info.Add(new(Loc.T("Field.Id"), entry.Id.ToString(), "hash", null, null, true));
        InfoRows = info;

        Children = entry.IsContainer
            ? ws.Tree.Children(entry.Id).Select(ch =>
            {
                var ce = ws.Resolver.Resolve(ch);
                IBrush? brush = ch.Kind == EntryKind.Folder && ce.ColorHue is { } h ? AppHost.Theme.FolderBrush(h) : null;
                return new ChildRow(ch, ce.Icon, brush, ch.Name, ch.Kind == EntryKind.Web ? ce.Url : ce.Host, Loc.Kind(ch.Kind));
            }).ToList()
            : [];

        PasswordRevealed = false;
        PasswordText = "••••••••";
        RefreshSysInfo(entry, eff);
        OnPropertyChanged(string.Empty);
    }

    private static string SettingText(string key, string value) => key switch
    {
        SettingKeys.FtpEncryption => Loc.T("Ftp." + value),
        _ when value is "true" => Loc.T("Yes"),
        _ when value is "false" => Loc.T("No"),
        _ => value
    };

    public static IBrush? EnvironmentBrush(string? env) => Envs.Brush(env);

    [RelayCommand]
    private void Connect()
    {
        if (Entry is not null) Owner?.Actions.Open(Entry);
    }

    [RelayCommand]
    private async Task ConnectExternal()
    {
        if (Entry is not null && Owner is not null) await Owner.Actions.OpenExternalAsync(Entry);
    }

    [RelayCommand]
    private async Task Edit()
    {
        if (Entry is not null && Owner is not null) await Owner.Actions.EditAsync(Entry);
    }

    [RelayCommand]
    private void ToggleFavorite()
    {
        if (Entry is not null) Owner?.Actions.ToggleFavorite([Entry.Id]);
    }

    [RelayCommand]
    private async Task Copy(CopyField field)
    {
        if (Entry is not null && Owner is not null) await Owner.Actions.CopyFieldAsync(Entry, field);
    }

    [RelayCommand]
    private async Task CopyPassword()
    {
        if (Entry is not null && Owner is not null) await Owner.Actions.CopyFieldAsync(Entry, CopyField.Password);
    }

    [RelayCommand]
    private async Task RevealPassword()
    {
        if (PasswordRevealed)
        {
            PasswordRevealed = false;
            PasswordText = "••••••••";
            return;
        }

        if (Owner is null || Effective?.SecretOwner is not { } secretOwner) return;
        if (!await Owner.Actions.ReauthenticateAsync()) return;
        PasswordText = AppHost.Workspace.GetSecret(secretOwner, Workspace.PasswordField) ?? "";
        PasswordRevealed = true;
    }

    [RelayCommand]
    private void OpenChild(ChildRow row) => Owner?.Actions.ShowDetails(row.Entry);

    [RelayCommand]
    private async Task AddChild(EntryKind kind)
    {
        if (Owner is not null) await Owner.Actions.NewEntryAsync(kind, EntryId);
    }

    [RelayCommand]
    private void RevealInTree() => Owner?.Window?.SelectAndReveal(EntryId);

    [RelayCommand]
    private void Approve()
    {
        if (Entry is not null) Owner?.Actions.ApproveReview([Entry.Id]);
    }
}
