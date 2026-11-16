using System.Diagnostics;
using System.Text;
using Avalonia.Controls;
using Avalonia.Threading;
using Nexode.App.Localization;
using Nexode.App.Views;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.Services;

/// <summary>Resolves an entry into connection parameters: inheritance, decrypted password, "ask on connect".</summary>
public static class ConnectionBuilder
{
    /// <summary>Null when the user cancelled a prompt or the vault stayed locked.</summary>
    public static async Task<ConnectParams?> BuildAsync(Entry entry, Window owner, ViewModels.EntryActions actions, IReadOnlyDictionary<string, string>? overrides = null)
    {
        var ws = AppHost.Workspace;
        var eff = ws.Resolver.Resolve(entry);
        if (entry.Kind == EntryKind.Serial)
        {
            // Serial console: the "host" is the port; no credentials.
            var device = entry.Setting(SettingKeys.SerialPort) ?? entry.Host;
            if (string.IsNullOrWhiteSpace(device)) throw new InvalidOperationException(Loc.T("Serial.NoPort"));
            var serialSettings = new Dictionary<string, string>(entry.Settings);
            if (overrides is not null)
            {
                foreach (var (k, v) in overrides) serialSettings[k] = v;
            }

            return new ConnectParams { Host = device.Trim(), Port = 0, Settings = serialSettings };
        }

        var host = eff.Host ?? entry.Host;
        int? urlPort = null;
        string? url = null;
        if (EntryKinds.HasUrl(entry.Kind))
        {
            url = eff.Url ?? entry.Url;
            if (url is null || !Uri.TryCreate(url, UriKind.Absolute, out var u)) throw new InvalidOperationException(Loc.T("Toast.InvalidUrl"));
            host = u.Host;
            urlPort = u.Port;
        }

        if (string.IsNullOrWhiteSpace(host)) throw new InvalidOperationException(Loc.T("Session.NoHost"));

        string? user = eff.Username;
        string? password = null;
        // Web panels only need credentials for automatic login.
        var needsCredentials = entry.Kind != EntryKind.Web || entry.Setting(SettingKeys.WebBasicAuth) == "1" || entry.Setting(SettingKeys.WebFormFill) == "1";
        if (needsCredentials && eff.CredentialMode == CredentialMode.Prompt)
        {
            var creds = await Dialog.CredentialsAsync(owner, Loc.T("Session.Credentials"), $"{entry.Name} · {host}", user);
            if (creds is null) return null;
            (user, password) = creds.Value;
        }
        else if (needsCredentials && eff.SecretOwner is { } secretOwner && ws.HasSecret(secretOwner))
        {
            if (!await actions.EnsureUnlockedAsync()) return null;
            password = ws.GetSecret(secretOwner, Workspace.PasswordField);
        }

        // Key file: own setting, else the entry that supplies the credentials.
        var keyFile = entry.Setting(SettingKeys.SshKeyFile)
                      ?? (eff.CredentialFrom is { } from ? ws.Get(from)?.Setting(SettingKeys.SshKeyFile) : null);

        var settings = new Dictionary<string, string>(entry.Settings);
        if (overrides is not null)
        {
            foreach (var (k, v) in overrides) settings[k] = v;
        }

        if (settings.TryGetValue("session.user", out var su) && !string.IsNullOrWhiteSpace(su)) user = su;
        if (settings.TryGetValue("session.password", out var pwOverride) && !string.IsNullOrEmpty(pwOverride)) password = pwOverride;
        var port = settings.TryGetValue("session.port", out var po) && int.TryParse(po, out var pn) && pn is > 0 and < 65536 ? pn : urlPort ?? eff.Port ?? EntryKinds.DefaultPort(entry.Kind) ?? 0;
        if (url is not null) settings[SettingKeys.WebUrl] = url;
        if (entry.Kind == EntryKind.Ssh && settings.ContainsKey("session.kind")) port = eff.Port ?? 22;
        if (entry.Kind == EntryKind.PowerShell && entry.Port is null && !settings.ContainsKey("session.port"))
            port = Nexode.Protocols.Terminal.PowerShellLaunch.DefaultPort(settings.GetValueOrDefault(SettingKeys.PsTransport) ?? "winrm");
        // Switched-off features must not act behind the user's back.
        if (!Features.On(Features.SshAgent)) settings.Remove(SettingKeys.SshAgent);

        return new ConnectParams
        {
            Host = host.Trim(),
            Port = port,
            Username = string.IsNullOrEmpty(user) ? null : user,
            Password = password,
            Domain = eff.Domain,
            KeyFile = keyFile,
            Settings = settings
        };
    }
}

/// <summary>SSH jump host for an entry ("Connect via"): opens the tunnel and rewrites the connection parameters.</summary>
public static class Tunnels
{
    /// <summary>The jump host entry of <paramref name="entry"/>, if one is set and still exists.</summary>
    public static Entry? JumpHostOf(Entry entry) =>
        EntryKinds.CanTunnel(entry.Kind) && Guid.TryParse(entry.Setting(SettingKeys.TunnelVia), out var id) && AppHost.Workspace.Tree.Get(id) is { Kind: EntryKind.Ssh } jump && jump.Id != entry.Id
            ? jump
            : null;

    /// <summary>
    /// Opens the tunnel (credentials of the jump host are resolved like for any SSH connection). Host/port based
    /// protocols get 127.0.0.1:&lt;local port&gt; (the real target stays the identity for host key checks), WebDAV/S3 keep
    /// their URL and connect through the tunnel. Web panels get the tunnel only – the browser tab decides (proxy or forward).
    /// </summary>
    public static async Task<(ConnectParams Params, SshTunnel? Tunnel)> ApplyAsync(Entry entry, ConnectParams p, Window owner, ViewModels.EntryActions actions,
        ISessionPrompts prompts, CancellationToken ct)
    {
        if (JumpHostOf(entry) is not { } jump) return (p, null);
        var jp = await ConnectionBuilder.BuildAsync(jump, owner, actions) ?? throw new OperationCanceledException();
        var tunnel = await Task.Run(() => SshTunnel.OpenAsync(jp, prompts, AppHost.KnownHosts, ct), ct);
        try
        {
            if (entry.Kind == EntryKind.Web) return (p, tunnel);
            var settings = new Dictionary<string, string>(p.Settings);
            var local = tunnel.ForwardLocal(p.Host, p.Port);
            if (entry.Kind is EntryKind.WebDav or EntryKind.S3)
            {
                settings[ConnectParams.TunnelLocalKey] = local.ToString(System.Globalization.CultureInfo.InvariantCulture);
                return (p with { Settings = settings }, tunnel);
            }

            settings[ConnectParams.TunnelHostKey] = p.IdentityHost;
            settings[ConnectParams.TunnelPortKey] = p.IdentityPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
            return (p with { Host = "127.0.0.1", Port = local, Settings = settings }, tunnel);
        }
        catch
        {
            tunnel.Dispose();
            throw;
        }
    }

    private static readonly List<SshTunnel> Detached = [];

    /// <summary>Tunnels of external programs stay open until Nexode exits.</summary>
    public static void KeepUntilExit(SshTunnel tunnel)
    {
        lock (Detached) Detached.Add(tunnel);
    }

    public static void CloseAll()
    {
        lock (Detached)
        {
            foreach (var t in Detached) t.Dispose();
            Detached.Clear();
        }
    }
}

/// <summary>Session questions answered with dialogs on the UI thread (called from connection threads).</summary>
public sealed class SessionPrompts(Func<Window?> owner) : ISessionPrompts
{
    public Task<bool> ConfirmHostKeyAsync(HostKeyCheck check) =>
        Dispatcher.UIThread.InvokeAsync(async () => owner() is { } w && await Dialog.HostKeyAsync(w, check));

    public Task<string?> AskSecretAsync(string title, string prompt) =>
        Dispatcher.UIThread.InvokeAsync(async () => owner() is { } w
            ? await Dialog.PasswordAsync(w, title, prompt, confirm: false, minLength: 1, icon: "key-round")
            : null);
}

/// <summary>Starts connections in external programs (system RDP client, terminal, VNC viewer).</summary>
public static class ExternalLauncher
{
    public static async Task<string?> LaunchAsync(Entry entry, ConnectParams p)
    {
        try
        {
            return entry.Kind switch
            {
                EntryKind.Rdp => await RdpAsync(entry, p),
                EntryKind.Ssh => Terminal(SshCommand(p)),
                EntryKind.Telnet => Terminal($"telnet {p.Host} {p.Port}"),
                EntryKind.PowerShell => PowerShell(p),
                EntryKind.Sftp => Uri($"sftp://{UserPart(p)}{p.Host}:{p.Port}{p.Setting(SettingKeys.RemotePath)}"),
                EntryKind.Ftp => Uri($"{(p.Setting(SettingKeys.FtpEncryption) is "Implicit" ? "ftps" : "ftp")}://{UserPart(p)}{p.Host}:{p.Port}{p.Setting(SettingKeys.RemotePath)}"),
                EntryKind.Vnc => Uri($"vnc://{p.Host}:{p.Port}"),
                EntryKind.Serial => Terminal(OperatingSystem.IsWindows()
                    ? $"putty -serial {p.Host} -sercfg {p.Setting(SettingKeys.SerialBaud) ?? "9600"}"
                    : $"screen {p.Host} {p.Setting(SettingKeys.SerialBaud) ?? "9600"}"),
                _ => Loc.T("Session.NoExternal")
            };
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return ex.Message;
        }
    }

    /// <summary>PowerShell in its own console window; the password is asked there (Get-Credential), never passed on.</summary>
    private static string? PowerShell(ConnectParams p)
    {
        var windows = OperatingSystem.IsWindows();
        var exe = Nexode.Protocols.Terminal.PowerShellLaunch.FindExecutable(p, Which, windows, windows ? Environment.SystemDirectory : null);
        if (exe is null) return Loc.T(windows ? "Ps.NoPowerShell" : "Ps.NoPwsh");
        var plan = Nexode.Protocols.Terminal.PowerShellLaunch.Build(p with { Password = null }, exe);
        if (windows)
        {
            var psi = new ProcessStartInfo(plan.Executable) { UseShellExecute = false };
            foreach (var a in plan.Arguments) psi.ArgumentList.Add(a);
            Process.Start(psi);
            return null;
        }

        return Terminal(string.Join(' ', new[] { plan.Executable }.Concat(plan.Arguments).Select(Nexode.Protocols.Terminal.LocalProcessConnection.ShellQuote)));
    }

    private static string UserPart(ConnectParams p) => string.IsNullOrEmpty(p.Username) ? "" : System.Uri.EscapeDataString(p.Username) + "@";

    public static string SshCommand(ConnectParams p)
    {
        var sb = new StringBuilder("ssh ");
        if (p.Port != 22) sb.Append("-p ").Append(p.Port).Append(' ');
        if (p.KeyFile is { Length: > 0 } key) sb.Append("-i \"").Append(key).Append("\" ");
        sb.Append(UserPart(p).Replace("%40", "@")).Append(p.Host);
        return sb.ToString();
    }

    private static string? Uri(string uri)
    {
        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        return null;
    }

    /// <summary>Opens a command in the system terminal (Windows Terminal/cmd, common Linux terminals, macOS Terminal).</summary>
    public static string? Terminal(string command)
    {
        if (OperatingSystem.IsWindows())
        {
            var wt = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wt.exe");
            if (File.Exists(wt)) Process.Start(new ProcessStartInfo(wt) { ArgumentList = { "cmd", "/k", command }, UseShellExecute = false });
            else Process.Start(new ProcessStartInfo("cmd.exe") { ArgumentList = { "/k", command }, UseShellExecute = true });
            return null;
        }

        if (OperatingSystem.IsMacOS())
        {
            var script = $"tell application \"Terminal\" to do script \"{command.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"";
            Process.Start(new ProcessStartInfo("osascript") { ArgumentList = { "-e", script, "-e", "tell application \"Terminal\" to activate" } });
            return null;
        }

        foreach (var (exe, args) in new (string, string[])[]
                 {
                     ("x-terminal-emulator", ["-e"]), ("gnome-terminal", ["--"]), ("konsole", ["-e"]), ("xfce4-terminal", ["-x"]), ("xterm", ["-e"])
                 })
        {
            if (Which(exe) is not { } path) continue;
            var psi = new ProcessStartInfo(path);
            foreach (var a in args) psi.ArgumentList.Add(a);
            psi.ArgumentList.Add("sh");
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(command + "; exec $SHELL");
            Process.Start(psi);
            return null;
        }

        return Loc.T("Session.NoTerminal");
    }

    public static string? Which(string exe)
    {
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var candidate = Path.Combine(dir, exe);
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    /// <summary>
    /// RDP: Windows mstsc with a temporary .rdp file and a short-lived Credential Manager entry (no password on the command line);
    /// Linux FreeRDP with the password on stdin; macOS the registered .rdp handler (Windows App).
    /// </summary>
    private static async Task<string?> RdpAsync(Entry entry, ConnectParams p)
    {
        var hyperV = p.Setting(SettingKeys.RdpHyperVId) is { Length: > 0 } vmId ? vmId : null;
        var port = hyperV is not null && p.Port == 3389 ? 2179 : p.Port;
        var address = port == 3389 ? p.Host : $"{p.Host}:{port}";
        var user = string.IsNullOrEmpty(p.Domain) || p.Username?.Contains('@') == true || p.Username?.Contains('\\') == true ? p.Username : $@"{p.Domain}\{p.Username}";
        if (OperatingSystem.IsLinux())
        {
            var exe = Which("xfreerdp3") ?? Which("xfreerdp") ?? Which("wlfreerdp") ?? Which("sdl-freerdp");
            if (exe is null) return Loc.T("Session.NoFreeRdp");
            var psi = new ProcessStartInfo(exe) { RedirectStandardInput = !string.IsNullOrEmpty(p.Password), UseShellExecute = false };
            psi.ArgumentList.Add("/v:" + (hyperV is null ? address : p.Host));
            if (hyperV is not null) psi.ArgumentList.Add("/vmconnect:" + hyperV);
            if (!string.IsNullOrEmpty(p.Username)) psi.ArgumentList.Add("/u:" + p.Username);
            if (!string.IsNullOrEmpty(p.Domain)) psi.ArgumentList.Add("/d:" + p.Domain);
            if (p.Flag(SettingKeys.RdpFullscreen))
            {
                psi.ArgumentList.Add("/f");
                if (int.TryParse(p.Setting(SettingKeys.RdpMonitor), out var mon) && mon > 0) psi.ArgumentList.Add("/monitors:" + (mon - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
            else if (p.Flag(SettingKeys.RdpSpan))
            {
                psi.ArgumentList.Add("/f");
                psi.ArgumentList.Add("/multimon");
                if (MonitorList(p) is { } monitors) psi.ArgumentList.Add("/monitors:" + monitors);
            }
            else
            {
                psi.ArgumentList.Add("/dynamic-resolution");
            }
            var v3 = FreeRdpMajor(exe) >= 3;
            if (p.Setting(SettingKeys.RdpClipboard) == "0") psi.ArgumentList.Add("-clipboard");
            else if (p.Setting(SettingKeys.RdpClipboardFiles) == "0" && v3) psi.ArgumentList.Add("/clipboard:files-to:off");
            else psi.ArgumentList.Add("+clipboard");
            if (p.Setting(SettingKeys.RdpRemoteApp) is { Length: > 0 } app)
            {
                var args = p.Setting(SettingKeys.RdpRemoteAppArgs);
                if (v3)
                {
                    psi.ArgumentList.Add($"/app:program:{app},name:{entry.Name}" + (string.IsNullOrWhiteSpace(args) ? "" : $",cmd:{args}"));
                }
                else
                {
                    psi.ArgumentList.Add("/app:" + app);
                    psi.ArgumentList.Add("/app-name:" + entry.Name);
                    if (!string.IsNullOrWhiteSpace(args)) psi.ArgumentList.Add("/app-cmd:" + args);
                }
            }
            if (p.Flag(SettingKeys.RdpDrives)) psi.ArgumentList.Add("/drive:home,~");
            if (p.Flag(SettingKeys.RdpPrinters)) psi.ArgumentList.Add("/printer");
            if (p.Flag(SettingKeys.RdpSmartCards)) psi.ArgumentList.Add("/smartcard");
            psi.ArgumentList.Add("/cert:tofu");
            if (p.Flag(SettingKeys.RdpAdmin)) psi.ArgumentList.Add("/admin");
            if (p.Setting(SettingKeys.RdpGateway) is { Length: > 0 } gw)
            {
                // Shared credentials: FreeRDP reuses /u /d /p for the gateway unless gateway credentials are given separately.
                psi.ArgumentList.Add($"/gateway:g:{gw}" + (p.Flag(SettingKeys.RdpGatewayBypass) ? ",bypass-local:on" : "") +
                                     (p.Setting(SettingKeys.RdpGatewaySameCreds) == "0" ? "" : ",use-tsg:auto"));
            }
            if (!string.IsNullOrEmpty(p.Password)) psi.ArgumentList.Add("/from-stdin");
            var proc = Process.Start(psi);
            if (proc is not null && !string.IsNullOrEmpty(p.Password))
            {
                await proc.StandardInput.WriteLineAsync(p.Password);
                proc.StandardInput.Close();
            }

            return null;
        }

        var file = Path.Combine(Path.GetTempPath(), $"nexode-{Guid.NewGuid():N}.rdp");
        await File.WriteAllTextAsync(file, RdpFile(entry, p, address, user), Encoding.Unicode);
        if (OperatingSystem.IsWindows())
        {
            if (!string.IsNullOrEmpty(p.Password) && !string.IsNullOrEmpty(user)) Platform.WindowsCredentials.WriteTemporary("TERMSRV/" + p.Host, user, p.Password, TimeSpan.FromSeconds(45));
            Process.Start(new ProcessStartInfo("mstsc.exe") { ArgumentList = { file }, UseShellExecute = false });
        }
        else
        {
            Process.Start(new ProcessStartInfo("open") { ArgumentList = { file } });
        }

        // The client reads the file right away; remove it shortly after.
        _ = Task.Delay(TimeSpan.FromSeconds(60)).ContinueWith(_ =>
        {
            try { File.Delete(file); } catch (IOException) { }
        }, TaskScheduler.Default);
        return null;
    }

    /// <summary>User sessions of a Remote Desktop server (qwinsta with the current Windows logon); error text instead on failure.</summary>
    public static async Task<(IReadOnlyList<Nexode.Core.Rdp.RdpUserSession> Sessions, string? Error)> QueryRdpSessionsAsync(string host)
    {
        if (!OperatingSystem.IsWindows()) return ([], Loc.T("Shadow.WindowsOnly"));
        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            var oem = System.Text.Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            var psi = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "qwinsta.exe"))
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = oem,
                StandardErrorEncoding = oem,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            psi.ArgumentList.Add("/server:" + host);
            using var proc = Process.Start(psi);
            if (proc is null) return ([], "qwinsta");
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var output = proc.StandardOutput.ReadToEndAsync(cts.Token);
            var error = proc.StandardError.ReadToEndAsync(cts.Token);
            await proc.WaitForExitAsync(cts.Token);
            var sessions = Nexode.Core.Rdp.RdpSessions.ParseQwinsta(await output);
            var err = (await error).Trim();
            return (sessions, sessions.Count == 0 ? (err.Length > 0 ? err : Loc.T("Shadow.NoSessions")) : null);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException or OperationCanceledException)
        {
            return ([], ex.Message);
        }
    }

    /// <summary>
    /// Shadows (watches or controls) a user's session with mstsc /shadow. Credentials go through a short-lived Credential
    /// Manager entry like a normal connection. The user is asked for consent unless the server's policy allows otherwise.
    /// </summary>
    public static string? Shadow(ConnectParams p, int sessionId, bool control, bool noConsent)
    {
        if (!OperatingSystem.IsWindows()) return Loc.T("Shadow.WindowsOnly");
        var address = p.Port == 3389 ? p.Host : $"{p.Host}:{p.Port}";
        var user = string.IsNullOrEmpty(p.Domain) || p.Username?.Contains('@') == true || p.Username?.Contains('\\') == true ? p.Username : $@"{p.Domain}\{p.Username}";
        if (!string.IsNullOrEmpty(p.Password) && !string.IsNullOrEmpty(user)) Platform.WindowsCredentials.WriteTemporary("TERMSRV/" + p.Host, user, p.Password, TimeSpan.FromSeconds(45));
        var psi = new ProcessStartInfo("mstsc.exe") { UseShellExecute = false };
        psi.ArgumentList.Add("/v:" + address);
        psi.ArgumentList.Add("/shadow:" + sessionId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        if (control) psi.ArgumentList.Add("/control");
        if (noConsent) psi.ArgumentList.Add("/noConsentPrompt");
        Process.Start(psi);
        return null;
    }

    /// <summary>"0,2" from the entry's monitor selection (numbers only, sorted, no duplicates); null = all monitors.</summary>
    public static string? MonitorList(ConnectParams p)
    {
        var ids = (p.Setting(SettingKeys.RdpMonitors) ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : -1)
            .Where(n => n is >= 0 and < 32).Distinct().Order().ToList();
        return ids.Count == 0 ? null : string.Join(',', ids);
    }

    private static readonly Dictionary<string, int> FreeRdpVersions = [];

    /// <summary>Major version of a FreeRDP binary ("This is FreeRDP version 3.x"); 2 when unknown.</summary>
    public static int FreeRdpMajor(string exe)
    {
        lock (FreeRdpVersions)
        {
            if (FreeRdpVersions.TryGetValue(exe, out var cached)) return cached;
        }

        var major = Path.GetFileName(exe).EndsWith('3') ? 3 : 2;
        try
        {
            using var proc = Process.Start(new ProcessStartInfo(exe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false });
            if (proc is not null && proc.WaitForExit(2000))
            {
                var text = proc.StandardOutput.ReadToEnd();
                var m = System.Text.RegularExpressions.Regex.Match(text, @"version (\d+)\.");
                if (m.Success) major = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
        }

        lock (FreeRdpVersions) FreeRdpVersions[exe] = major;
        return major;
    }

    public static string RdpFile(Entry entry, ConnectParams p, string address, string? user)
    {
        var sb = new StringBuilder();
        void Line(string s) => sb.Append(s).Append("\r\n");
        Line("full address:s:" + address);
        if (!string.IsNullOrEmpty(user)) Line("username:s:" + user);
        var res = p.Setting(SettingKeys.RdpResolution);
        if (p.Flag(SettingKeys.RdpFullscreen))
        {
            Line("screen mode id:i:2");
            if (int.TryParse(p.Setting(SettingKeys.RdpMonitor), out var monitor) && monitor > 0)
            {
                // mstsc numbers monitors from 0 (see "mstsc /l").
                Line("use multimon:i:0");
                Line("selectedmonitors:s:" + (monitor - 1).ToString(System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        else if (p.Flag(SettingKeys.RdpSpan))
        {
            Line("screen mode id:i:2");
            Line("use multimon:i:1");
            if (MonitorList(p) is { } monitors) Line("selectedmonitors:s:" + monitors);
        }
        else if (res is { } r && r.Split('x') is [var w, var h])
        {
            Line("screen mode id:i:1");
            Line("desktopwidth:i:" + w);
            Line("desktopheight:i:" + h);
        }
        else
        {
            Line("screen mode id:i:2");
            Line("dynamic resolution:i:1");
        }

        Line("session bpp:i:" + (p.Setting(SettingKeys.RdpColorDepth) ?? "32"));
        Line("administrative session:i:" + (p.Flag(SettingKeys.RdpAdmin) ? 1 : 0));
        Line("redirectclipboard:i:" + (p.Setting(SettingKeys.RdpClipboard) == "0" ? 0 : 1));
        Line("redirectprinters:i:" + (p.Flag(SettingKeys.RdpPrinters) ? 1 : 0));
        if (p.Flag(SettingKeys.RdpDrives)) Line("drivestoredirect:s:*");
        Line("redirectsmartcards:i:" + (p.Flag(SettingKeys.RdpSmartCards) ? 1 : 0));
        Line("audiomode:i:" + (p.Setting(SettingKeys.RdpAudio) ?? "0"));
        Line("prompt for credentials:i:0");
        Line("authentication level:i:2");
        Line("enablecredsspsupport:i:1");
        if (p.Setting(SettingKeys.RdpGateway) is { Length: > 0 } gw)
        {
            Line("gatewayhostname:s:" + gw);
            Line("gatewayusagemethod:i:" + (p.Flag(SettingKeys.RdpGatewayBypass) ? 2 : 1));
            Line("gatewayprofileusagemethod:i:1");
            Line("gatewaycredentialssource:i:4");
            Line("promptcredentialonce:i:" + (p.Setting(SettingKeys.RdpGatewaySameCreds) == "0" ? 0 : 1));
        }

        if (p.Setting(SettingKeys.RdpHyperVId) is { Length: > 0 } vm)
        {
            // Hyper-V console through the host (like vmconnect); enhanced session where the VM supports it.
            Line("pcb:s:" + vm + ";EnhancedMode=1");
            Line("server port:i:2179");
            Line("negotiate security layer:i:0");
        }

        if (p.Setting(SettingKeys.RdpRemoteApp) is { Length: > 0 } app)
        {
            // RemoteApp: only the program's windows, seamless on the local desktop.
            Line("remoteapplicationmode:i:1");
            Line("remoteapplicationprogram:s:" + app);
            Line("remoteapplicationname:s:" + entry.Name);
            if (p.Setting(SettingKeys.RdpRemoteAppArgs) is { Length: > 0 } args)
            {
                Line("remoteapplicationcmdline:s:" + args);
                Line("remoteapplicationexpandcmdline:i:1");
            }

            Line("disableremoteappcapscheck:i:1");
            Line("alternate shell:s:rdpinit.exe");
        }

        return sb.ToString();
    }
}
