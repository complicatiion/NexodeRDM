using System.Diagnostics;
using System.Text;
using Nexode.Core.Model;
using Nexode.Protocols.Terminal;
using Renci.SshNet;

namespace Nexode.Protocols;

/// <summary>
/// Reads <see cref="HostInfo"/> from a host: over SSH (POSIX sh, falls back to PowerShell for Windows OpenSSH) or over
/// PowerShell remoting (WinRM, Invoke-Command in a local PowerShell). Only read-only commands run on the host.
/// </summary>
public static class HostProbe
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(40);

    /// <summary>The POSIX script wrapped for any login shell (bash, zsh, fish, csh all start /bin/sh with it).</summary>
    public static string PosixCommand => "/bin/sh -c " + ShQuote(HostInfo.PosixScript.Replace("\r", "", StringComparison.Ordinal));

    /// <summary>The Windows script as an encoded PowerShell call (cmd.exe and PowerShell as login shell).</summary>
    public static string WindowsCommand =>
        "powershell -NoLogo -NoProfile -NonInteractive -EncodedCommand " + Convert.ToBase64String(Encoding.Unicode.GetBytes(HostInfo.WindowsScript));

    public static async Task<HostInfo> SshAsync(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts, CancellationToken ct)
    {
        var info = await SshConnections.CreateConnectionInfoAsync(p, prompts).ConfigureAwait(false);
        using var client = new SshClient(info);
        SshConnections.AttachHostKeyCheck(client, p, prompts, knownHosts);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            var (output, error) = await RunAsync(client, PosixCommand, ct).ConfigureAwait(false);
            if (HostInfo.Parse(output, "ssh") is { } posix) return posix;
            // Windows OpenSSH: cmd.exe or PowerShell as login shell, no /bin/sh.
            var (psOut, psErr) = await RunAsync(client, WindowsCommand, ct).ConfigureAwait(false);
            return HostInfo.Parse(psOut, "ssh-powershell") ?? throw new InvalidOperationException(FirstLine(psErr) ?? FirstLine(error) ?? "No system information in the output.");
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static async Task<(string Output, string Error)> RunAsync(SshClient client, string command, CancellationToken ct)
    {
        using var cmd = client.CreateCommand(command);
        cmd.CommandTimeout = CommandTimeout;
        var output = await Task.Run(cmd.Execute, ct).ConfigureAwait(false);
        return (output, cmd.Error ?? "");
    }

    /// <summary>
    /// The local PowerShell script for WinRM: reads the password from standard input (never on the command line) and
    /// runs <see cref="HostInfo.WindowsScript"/> with Invoke-Command.
    /// </summary>
    public static string WinRmScript(ConnectParams p, bool withPassword)
    {
        var transport = PowerShellLaunch.Transport(p);
        var port = p.Port > 0 ? p.Port : PowerShellLaunch.DefaultPort(transport);
        var user = PowerShellLaunch.UserName(p);
        var sb = new StringBuilder();
        sb.AppendLine("$ErrorActionPreference = 'Stop'");
        sb.AppendLine("[Console]::OutputEncoding = [Text.Encoding]::UTF8");
        var args = new StringBuilder();
        args.Append(" -ComputerName ").Append(PowerShellLaunch.Quote(p.Host)).Append(" -Port ").Append(port);
        if (transport == "winrm-ssl") args.Append(" -UseSSL");
        if (user is not null && withPassword)
        {
            sb.AppendLine("$nxSecret = New-Object System.Security.SecureString");
            sb.AppendLine("foreach ($ch in [Console]::In.ReadLine().ToCharArray()) { $nxSecret.AppendChar($ch) }");
            sb.Append("$nxCred = New-Object System.Management.Automation.PSCredential(").Append(PowerShellLaunch.Quote(user)).AppendLine(", $nxSecret)");
            args.Append(" -Credential $nxCred");
        }

        if (p.Setting(SettingKeys.PsAuth) is { } auth && auth is "Negotiate" or "Kerberos" or "CredSSP") args.Append(" -Authentication ").Append(auth);
        if (p.Setting(SettingKeys.PsConfiguration) is { } config) args.Append(" -ConfigurationName ").Append(PowerShellLaunch.Quote(config));
        if (p.Flag(SettingKeys.PsSkipCertCheck) && transport == "winrm-ssl")
        {
            sb.AppendLine("$nxOptions = New-PSSessionOption -SkipCACheck -SkipCNCheck -SkipRevocationCheck");
            args.Append(" -SessionOption $nxOptions");
        }

        sb.Append("Invoke-Command").Append(args).AppendLine(" -ScriptBlock {");
        sb.AppendLine(HostInfo.WindowsScript);
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Runs <see cref="WinRmScript"/> in the given local PowerShell.</summary>
    public static async Task<HostInfo> WinRmAsync(ConnectParams p, string powershell, CancellationToken ct)
    {
        var withPassword = !string.IsNullOrEmpty(p.Password) && PowerShellLaunch.UserName(p) is not null;
        var psi = new ProcessStartInfo(powershell)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };
        foreach (var a in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(WinRmScript(p, withPassword))) })
            psi.ArgumentList.Add(a);

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("PowerShell could not be started.");
        if (withPassword) await proc.StandardInput.WriteLineAsync(p.Password.AsMemory(), ct).ConfigureAwait(false);
        proc.StandardInput.Close();
        var stdout = proc.StandardOutput.ReadToEndAsync(ct);
        var stderr = proc.StandardError.ReadToEndAsync(ct);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(CommandTimeout + TimeSpan.FromSeconds(20));
        try
        {
            await proc.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(true); } catch (InvalidOperationException) { }
            if (ct.IsCancellationRequested) throw;
            throw new TimeoutException("PowerShell remoting did not answer in time.");
        }

        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        return HostInfo.Parse(output, "winrm") ?? throw new InvalidOperationException(FirstLine(error) ?? "No system information in the output.");
    }

    private static string? FirstLine(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault(l => !l.StartsWith('+') && !l.StartsWith("At line", StringComparison.Ordinal));

    /// <summary>POSIX single-quoted string.</summary>
    public static string ShQuote(string s) => "'" + s.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
