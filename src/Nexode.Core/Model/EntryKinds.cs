namespace Nexode.Core.Model;

/// <summary>Static facts per entry kind.</summary>
public static class EntryKinds
{
    public static int? DefaultPort(EntryKind kind) => kind switch
    {
        EntryKind.Rdp => 3389,
        EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp => 22,
        EntryKind.Ftp => 21,
        EntryKind.Vnc => 5900,
        EntryKind.Telnet => 23,
        EntryKind.Web or EntryKind.WebDav or EntryKind.S3 => 443,
        EntryKind.PowerShell => 5985,
        _ => null
    };

    public static string Icon(EntryKind kind) => kind switch
    {
        EntryKind.Folder => "folder",
        EntryKind.Host => "server",
        EntryKind.Rdp => "monitor",
        EntryKind.Ssh => "square-terminal",
        EntryKind.Sftp => "folder-lock",
        EntryKind.Ftp => "folder-up",
        EntryKind.Scp => "folder-key",
        EntryKind.WebDav => "folder-sync",
        EntryKind.S3 => "cloud-cog",
        EntryKind.Vnc => "screen-share",
        EntryKind.Telnet => "terminal",
        EntryKind.Serial => "cable",
        EntryKind.Web => "globe",
        EntryKind.PowerShell => "code-xml",
        EntryKind.Command => "square-chevron-right",
        EntryKind.Database => "database",
        EntryKind.Credential => "key-round",
        EntryKind.Note => "notebook-pen",
        _ => "circle"
    };

    public static string Scheme(EntryKind kind) => kind switch
    {
        EntryKind.Rdp => "rdp",
        EntryKind.Ssh => "ssh",
        EntryKind.Sftp => "sftp",
        EntryKind.Ftp => "ftp",
        EntryKind.Vnc => "vnc",
        EntryKind.Telnet => "telnet",
        EntryKind.Web => "https",
        EntryKind.Scp => "scp",
        EntryKind.WebDav => "davs",
        EntryKind.S3 => "s3",
        _ => ""
    };

    public static bool HasHost(EntryKind kind) => kind is EntryKind.Host or EntryKind.Rdp or EntryKind.Ssh or EntryKind.Sftp
        or EntryKind.Ftp or EntryKind.Scp or EntryKind.S3 or EntryKind.Vnc or EntryKind.Telnet or EntryKind.Database or EntryKind.PowerShell;

    /// <summary>Kinds addressed by a URL instead of host and port.</summary>
    public static bool HasUrl(EntryKind kind) => kind is EntryKind.Web or EntryKind.WebDav;

    /// <summary>Kinds that open in the file browser.</summary>
    public static bool IsFileTransfer(EntryKind kind) => kind is EntryKind.Sftp or EntryKind.Ftp or EntryKind.Scp or EntryKind.WebDav or EntryKind.S3;

    /// <summary>Kinds that can be reached through an SSH jump host (not FTP: its data connections use separate ports).</summary>
    public static bool CanTunnel(EntryKind kind) => kind is EntryKind.Rdp or EntryKind.Ssh or EntryKind.Sftp or EntryKind.Scp
        or EntryKind.Vnc or EntryKind.Telnet or EntryKind.Web or EntryKind.WebDav or EntryKind.S3;

    public static bool HasCredentials(EntryKind kind) => kind is not (EntryKind.Note or EntryKind.Command or EntryKind.Serial);

    public static bool IsConnection(EntryKind kind) => kind is EntryKind.Rdp or EntryKind.Ssh or EntryKind.Sftp or EntryKind.Ftp
        or EntryKind.Scp or EntryKind.WebDav or EntryKind.S3 or EntryKind.Vnc or EntryKind.Telnet or EntryKind.Serial or EntryKind.Web or EntryKind.Command or EntryKind.Database
        or EntryKind.PowerShell;

    /// <summary>Kinds that open as a live session tab (can be part of a split view).</summary>
    public static bool IsSession(EntryKind kind) => kind is EntryKind.Rdp or EntryKind.Ssh or EntryKind.Sftp or EntryKind.Ftp
        or EntryKind.Scp or EntryKind.WebDav or EntryKind.S3 or EntryKind.Vnc or EntryKind.Telnet or EntryKind.Serial or EntryKind.PowerShell;

    /// <summary>Kinds the "New" menu offers, in menu order.</summary>
    public static IReadOnlyList<EntryKind> Creatable { get; } =
    [
        EntryKind.Folder, EntryKind.Host, EntryKind.Rdp, EntryKind.Ssh, EntryKind.Sftp, EntryKind.Ftp, EntryKind.Scp, EntryKind.WebDav, EntryKind.S3, EntryKind.Vnc,
        EntryKind.Telnet, EntryKind.Serial, EntryKind.PowerShell, EntryKind.Web, EntryKind.Command, EntryKind.Database, EntryKind.Credential, EntryKind.Note
    ];

    public static string OsIcon(OsFamily os) => os switch
    {
        OsFamily.Windows => "os-windows",
        OsFamily.Linux => "os-linux",
        OsFamily.MacOS => "os-macos",
        OsFamily.Unix => "terminal",
        OsFamily.Network => "router",
        _ => ""
    };

    /// <summary>Parses "ssh://user@host:2222/path" or "rdp://host"; null when no known scheme.</summary>
    public static Entry? FromUri(string text)
    {
        text = text.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
        {
            return null;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri))
        {
            return null;
        }

        var kind = uri.Scheme.ToLowerInvariant() switch
        {
            "rdp" or "ms-rd" => EntryKind.Rdp,
            "ssh" => EntryKind.Ssh,
            "sftp" => EntryKind.Sftp,
            "ftp" or "ftps" or "ftpes" => EntryKind.Ftp,
            "vnc" => EntryKind.Vnc,
            "telnet" => EntryKind.Telnet,
            "http" or "https" => EntryKind.Web,
            "scp" => EntryKind.Scp,
            "dav" or "davs" or "webdav" or "webdavs" => EntryKind.WebDav,
            "s3" => EntryKind.S3,
            _ => (EntryKind?)null
        };
        if (kind is null)
        {
            return null;
        }

        var entry = new Entry { Kind = kind.Value, Name = uri.Host };
        if (kind == EntryKind.WebDav)
        {
            var secure = uri.Scheme is "davs" or "webdavs";
            entry.Url = new UriBuilder(uri) { Scheme = secure ? "https" : "http", Port = uri.IsDefaultPort ? -1 : uri.Port, UserName = "", Password = "" }.Uri.AbsoluteUri;
            return entry;
        }

        if (kind == EntryKind.Web)
        {
            entry.Url = uri.AbsolutePath == "/" && string.IsNullOrEmpty(uri.Query) ? $"{uri.Scheme}://{uri.Authority}" : uri.AbsoluteUri;
            return entry;
        }

        entry.Host = uri.Host;
        if (!uri.IsDefaultPort && uri.Port > 0)
        {
            entry.Port = uri.Port;
        }

        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            entry.CredentialMode = CredentialMode.Own;
            var user = Uri.UnescapeDataString(uri.UserInfo.Split(':')[0]);
            var slash = user.IndexOf('\\');
            if (slash > 0)
            {
                entry.Domain = user[..slash];
                user = user[(slash + 1)..];
            }
            entry.Username = user;
        }

        if (kind == EntryKind.S3 && uri.AbsolutePath.Trim('/') is { Length: > 0 } bucket)
        {
            entry.SetSetting(SettingKeys.S3Bucket, bucket.Split('/')[0]);
        }

        if (uri.Scheme is "ftps" or "ftpes")
        {
            entry.SetSetting(SettingKeys.FtpEncryption, uri.Scheme == "ftps" ? nameof(FtpEncryption.Implicit) : nameof(FtpEncryption.Explicit));
        }

        if (uri.AbsolutePath.Length > 1 && kind is EntryKind.Sftp or EntryKind.Ftp or EntryKind.Scp)
        {
            entry.SetSetting(SettingKeys.RemotePath, Uri.UnescapeDataString(uri.AbsolutePath));
        }

        return entry;
    }
}
