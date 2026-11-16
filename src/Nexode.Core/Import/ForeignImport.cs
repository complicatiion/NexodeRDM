using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Linq;
using Nexode.Core.Files;
using Nexode.Core.Model;
using Org.BouncyCastle.Crypto.Engines;
using Org.BouncyCastle.Crypto.Modes;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nexode.Core.Import;

public enum ForeignFormat
{
    MRemoteNg,
    RdcMan,
    RdpFile,
    Putty,
    SshConfig,
    WinScp,
    MobaXterm,
    Remmina
}

/// <summary>Result of reading another tool's configuration: entries, plain secrets, warnings.</summary>
public sealed class ForeignImport
{
    public required ForeignFormat Format { get; init; }
    public string SourceName { get; init; } = "";
    public List<Entry> Entries { get; } = [];
    public List<ExportedSecret> Secrets { get; } = [];
    public List<string> Warnings { get; } = [];
    /// <summary>Secrets that could not be decrypted (other user/PC, unknown password).</summary>
    public int UndecryptedSecrets { get; set; }

    /// <summary>As a .nexode payload, so the normal import preview and conflict handling apply.</summary>
    public ConnectionsPayload ToPayload() => new()
    {
        SourceName = SourceName,
        SecretMode = SecretExportMode.EncryptAll,
        Entries = Entries,
        Secrets = Secrets
    };

    internal Entry Add(EntryKind kind, string name, Guid? parent, string stableKey)
    {
        var e = new Entry
        {
            Id = StableId(Format + ":" + stableKey),
            Kind = kind,
            Name = string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name.Trim(),
            ParentId = parent,
            SortKey = Tree.SortKeys.Between(Entries.Count == 0 ? null : Entries[^1].SortKey, null)
        };
        Entries.Add(e);
        return e;
    }

    internal void Secret(Entry e, string? value)
    {
        if (string.IsNullOrEmpty(value)) return;
        Secrets.Add(new ExportedSecret { EntryId = e.Id, Field = "password", Value = value });
        if (e.CredentialMode == CredentialMode.Inherit) e.CredentialMode = CredentialMode.Own;
    }

    /// <summary>Deterministic id: importing the same file twice shows "identical" instead of duplicates.</summary>
    public static Guid StableId(string key)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x80); // version 8 (custom)
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16), bigEndian: true);
    }
}

/// <summary>Readers for mRemoteNG, RDCMan, .rdp, PuTTY and OpenSSH configurations.</summary>
public static partial class ForeignImporters
{
    /// <summary>Detects the format of a file by extension and content; null = unknown.</summary>
    public static ForeignFormat? Detect(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".rdp") return ForeignFormat.RdpFile;
        if (ext == ".rdg") return ForeignFormat.RdcMan;
        string head;
        try
        {
            using var reader = new StreamReader(path);
            var buf = new char[4096];
            head = new string(buf, 0, reader.Read(buf, 0, buf.Length));
        }
        catch (IOException)
        {
            return null;
        }

        if (head.Contains("<RDCMan", StringComparison.Ordinal)) return ForeignFormat.RdcMan;
        if (head.Contains("Connections", StringComparison.Ordinal) && (head.Contains("mrng", StringComparison.OrdinalIgnoreCase) || head.Contains("ConfVersion", StringComparison.Ordinal))) return ForeignFormat.MRemoteNg;
        if (head.Contains("full address:s:", StringComparison.OrdinalIgnoreCase)) return ForeignFormat.RdpFile;
        if (ext == ".remmina" || head.Contains("[remmina]", StringComparison.Ordinal)) return ForeignFormat.Remmina;
        if (ext == ".mxtsessions" || head.Contains("[Bookmarks", StringComparison.Ordinal)) return ForeignFormat.MobaXterm;
        if (head.Contains("[Sessions\\", StringComparison.Ordinal) || head.Contains("[Configuration\\", StringComparison.Ordinal)) return ForeignFormat.WinScp;
        if (Path.GetFileName(path) == "config" || head.TrimStart().StartsWith("Host ", StringComparison.OrdinalIgnoreCase)) return ForeignFormat.SshConfig;
        return null;
    }

    // ------------------------------------------------------------------ mRemoteNG

    public sealed class PasswordRequiredException(string message) : Exception(message);

    /// <summary>mRemoteNG confCons.xml (1.7x – 1.77); passwords are decrypted with the default or the given master password.</summary>
    public static ForeignImport ReadMRemoteNg(string xml, string? masterPassword = null)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new InvalidDataException("Empty file.");
        var crypto = new MRemoteCrypto(root);
        var password = masterPassword ?? "mR3m";
        var protectedValue = Attr(root, "Protected");
        if (protectedValue is not null && crypto.Decrypt(protectedValue, password) is null)
        {
            throw new PasswordRequiredException("The connection file is protected with a master password.");
        }

        if (Attr(root, "FullFileEncryption") is "true" or "True")
        {
            var inner = crypto.Decrypt(root.Value, password) ?? throw new PasswordRequiredException("Wrong master password.");
            var wrapper = XElement.Parse($"<Root>{inner}</Root>");
            root.RemoveNodes();
            root.Add(wrapper.Elements());
        }

        var result = new ForeignImport { Format = ForeignFormat.MRemoteNg, SourceName = Attr(root, "Name") is { Length: > 0 } n && n != "Connections" ? n : "mRemoteNG" };
        void Walk(XElement parent, Guid? parentId, string path)
        {
            foreach (var node in parent.Elements().Where(x => x.Name.LocalName == "Node"))
            {
                var name = Attr(node, "Name") ?? "";
                var key = Attr(node, "Id") ?? path + "/" + name;
                var isFolder = string.Equals(Attr(node, "Type"), "Container", StringComparison.OrdinalIgnoreCase);
                var protocol = (Attr(node, "Protocol") ?? "").ToUpperInvariant();
                var kind = isFolder ? EntryKind.Folder : protocol switch
                {
                    "RDP" => EntryKind.Rdp,
                    "SSH1" or "SSH2" => EntryKind.Ssh,
                    "TELNET" or "RAW" or "RLOGIN" => EntryKind.Telnet,
                    "VNC" => EntryKind.Vnc,
                    "HTTP" or "HTTPS" => EntryKind.Web,
                    "INTAPP" or "POWERSHELL" => EntryKind.Command,
                    _ => EntryKind.Host
                };
                var e = result.Add(kind, name, parentId, key);
                e.Description = Attr(node, "Descr");
                var host = Attr(node, "Hostname");
                if (kind == EntryKind.Web)
                {
                    var scheme = protocol == "HTTP" ? "http" : "https";
                    var port = Int(Attr(node, "Port"));
                    e.Url = string.IsNullOrEmpty(host) ? null : $"{scheme}://{host}" + (port is { } pn && pn != (scheme == "http" ? 80 : 443) ? ":" + pn : "");
                }
                else if (!isFolder)
                {
                    e.Host = string.IsNullOrEmpty(host) ? null : host;
                    var port = Int(Attr(node, "Port"));
                    if (port is not null && port != EntryKinds.DefaultPort(kind)) e.Port = port;
                }

                if (kind == EntryKind.Command)
                {
                    e.NeedsReview = true;
                    e.SetSetting(SettingKeys.CommandLine, Attr(node, "ExtApp"));
                }

                var inheritUser = Attr(node, "InheritUsername") is "true" or "True";
                var inheritPass = Attr(node, "InheritPassword") is "true" or "True";
                var user = Attr(node, "Username");
                var domain = Attr(node, "Domain");
                var pw = Attr(node, "Password") is { Length: > 0 } enc ? crypto.Decrypt(enc, password) : null;
                if (Attr(node, "Password") is { Length: > 0 } && pw is null) result.UndecryptedSecrets++;
                if (!(inheritUser && inheritPass) && (!string.IsNullOrEmpty(user) || !string.IsNullOrEmpty(pw)))
                {
                    e.CredentialMode = CredentialMode.Own;
                    e.Username = user;
                    e.Domain = string.IsNullOrEmpty(domain) ? null : domain;
                    result.Secret(e, pw);
                }

                if (kind == EntryKind.Rdp)
                {
                    if (Attr(node, "ConnectToConsole") is "true" or "True") e.SetSetting(SettingKeys.RdpAdmin, "1");
                    if (Attr(node, "RedirectDiskDrives") is "true" or "True") e.SetSetting(SettingKeys.RdpDrives, "1");
                    if (Attr(node, "RDGatewayHostname") is { Length: > 0 } gw) e.SetSetting(SettingKeys.RdpGateway, gw);
                }

                if (kind == EntryKind.Vnc && Attr(node, "VNCViewOnly") is "true" or "True") e.SetSetting(SettingKeys.VncViewOnly, "1");
                if (isFolder) Walk(node, e.Id, key);
            }
        }

        Walk(root, null, "");
        return result;
    }

    /// <summary>mRemoteNG crypto: AES-GCM (PBKDF2-SHA1 key, 16-byte salt and nonce) or legacy AES-CBC (MD5 key).</summary>
    private sealed class MRemoteCrypto(XElement root)
    {
        private readonly bool _gcm = !string.Equals(Attr(root, "BlockCipherMode"), "CBC", StringComparison.OrdinalIgnoreCase)
                                     && Attr(root, "EncryptionEngine") is not null;
        private readonly int _iterations = Int(Attr(root, "KdfIterations")) ?? 1000;

        public string? Decrypt(string base64, string password)
        {
            byte[] data;
            try
            {
                data = Convert.FromBase64String(base64.Trim());
            }
            catch (FormatException)
            {
                return null;
            }

            return _gcm ? Gcm(data, password) : Cbc(data, password);
        }

        private string? Gcm(byte[] data, string password)
        {
            if (data.Length < 48) return null;
            var salt = data.AsSpan(0, 16).ToArray();
            var nonce = data.AsSpan(16, 16).ToArray();
            var key = Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, _iterations, HashAlgorithmName.SHA1, 32);
            var cipher = new GcmBlockCipher(new AesEngine());
            cipher.Init(false, new AeadParameters(new KeyParameter(key), 128, nonce, salt));
            var body = data.AsSpan(32).ToArray();
            var plain = new byte[cipher.GetOutputSize(body.Length)];
            try
            {
                var len = cipher.ProcessBytes(body, 0, body.Length, plain, 0);
                len += cipher.DoFinal(plain, len);
                return Encoding.UTF8.GetString(plain, 0, len);
            }
            catch (Org.BouncyCastle.Crypto.InvalidCipherTextException)
            {
                return null;
            }
        }

        private static string? Cbc(byte[] data, string password)
        {
            if (data.Length < 32) return null;
            try
            {
                using var aes = Aes.Create();
                aes.Key = MD5.HashData(Encoding.UTF8.GetBytes(password));
                return Encoding.UTF8.GetString(aes.DecryptCbc(data.AsSpan(16), data.AsSpan(0, 16)));
            }
            catch (CryptographicException)
            {
                return null;
            }
        }
    }

    // ------------------------------------------------------------------ RDCMan

    /// <param name="unprotect">Windows DPAPI (current user); null elsewhere – passwords are then skipped.</param>
    public static ForeignImport ReadRdcMan(string xml, Func<byte[], byte[]?>? unprotect)
    {
        var doc = XDocument.Parse(xml);
        var file = doc.Root?.Element("file") ?? throw new InvalidDataException("Not an RDCMan file.");
        var result = new ForeignImport { Format = ForeignFormat.RdcMan, SourceName = file.Element("properties")?.Element("name")?.Value ?? "RDCMan" };

        void Credentials(XElement node, Entry e)
        {
            var cred = node.Element("logonCredentials");
            if (cred is null || Attr(cred, "inherit") is "FromParent") return;
            var user = cred.Element("userName")?.Value;
            var domain = cred.Element("domain")?.Value;
            if (string.IsNullOrEmpty(user)) return;
            e.CredentialMode = CredentialMode.Own;
            e.Username = user;
            e.Domain = string.IsNullOrEmpty(domain) ? null : domain;
            if (cred.Element("password")?.Value is { Length: > 0 } pw)
            {
                var plain = Unprotect(pw, unprotect);
                if (plain is null) result.UndecryptedSecrets++;
                result.Secret(e, plain);
            }
        }

        void Walk(XElement parent, Guid? parentId, string path)
        {
            foreach (var node in parent.Elements())
            {
                var props = node.Element("properties");
                if (node.Name.LocalName == "group")
                {
                    var name = props?.Element("name")?.Value ?? "";
                    var e = result.Add(EntryKind.Folder, name, parentId, path + "/" + name);
                    Credentials(node, e);
                    Walk(node, e.Id, path + "/" + name);
                }
                else if (node.Name.LocalName == "server")
                {
                    var host = props?.Element("name")?.Value ?? "";
                    var display = props?.Element("displayName")?.Value;
                    var e = result.Add(EntryKind.Rdp, string.IsNullOrEmpty(display) ? host : display, parentId, path + "/" + host);
                    e.Host = host;
                    e.Description = props?.Element("comment")?.Value;
                    var conn = node.Element("connectionSettings");
                    if (Int(conn?.Element("port")?.Value) is { } port && port != 3389) e.Port = port;
                    if (conn?.Element("connectToConsole")?.Value is "True" or "true") e.SetSetting(SettingKeys.RdpAdmin, "1");
                    if (node.Element("gatewaySettings")?.Element("hostName")?.Value is { Length: > 0 } gw && node.Element("gatewaySettings")?.Element("enabled")?.Value is "True") e.SetSetting(SettingKeys.RdpGateway, gw);
                    Credentials(node, e);
                }
                else if (node.Name.LocalName == "smartGroup")
                {
                    result.Warnings.Add("Smart groups are skipped: " + props?.Element("name")?.Value);
                }
            }
        }

        Walk(file, null, "");
        return result;
    }

    private static string? Unprotect(string base64, Func<byte[], byte[]?>? unprotect)
    {
        if (unprotect is null) return null;
        try
        {
            var plain = unprotect(Convert.FromBase64String(base64));
            return plain is null ? null : Encoding.Unicode.GetString(plain).TrimEnd('\0');
        }
        catch (FormatException)
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ .rdp

    public static ForeignImport ReadRdpFile(string text, string fileName, Func<byte[], byte[]?>? unprotect)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in text.Split('\n'))
        {
            var parts = line.Trim().TrimStart('﻿').Split(':', 3);
            if (parts.Length == 3) values[parts[0].Trim()] = parts[2].Trim();
        }

        var result = new ForeignImport { Format = ForeignFormat.RdpFile, SourceName = fileName };
        var address = values.GetValueOrDefault("full address") ?? values.GetValueOrDefault("alternate full address") ?? "";
        var e = result.Add(EntryKind.Rdp, Path.GetFileNameWithoutExtension(fileName), null, fileName + "/" + address);
        var (host, port) = SplitHostPort(address);
        e.Host = host;
        port ??= Int(values.GetValueOrDefault("server port"));
        if (port is not null && port != 3389) e.Port = port;
        if (values.GetValueOrDefault("username") is { Length: > 0 } user)
        {
            e.CredentialMode = CredentialMode.Own;
            var slash = user.IndexOf('\\');
            if (slash > 0)
            {
                e.Domain = user[..slash];
                e.Username = user[(slash + 1)..];
            }
            else
            {
                e.Username = user;
                e.Domain = values.GetValueOrDefault("domain");
            }
        }

        if (values.GetValueOrDefault("password 51") is { Length: > 0 } hex && unprotect is not null)
        {
            try
            {
                var plain = unprotect(Convert.FromHexString(hex));
                if (plain is not null) result.Secret(e, Encoding.Unicode.GetString(plain).TrimEnd('\0'));
            }
            catch (FormatException)
            {
            }
        }

        if (values.GetValueOrDefault("administrative session") == "1") e.SetSetting(SettingKeys.RdpAdmin, "1");
        if (values.GetValueOrDefault("gatewayhostname") is { Length: > 0 } gw && values.GetValueOrDefault("gatewayusagemethod") is "1" or "2") e.SetSetting(SettingKeys.RdpGateway, gw);
        if (values.GetValueOrDefault("redirectdrives") == "1" || values.GetValueOrDefault("drivestoredirect") is { Length: > 0 }) e.SetSetting(SettingKeys.RdpDrives, "1");
        if (values.GetValueOrDefault("redirectclipboard") == "0") e.SetSetting(SettingKeys.RdpClipboard, "0");
        if (values.GetValueOrDefault("desktopwidth") is { } w && values.GetValueOrDefault("desktopheight") is { } h && values.GetValueOrDefault("screen mode id") != "2")
        {
            e.SetSetting(SettingKeys.RdpResolution, $"{w}x{h}");
        }

        return result;
    }

    // ------------------------------------------------------------------ PuTTY

    /// <summary>PuTTY sessions (registry values or ~/.putty/sessions files) as name → key/value.</summary>
    public static ForeignImport ReadPutty(IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> sessions)
    {
        var result = new ForeignImport { Format = ForeignFormat.Putty, SourceName = "PuTTY" };
        foreach (var (rawName, v) in sessions)
        {
            var name = Uri.UnescapeDataString(rawName);
            if (name == "Default Settings") continue;
            var host = v.GetValueOrDefault("HostName") ?? "";
            if (host.Length == 0) continue;
            var protocol = (v.GetValueOrDefault("Protocol") ?? "ssh").ToLowerInvariant();
            var kind = protocol switch
            {
                "ssh" => EntryKind.Ssh,
                "telnet" or "raw" or "rlogin" => EntryKind.Telnet,
                _ => (EntryKind?)null
            };
            if (kind is null)
            {
                result.Warnings.Add($"{name}: protocol \"{protocol}\" is not supported.");
                continue;
            }

            var e = result.Add(kind.Value, name, null, name);
            var at = host.LastIndexOf('@');
            if (at > 0)
            {
                e.Username = host[..at];
                host = host[(at + 1)..];
            }

            e.Host = host;
            if (Int(v.GetValueOrDefault("PortNumber")) is { } port && port != EntryKinds.DefaultPort(kind.Value)) e.Port = port;
            if (v.GetValueOrDefault("UserName") is { Length: > 0 } user) e.Username = user;
            if (e.Username is not null) e.CredentialMode = CredentialMode.Own;
            if (v.GetValueOrDefault("PublicKeyFile") is { Length: > 0 } key)
            {
                e.CredentialMode = CredentialMode.Own;
                e.SetSetting(SettingKeys.SshKeyFile, key);
                if (key.EndsWith(".ppk", StringComparison.OrdinalIgnoreCase)) result.Warnings.Add($"{name}: PuTTY keys (.ppk) are supported by Nexode directly.");
            }
        }

        return result;
    }

    /// <summary>Linux/macOS PuTTY session files ("key=value" lines).</summary>
    public static IReadOnlyDictionary<string, string> ParsePuttySessionFile(string text) =>
        text.Split('\n').Select(l => l.TrimEnd('\r').Split('=', 2)).Where(p => p.Length == 2).GroupBy(p => p[0]).ToDictionary(g => g.Key, g => g.Last()[1]);

    // ------------------------------------------------------------------ OpenSSH config

    public static ForeignImport ReadSshConfig(string text)
    {
        var result = new ForeignImport { Format = ForeignFormat.SshConfig, SourceName = "SSH config" };
        Dictionary<string, string>? current = null;
        var blocks = new List<(string Alias, Dictionary<string, string> Values)>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            var sep = line.IndexOfAny([' ', '\t', '=']);
            if (sep < 0) continue;
            var key = line[..sep].Trim().ToLowerInvariant();
            var value = line[(sep + 1)..].Trim().TrimStart('=').Trim().Trim('"');
            if (key == "host")
            {
                current = null;
                foreach (var alias in value.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (alias.Contains('*') || alias.Contains('?') || alias.StartsWith('!')) continue;
                    current ??= new Dictionary<string, string>();
                    blocks.Add((alias, current));
                }
            }
            else if (key == "match")
            {
                current = null;
            }
            else if (current is not null && !current.ContainsKey(key))
            {
                current[key] = value;
            }
        }

        foreach (var (alias, v) in blocks)
        {
            var e = result.Add(EntryKind.Ssh, alias, null, alias);
            e.Host = v.GetValueOrDefault("hostname") ?? alias;
            if (Int(v.GetValueOrDefault("port")) is { } port && port != 22) e.Port = port;
            if (v.GetValueOrDefault("user") is { Length: > 0 } user)
            {
                e.CredentialMode = CredentialMode.Own;
                e.Username = user;
            }

            if (v.GetValueOrDefault("identityfile") is { Length: > 0 } key)
            {
                e.CredentialMode = CredentialMode.Own;
                e.SetSetting(SettingKeys.SshKeyFile, key);
            }

            if (v.ContainsKey("proxyjump") || v.ContainsKey("proxycommand")) result.Warnings.Add($"{alias}: ProxyJump/ProxyCommand is not supported yet.");
        }

        return result;
    }

    // ------------------------------------------------------------------ helpers

    private static string? Attr(XElement e, string name) => e.Attribute(name)?.Value;

    private static int? Int(string? s) => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;

    private static (string Host, int? Port) SplitHostPort(string address)
    {
        if (address.StartsWith('[') && address.IndexOf(']') is var close and > 0)
        {
            var rest = address[(close + 1)..];
            return (address[1..close], rest.StartsWith(':') ? Int(rest[1..]) : null);
        }

        var colon = address.LastIndexOf(':');
        return colon > 0 && address.IndexOf(':') == colon ? (address[..colon], Int(address[(colon + 1)..])) : (address, null);
    }
}
