using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Nexode.Core.Model;

namespace Nexode.Core.Import;

/// <summary>Readers for WinSCP, MobaXterm and Remmina.</summary>
public static partial class ForeignImporters
{
    // ------------------------------------------------------------------ WinSCP

    /// <summary>
    /// WinSCP sessions (registry values or the [Sessions\…] sections of WinSCP.ini). "Folder/Sub/Name" session names
    /// become folders. Passwords are decoded unless WinSCP protects them with its master password.
    /// </summary>
    public static ForeignImport ReadWinScp(IEnumerable<(string Name, IReadOnlyDictionary<string, string> Values)> sessions, bool masterPasswordUsed = false)
    {
        var result = new ForeignImport { Format = ForeignFormat.WinScp, SourceName = "WinSCP" };
        var folders = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var skippedPasswords = 0;
        foreach (var (rawName, v) in sessions)
        {
            var full = Uri.UnescapeDataString(rawName.Replace('+', ' '));
            if (full is "Default Settings" or "") continue;
            var host = v.GetValueOrDefault("HostName") ?? "";
            if (host.Length == 0) continue;

            var parts = full.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var parent = FolderPath(result, folders, parts[..^1], "winscp");
            var protocol = Int(v.GetValueOrDefault("FSProtocol")) ?? 2;
            EntryKind? kind = protocol switch
            {
                0 or 1 or 2 => EntryKind.Sftp,
                5 => EntryKind.Ftp,
                _ => null
            };
            if (kind is null)
            {
                result.Warnings.Add($"{full}: protocol {(protocol == 6 ? "WebDAV" : protocol == 7 ? "S3" : protocol.ToString(CultureInfo.InvariantCulture))} is not supported.");
                continue;
            }

            if (protocol == 0) result.Warnings.Add($"{full}: SCP is opened as SFTP.");
            var e = result.Add(kind.Value, parts[^1], parent, "s:" + full);
            e.Host = host.Trim();
            if (Int(v.GetValueOrDefault("PortNumber")) is { } port && port != EntryKinds.DefaultPort(kind.Value)) e.Port = port;
            var user = v.GetValueOrDefault("UserName");
            if (!string.IsNullOrEmpty(user))
            {
                e.Username = Uri.UnescapeDataString(user);
                e.CredentialMode = CredentialMode.Own;
            }

            if (v.GetValueOrDefault("RemoteDirectory") is { Length: > 0 } dir) e.SetSetting(SettingKeys.RemotePath, Uri.UnescapeDataString(dir));
            if (v.GetValueOrDefault("PublicKeyFile") is { Length: > 0 } key)
            {
                e.SetSetting(SettingKeys.SshKeyFile, Uri.UnescapeDataString(key));
                e.CredentialMode = CredentialMode.Own;
            }

            if (kind == EntryKind.Ftp)
            {
                var ftps = Int(v.GetValueOrDefault("Ftps")) ?? 0;
                if (ftps != 0) e.SetSetting(SettingKeys.FtpEncryption, ftps == 1 ? nameof(FtpEncryption.Implicit) : nameof(FtpEncryption.Explicit));
            }

            if (v.GetValueOrDefault("Password") is { Length: > 0 } pw)
            {
                if (masterPasswordUsed)
                {
                    skippedPasswords++;
                }
                else if (WinScpPassword(pw, e.Username ?? "", host) is { } clear)
                {
                    result.Secret(e, clear);
                }
                else
                {
                    result.UndecryptedSecrets++;
                }
            }
        }

        if (skippedPasswords > 0)
        {
            result.UndecryptedSecrets += skippedPasswords;
            result.Warnings.Add("WinSCP protects the passwords with its master password: they are not taken over.");
        }

        return result;
    }

    /// <summary>WinSCP.ini: sessions plus whether a master password is in use.</summary>
    public static ForeignImport ReadWinScpIni(string text)
    {
        var sections = Ini(text);
        var master = sections.FirstOrDefault(s => s.Name.Equals(@"Configuration\Security", StringComparison.OrdinalIgnoreCase)).Values?
            .GetValueOrDefault("UseMasterPassword") is "1";
        var sessions = sections.Where(s => s.Name.StartsWith(@"Sessions\", StringComparison.OrdinalIgnoreCase))
            .Select(s => (s.Name[9..], (IReadOnlyDictionary<string, string>)s.Values));
        return ReadWinScp(sessions, master);
    }

    /// <summary>WinSCP's password obfuscation (not encryption): nibble pairs XOR 0xA3, prefixed with user + host.</summary>
    public static string? WinScpPassword(string hex, string user, string host)
    {
        try
        {
            var pos = 0;
            int Next()
            {
                var a = Convert.ToInt32(hex.Substring(pos, 2), 16);
                pos += 2;
                return ~(a ^ 0xA3) & 0xFF;
            }

            var flag = Next();
            int length;
            if (flag == 0xFF)
            {
                Next();
                length = Next();
            }
            else
            {
                length = flag;
            }

            var skip = Next();
            pos += skip * 2;
            var bytes = new byte[length];
            for (var i = 0; i < length; i++) bytes[i] = (byte)Next();
            // Newer WinSCP versions store UTF-8.
            var clear = Encoding.UTF8.GetString(bytes);
            if (flag == 0xFF)
            {
                var key = user + host;
                if (!clear.StartsWith(key, StringComparison.Ordinal)) return null;
                clear = clear[key.Length..];
            }

            return clear;
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or FormatException)
        {
            return null;
        }
    }

    /// <summary>Counterpart of <see cref="WinScpPassword"/> (tests).</summary>
    public static string WinScpObfuscate(string password, string user, string host)
    {
        var data = Encoding.UTF8.GetBytes(user + host + password);
        var sb = new StringBuilder();
        void Put(int b) => sb.Append((~b & 0xFF ^ 0xA3).ToString("X2", CultureInfo.InvariantCulture));
        Put(0xFF);
        Put(0);
        Put(data.Length);
        Put(0);
        foreach (var b in data) Put(b);
        return sb.ToString();
    }

    // ------------------------------------------------------------------ MobaXterm

    /// <summary>
    /// MobaXterm.ini or exported .mxtsessions: [Bookmarks…] sections, "SubRep" is the folder. Passwords are not taken
    /// over (MobaXterm encrypts them per user / with its master password).
    /// </summary>
    public static ForeignImport ReadMobaXterm(string text)
    {
        var result = new ForeignImport { Format = ForeignFormat.MobaXterm, SourceName = "MobaXterm" };
        var folders = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        var sawPasswords = false;
        foreach (var (section, values, order) in IniOrdered(text))
        {
            if (section.Equals("Passwords", StringComparison.OrdinalIgnoreCase) || section.Equals("Credentials", StringComparison.OrdinalIgnoreCase))
            {
                sawPasswords |= values.Count > 0;
                continue;
            }

            if (!section.StartsWith("Bookmarks", StringComparison.OrdinalIgnoreCase)) continue;
            var sub = values.GetValueOrDefault("SubRep") ?? "";
            var parent = FolderPath(result, folders, sub.Split('\\', StringSplitOptions.RemoveEmptyEntries), "moba");
            foreach (var key in order)
            {
                if (key.Equals("SubRep", StringComparison.OrdinalIgnoreCase) || key.Equals("ImgNum", StringComparison.OrdinalIgnoreCase)) continue;
                var value = values[key].Trim();
                // name=#icon#type%field1%field2…
                var hash = value.IndexOf('#', 1);
                if (!value.StartsWith('#') || hash < 0) continue;
                var fields = value[(hash + 1)..].Split('%');
                if (fields.Length < 2 || !int.TryParse(fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var type)) continue;
                var name = key.Trim();
                string F(int i) => i < fields.Length ? fields[i].Trim() : "";

                EntryKind? kind = type switch
                {
                    0 => EntryKind.Ssh,
                    1 => EntryKind.Telnet,
                    4 => EntryKind.Rdp,
                    5 => EntryKind.Vnc,
                    6 => EntryKind.Ftp,
                    7 => EntryKind.Sftp,
                    8 => EntryKind.Serial,
                    11 => EntryKind.Web,
                    _ => null
                };
                if (kind is null)
                {
                    result.Warnings.Add($"{name}: this MobaXterm session type is not supported.");
                    continue;
                }

                var e = result.Add(kind.Value, name, parent, "b:" + sub + "\\" + name);
                switch (kind)
                {
                    case EntryKind.Serial:
                        e.SetSetting(SettingKeys.SerialPort, F(1).Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? F(1));
                        if (Int(F(2)) is { } baud && baud >= 300) e.SetSetting(SettingKeys.SerialBaud, baud.ToString(CultureInfo.InvariantCulture));
                        result.Warnings.Add($"{name}: check the serial line settings.");
                        break;
                    case EntryKind.Web:
                        e.Url = F(1);
                        break;
                    default:
                        e.Host = F(1);
                        if (Int(F(2)) is { } port && port > 0 && port != EntryKinds.DefaultPort(kind.Value)) e.Port = port;
                        var user = F(3);
                        if (user.Length > 0 && !user.StartsWith('['))
                        {
                            e.Username = user;
                            e.CredentialMode = CredentialMode.Own;
                        }

                        break;
                }
            }
        }

        if (sawPasswords) result.Warnings.Add("MobaXterm passwords are encrypted for your Windows account or its master password: enter them again in Nexode.");
        return result;
    }

    // ------------------------------------------------------------------ Remmina

    /// <summary>
    /// Remmina profiles (*.remmina). Passwords stored in the files are decrypted with the secret from remmina.pref;
    /// passwords kept in the desktop keyring (".") are not available.
    /// </summary>
    public static ForeignImport ReadRemmina(IEnumerable<(string FileName, string Text)> files, string? prefText)
    {
        var result = new ForeignImport { Format = ForeignFormat.Remmina, SourceName = "Remmina" };
        var folders = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        byte[]? secret = null;
        if (prefText is not null && Ini(prefText).SelectMany(s => s.Values).FirstOrDefault(kv => kv.Key == "secret").Value is { Length: > 0 } b64)
        {
            try { secret = Convert.FromBase64String(b64); } catch (FormatException) { }
        }

        var keyring = 0;
        foreach (var (fileName, text) in files.OrderBy(f => f.FileName, StringComparer.Ordinal))
        {
            var v = Ini(text).FirstOrDefault(s => s.Name.Equals("remmina", StringComparison.OrdinalIgnoreCase)).Values;
            if (v is null) continue;
            var protocol = (v.GetValueOrDefault("protocol") ?? "").ToUpperInvariant();
            EntryKind? kind = protocol switch
            {
                "RDP" => EntryKind.Rdp,
                "SSH" => EntryKind.Ssh,
                "SFTP" => EntryKind.Sftp,
                "VNC" or "VNCI" => EntryKind.Vnc,
                "WWW" => EntryKind.Web,
                _ => null
            };
            var name = v.GetValueOrDefault("name") is { Length: > 0 } n ? n : Path.GetFileNameWithoutExtension(fileName);
            if (kind is null)
            {
                result.Warnings.Add($"{name}: protocol \"{protocol}\" is not supported.");
                continue;
            }

            var group = v.GetValueOrDefault("group") ?? "";
            var parent = FolderPath(result, folders, group.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries), "remmina");
            var e = result.Add(kind.Value, name, parent, "f:" + Path.GetFileName(fileName));
            var server = v.GetValueOrDefault("server") ?? "";
            if (kind == EntryKind.Web)
            {
                e.Url = server.Contains("://", StringComparison.Ordinal) ? server : "https://" + server;
            }
            else
            {
                var (h, port) = SplitHostPort(server);
                e.Host = h;
                if (port is { } pt && pt != EntryKinds.DefaultPort(kind.Value)) e.Port = pt;
            }

            var user = v.GetValueOrDefault("username");
            if (string.IsNullOrEmpty(user) && kind == EntryKind.Ssh) user = v.GetValueOrDefault("ssh_username");
            if (!string.IsNullOrEmpty(user))
            {
                e.Username = user;
                e.CredentialMode = CredentialMode.Own;
            }

            if (v.GetValueOrDefault("domain") is { Length: > 0 } domain) e.Domain = domain;
            if (v.GetValueOrDefault("ssh_privatekey") is { Length: > 0 } key) e.SetSetting(SettingKeys.SshKeyFile, key);
            if (v.GetValueOrDefault("resolution_mode") == "2" && v.GetValueOrDefault("resolution_width") is { } w && v.GetValueOrDefault("resolution_height") is { } hh)
            {
                e.SetSetting(SettingKeys.RdpResolution, $"{w}x{hh}");
            }

            var pw = v.GetValueOrDefault("password");
            if (pw == ".")
            {
                keyring++;
            }
            else if (!string.IsNullOrEmpty(pw))
            {
                if (secret is { Length: >= 32 } && RemminaDecrypt(pw, secret) is { Length: > 0 } clear) result.Secret(e, clear);
                else result.UndecryptedSecrets++;
            }
        }

        if (keyring > 0) result.Warnings.Add($"{keyring} password(s) are kept in the desktop keyring and are not taken over.");
        return result;
    }

    /// <summary>Remmina: 3DES-CBC with key = secret[0..24], IV = secret[24..32].</summary>
    public static string? RemminaDecrypt(string base64, byte[] secret)
    {
        try
        {
            using var des = TripleDES.Create();
            des.Key = secret[..24];
            var plain = des.DecryptCbc(Convert.FromBase64String(base64), secret[24..32], PaddingMode.Zeros);
            return Encoding.UTF8.GetString(plain).TrimEnd('\0');
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }

    public static string RemminaEncrypt(string text, byte[] secret)
    {
        using var des = TripleDES.Create();
        des.Key = secret[..24];
        return Convert.ToBase64String(des.EncryptCbc(Encoding.UTF8.GetBytes(text), secret[24..32], PaddingMode.Zeros));
    }

    // ------------------------------------------------------------------ helpers

    private static Guid? FolderPath(ForeignImport result, Dictionary<string, Guid> folders, IReadOnlyList<string> parts, string scope)
    {
        Guid? parent = null;
        var path = "";
        foreach (var part in parts)
        {
            path = path.Length == 0 ? part : path + "/" + part;
            if (!folders.TryGetValue(path, out var id))
            {
                var f = result.Add(EntryKind.Folder, part, parent, scope + ":folder:" + path);
                id = f.Id;
                folders[path] = id;
            }

            parent = id;
        }

        return parent;
    }

    private static List<(string Name, Dictionary<string, string> Values)> Ini(string text) =>
        IniOrdered(text).Select(s => (s.Name, s.Values)).ToList();

    private static List<(string Name, Dictionary<string, string> Values, List<string> Order)> IniOrdered(string text)
    {
        var list = new List<(string, Dictionary<string, string>, List<string>)>();
        Dictionary<string, string>? current = null;
        List<string>? order = null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            var t = line.Trim();
            if (t.Length == 0 || t.StartsWith(';')) continue;
            if (t.StartsWith('[') && t.EndsWith(']'))
            {
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                order = [];
                list.Add((t[1..^1], current, order));
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0 || current is null) continue;
            var key = line[..eq].Trim();
            if (!current.ContainsKey(key)) order!.Add(key);
            current[key] = line[(eq + 1)..];
        }

        return list;
    }
}
