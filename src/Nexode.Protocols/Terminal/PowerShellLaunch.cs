using System.Text;
using System.Text.RegularExpressions;
using Nexode.Core.Model;

namespace Nexode.Protocols.Terminal;

/// <summary>
/// PowerShell remoting in an embedded terminal: a local PowerShell enters a remote session (Enter-PSSession) over
/// WinRM (HTTP/HTTPS) or SSH. The password is never on the command line: the start script asks for it with
/// Read-Host -AsSecureString and Nexode types it into the prompt; SSH asks itself and gets it the same way.
/// Leaving the remote session (exit) ends the local PowerShell as well, so the tab ends with the remote session.
/// </summary>
public static partial class PowerShellLaunch
{
    /// <summary>Visible prompt of the start script; Nexode answers it with the password.</summary>
    public const string PasswordPrompt = "Nexode credential";

    public sealed record Plan(string Executable, IReadOnlyList<string> Arguments, string? Secret, Regex? SecretPrompt);

    public static string Transport(ConnectParams p) => p.Setting(SettingKeys.PsTransport) ?? "winrm";

    public static int DefaultPort(string transport) => transport switch
    {
        "winrm-ssl" => 5986,
        "ssh" => 22,
        _ => 5985
    };

    /// <summary>Executable to use; null when no suitable PowerShell is installed (PowerShell 7 is needed for SSH and outside Windows).</summary>
    public static string? FindExecutable(ConnectParams p, Func<string, string?> which, bool windows, string? systemDirectory)
    {
        var ssh = Transport(p) == "ssh";
        if (!windows) return which("pwsh");
        var pwsh = which("pwsh.exe");
        if (ssh || p.Flag(SettingKeys.PsPwsh)) return pwsh ?? (ssh ? null : WindowsPowerShell(systemDirectory));
        return WindowsPowerShell(systemDirectory) ?? pwsh;
    }

    private static string? WindowsPowerShell(string? systemDirectory) =>
        systemDirectory is null ? "powershell.exe" : Path.Combine(systemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe");

    public static Plan Build(ConnectParams p, string executable)
    {
        var script = Script(p, out var asksPassword);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var args = new List<string> { "-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", encoded };
        if (asksPassword) return new Plan(executable, args, p.Password, PromptRegex());
        // SSH transport: ssh asks on its own ("user@host's password:").
        if (Transport(p) == "ssh" && !string.IsNullOrEmpty(p.Password)) return new Plan(executable, args, p.Password, SshPasswordRegex());
        return new Plan(executable, args, null, null);
    }

    /// <summary>The start script (no secrets in it).</summary>
    public static string Script(ConnectParams p, out bool asksPassword)
    {
        var transport = Transport(p);
        var port = p.Port > 0 ? p.Port : DefaultPort(transport);
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        // Back at the local prompt (remote session left or failed): end this PowerShell, so the tab ends too.
        sb.AppendLine("function global:prompt { [Environment]::Exit(0) }");
        asksPassword = false;

        if (transport == "ssh")
        {
            sb.Append("Enter-PSSession -HostName ").Append(Quote(p.Host)).Append(" -Port ").Append(port);
            if (!string.IsNullOrEmpty(p.Username)) sb.Append(" -UserName ").Append(Quote(p.Username));
            if (!string.IsNullOrEmpty(p.KeyFile)) sb.Append(" -KeyFilePath ").Append(Quote(p.KeyFile));
            sb.AppendLine();
            return sb.ToString();
        }

        var user = UserName(p);
        if (user is not null)
        {
            if (!string.IsNullOrEmpty(p.Password))
            {
                asksPassword = true;
                sb.Append("$nxSecret = Read-Host -AsSecureString -Prompt ").AppendLine(Quote(PasswordPrompt));
                sb.Append("$nxCred = [System.Management.Automation.PSCredential]::new(").Append(Quote(user)).AppendLine(", $nxSecret)");
                sb.AppendLine("Remove-Variable nxSecret");
                sb.AppendLine("Clear-Host");
            }
            else
            {
                sb.Append("$nxCred = Get-Credential -UserName ").Append(Quote(user)).AppendLine(" -Message 'Nexode RDM'");
            }
        }

        if (p.Flag(SettingKeys.PsSkipCertCheck) && transport == "winrm-ssl")
            sb.AppendLine("$nxOptions = New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck");

        sb.Append("Enter-PSSession -ComputerName ").Append(Quote(p.Host)).Append(" -Port ").Append(port);
        if (transport == "winrm-ssl") sb.Append(" -UseSSL");
        if (user is not null) sb.Append(" -Credential $nxCred");
        if (p.Setting(SettingKeys.PsAuth) is { } auth && auth is "Negotiate" or "Kerberos" or "CredSSP") sb.Append(" -Authentication ").Append(auth);
        if (p.Setting(SettingKeys.PsConfiguration) is { } config) sb.Append(" -ConfigurationName ").Append(Quote(config));
        if (p.Flag(SettingKeys.PsSkipCertCheck) && transport == "winrm-ssl") sb.Append(" -SessionOption $nxOptions");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>DOMAIN\user unless the user name already carries a domain (or is a UPN).</summary>
    public static string? UserName(ConnectParams p)
    {
        if (string.IsNullOrEmpty(p.Username)) return null;
        if (string.IsNullOrEmpty(p.Domain) || p.Username.Contains('\\') || p.Username.Contains('@')) return p.Username;
        return p.Domain + "\\" + p.Username;
    }

    /// <summary>PowerShell single-quoted string literal.</summary>
    public static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    [GeneratedRegex(@"Nexode credential: ?\s*$")]
    private static partial Regex PromptRegex();

    [GeneratedRegex(@"password: ?\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex SshPasswordRegex();
}
