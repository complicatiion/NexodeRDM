using Nexode.App.Localization;
using Nexode.Core.Model;

namespace Nexode.App.Services;

/// <summary>Built-in entry templates plus the templates of the active data source.</summary>
public static class Templates
{
    /// <summary>Built-in templates (ids start with "builtin:"); names are localized.</summary>
    public static IReadOnlyList<EntryTemplate> BuiltIn()
    {
        EntryTemplate T(string id, EntryKind kind, OsFamily os, int? port = null, params (string Key, string Value)[] settings)
        {
            var e = new Entry { Kind = kind, Os = os, Port = port, Name = "" };
            foreach (var (k, v) in settings) e.SetSetting(k, v);
            return new EntryTemplate { Id = "builtin:" + id, Name = Loc.T("Template." + id), Description = Loc.T("Template." + id + ".Info"), Entry = e };
        }

        return
        [
            T("windows", EntryKind.Rdp, OsFamily.Windows, null, (SettingKeys.RdpDrives, "1"), (SettingKeys.RdpClipboard, "1")),
            T("windowsAdmin", EntryKind.Rdp, OsFamily.Windows, null, (SettingKeys.RdpAdmin, "1"), (SettingKeys.RdpClipboard, "1")),
            T("windowsPs", EntryKind.PowerShell, OsFamily.Windows, null, (SettingKeys.PsTransport, "winrm-ssl")),
            T("linux", EntryKind.Ssh, OsFamily.Linux),
            T("linuxFiles", EntryKind.Sftp, OsFamily.Linux),
            T("network", EntryKind.Ssh, OsFamily.Network),
            T("webHttps", EntryKind.Web, OsFamily.Unknown)
        ];
    }

    /// <summary>Templates offered in "New from template": built-in ones first, switched-off kinds left out.</summary>
    public static IReadOnlyList<EntryTemplate> Available() =>
        BuiltIn().Concat(AppHost.Workspace.Templates).Where(t => Features.KindEnabled(t.Entry.Kind)).ToList();

    public static bool IsBuiltIn(EntryTemplate t) => t.Id.StartsWith("builtin:", StringComparison.Ordinal);
}
