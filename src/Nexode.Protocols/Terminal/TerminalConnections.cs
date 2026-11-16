using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Nexode.Core.Model;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nexode.Protocols.Terminal;

/// <summary>Byte stream behind a terminal tab (SSH shell, Telnet).</summary>
public interface ITerminalConnection : IDisposable
{
    /// <summary>Data from the host; raised on a background thread.</summary>
    event Action<byte[]>? DataReceived;

    /// <summary>Connection ended; null = normal close, otherwise the reason.</summary>
    event Action<string?>? Closed;

    bool IsConnected { get; }

    Task ConnectAsync(int columns, int rows, CancellationToken ct);
    void Send(byte[] data);
    void Resize(int columns, int rows);
}

/// <summary>Interactive SSH shell (SSH.NET): password, keyboard-interactive and key authentication, TOFU host keys.</summary>
public sealed class SshTerminalConnection(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts) : ITerminalConnection
{
    private SshClient? _client;
    private ShellStream? _shell;
    private int _closed;

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public bool IsConnected => _client?.IsConnected == true && _shell is not null;

    public async Task ConnectAsync(int columns, int rows, CancellationToken ct)
    {
        var info = await SshConnections.CreateConnectionInfoAsync(p, prompts).ConfigureAwait(false);
        _client = new SshClient(info);
        SshConnections.AttachHostKeyCheck(_client, p, prompts, knownHosts);
        var keepAlive = int.TryParse(p.Setting(SettingKeys.SshKeepAlive), out var ka) && ka > 0 ? ka : 30;
        _client.KeepAliveInterval = TimeSpan.FromSeconds(keepAlive);
        await _client.ConnectAsync(ct).ConfigureAwait(false);

        var modes = new Dictionary<TerminalModes, uint> { [TerminalModes.ECHO] = 1, [TerminalModes.IUTF8] = 1 };
        _shell = _client.CreateShellStream(p.Setting(SettingKeys.SshTerminalType) ?? "xterm-256color", (uint)columns, (uint)rows, 0, 0, 64 * 1024, modes);
        _shell.Closed += (_, _) => OnClosed(null);
        // Read loop (the stream buffers internally, so it must be drained rather than using its event).
        var shell = _shell;
        new Thread(() => ReadLoop(shell)) { IsBackground = true, Name = "ssh-read" }.Start();
        _client.ErrorOccurred += (_, e) => OnClosed(NetErrors.Describe(e.Exception));
        if (p.Setting(SettingKeys.SshStartCommand) is { } start) Send(Encoding.UTF8.GetBytes(start + "\r"));
    }

    private void ReadLoop(ShellStream shell)
    {
        var buf = new byte[32768];
        try
        {
            while (true)
            {
                var n = shell.Read(buf, 0, buf.Length);
                if (n <= 0) break;
                DataReceived?.Invoke(buf.AsSpan(0, n).ToArray());
            }

            OnClosed(null);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or IOException or SocketException)
        {
            OnClosed(_closed == 1 ? null : ex.Message);
        }
    }

    public void Send(byte[] data)
    {
        try
        {
            _shell?.Write(data, 0, data.Length);
            _shell?.Flush();
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or SocketException or IOException)
        {
            OnClosed(ex.Message);
        }
    }

    public void Resize(int columns, int rows)
    {
        try
        {
            _shell?.ChangeWindowSize((uint)columns, (uint)rows, 0, 0);
        }
        catch (Exception ex) when (ex is ObjectDisposedException or SshException or InvalidOperationException)
        {
        }
    }

    private void OnClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _closed = 1;
        try { _shell?.Dispose(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException) { }
        try { _client?.Disconnect(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException) { }
        _client?.Dispose();
    }
}

/// <summary>Shared SSH setup for shell and SFTP.</summary>
public static class SshConnections
{
    public static async Task<ConnectionInfo> CreateConnectionInfoAsync(ConnectParams p, ISessionPrompts prompts)
    {
        var user = string.IsNullOrEmpty(p.Username) ? Environment.UserName : p.Username;
        var methods = new List<AuthenticationMethod>();

        if (p.KeyFile is { Length: > 0 } keyPath)
        {
            var path = Environment.ExpandEnvironmentVariables(keyPath.StartsWith('~') ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), keyPath[1..].TrimStart('/', '\\')) : keyPath);
            if (!File.Exists(path)) throw new FileNotFoundException($"Key file not found: {path}");
            PrivateKeyFile key;
            try
            {
                key = new PrivateKeyFile(path, p.Password);
            }
            catch (Exception ex) when (ex is SshPassPhraseNullOrEmptyException or SshException or InvalidOperationException)
            {
                var pass = await prompts.AskSecretAsync("SSH", $"Passphrase for {Path.GetFileName(path)}").ConfigureAwait(false)
                           ?? throw new OperationCanceledException();
                key = new PrivateKeyFile(path, pass);
            }

            methods.Add(new PrivateKeyAuthenticationMethod(user, key));
        }

        // SSH agent: the private keys never leave the agent; tried before the password.
        if (p.Flag(Core.Model.SettingKeys.SshAgent) && SshAgent.TryOpen() is { } agentKeys)
        {
            methods.Add(new PrivateKeyAuthenticationMethod(user, agentKeys));
        }

        if (!string.IsNullOrEmpty(p.Password))
        {
            methods.Add(new PasswordAuthenticationMethod(user, p.Password));
        }

        // Keyboard-interactive: answers password prompts with the stored password, asks for everything else (OTP).
        var ki = new KeyboardInteractiveAuthenticationMethod(user);
        ki.AuthenticationPrompt += (_, e) =>
        {
            foreach (var prompt in e.Prompts)
            {
                if (!string.IsNullOrEmpty(p.Password) && prompt.Request.Contains("assword", StringComparison.OrdinalIgnoreCase))
                {
                    prompt.Response = p.Password;
                    continue;
                }

                prompt.Response = prompts.AskSecretAsync("SSH", string.IsNullOrWhiteSpace(prompt.Request) ? e.Instruction : prompt.Request.Trim())
                    .GetAwaiter().GetResult() ?? "";
            }
        };
        methods.Add(ki);

        return new ConnectionInfo(p.Host, p.Port, user, [.. methods]) { Timeout = p.Timeout };
    }

    /// <summary>Host key verification (blocks the connecting background thread while the user decides).</summary>
    public static void AttachHostKeyCheck(BaseClient client, ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts)
    {
        client.HostKeyReceived += (_, e) =>
        {
            e.CanTrust = knownHosts.VerifyAsync("ssh", p.IdentityHost, p.IdentityPort, e.HostKeyName, "SHA256:" + e.FingerPrintSHA256, prompts).GetAwaiter().GetResult();
        };
    }
}

/// <summary>Telnet client (RFC 854) with NAWS, terminal type and optional automatic login.</summary>
public sealed partial class TelnetConnection(ConnectParams p) : ITerminalConnection
{
    private const byte Iac = 255, Dont = 254, Do = 253, Wont = 252, Will = 251, Sb = 250, Se = 240;
    private const byte OptEcho = 1, OptSga = 3, OptTtype = 24, OptNaws = 31;

    private TcpClient? _tcp;
    private NetworkStream? _stream;
    private CancellationTokenSource? _cts;
    private int _cols = 80, _rows = 24;
    private bool _naws;
    private int _closed;
    private int _loginStep;
    private readonly StringBuilder _recent = new();

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public bool IsConnected => _tcp?.Connected == true;

    public async Task ConnectAsync(int columns, int rows, CancellationToken ct)
    {
        _cols = columns;
        _rows = rows;
        _tcp = await NetErrors.ConnectAsync(p.Host, p.Port, p.Timeout, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        _cts = new CancellationTokenSource();
        _loginStep = p.Flag(SettingKeys.TelnetAutoLogin) && !string.IsNullOrEmpty(p.Username) ? 1 : 0;
        _ = Task.Run(() => ReadLoop(_cts.Token));
    }

    private async Task ReadLoop(CancellationToken ct)
    {
        var buf = new byte[16384];
        var output = new List<byte>(16384);
        var sub = new List<byte>();
        var state = 0; // 0 data, 1 IAC, 2 option verb, 3 SB, 4 SB IAC
        byte verb = 0;
        try
        {
            while (!ct.IsCancellationRequested && _stream is { } s)
            {
                var n = await s.ReadAsync(buf, ct).ConfigureAwait(false);
                if (n == 0) break;
                output.Clear();
                for (var i = 0; i < n; i++)
                {
                    var b = buf[i];
                    switch (state)
                    {
                        case 0:
                            if (b == Iac) state = 1;
                            else output.Add(b);
                            break;
                        case 1:
                            if (b == Iac)
                            {
                                output.Add(Iac);
                                state = 0;
                            }
                            else if (b is Do or Dont or Will or Wont)
                            {
                                verb = b;
                                state = 2;
                            }
                            else if (b == Sb)
                            {
                                sub.Clear();
                                state = 3;
                            }
                            else
                            {
                                state = 0;
                            }

                            break;
                        case 2:
                            Negotiate(verb, b);
                            state = 0;
                            break;
                        case 3:
                            if (b == Iac) state = 4;
                            else if (sub.Count < 512) sub.Add(b);
                            break;
                        case 4:
                            if (b == Se)
                            {
                                Subnegotiation(sub);
                                state = 0;
                            }
                            else
                            {
                                sub.Add(b);
                                state = 3;
                            }

                            break;
                    }
                }

                if (output.Count > 0)
                {
                    var data = output.ToArray();
                    DataReceived?.Invoke(data);
                    AutoLogin(data);
                }
            }

            OnClosed(null);
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
        {
            OnClosed(ct.IsCancellationRequested ? null : NetErrors.Describe(ex));
        }
    }

    private void Negotiate(byte verb, byte option)
    {
        switch (verb)
        {
            case Do when option == OptTtype:
                Raw(Iac, Will, OptTtype);
                break;
            case Do when option == OptNaws:
                _naws = true;
                Raw(Iac, Will, OptNaws);
                SendSize();
                break;
            case Do when option == OptSga:
                Raw(Iac, Will, OptSga);
                break;
            case Do:
                Raw(Iac, Wont, option);
                break;
            case Will when option is OptEcho or OptSga:
                Raw(Iac, Do, option);
                break;
            case Will:
                Raw(Iac, Dont, option);
                break;
        }
    }

    private void Subnegotiation(List<byte> sub)
    {
        if (sub.Count >= 2 && sub[0] == OptTtype && sub[1] == 1)
        {
            var name = Encoding.ASCII.GetBytes(p.Setting(SettingKeys.SshTerminalType) ?? "xterm-256color");
            Raw([Iac, Sb, OptTtype, 0, .. name, Iac, Se]);
        }
    }

    private void SendSize()
    {
        if (!_naws) return;
        byte[] size = [(byte)(_cols >> 8), (byte)_cols, (byte)(_rows >> 8), (byte)_rows];
        var payload = new List<byte> { Iac, Sb, OptNaws };
        foreach (var b in size)
        {
            payload.Add(b);
            if (b == Iac) payload.Add(Iac);
        }

        payload.Add(Iac);
        payload.Add(Se);
        Raw([.. payload]);
    }

    [GeneratedRegex(@"(login|user ?name|user)\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex LoginPrompt();

    [GeneratedRegex(@"password\s*:\s*$", RegexOptions.IgnoreCase)]
    private static partial Regex PasswordPrompt();

    private void AutoLogin(byte[] data)
    {
        if (_loginStep == 0) return;
        _recent.Append(Encoding.UTF8.GetString(data));
        if (_recent.Length > 400) _recent.Remove(0, _recent.Length - 400);
        var text = _recent.ToString().TrimEnd(' ', '\0');
        if (_loginStep == 1 && LoginPrompt().IsMatch(text))
        {
            Send(Encoding.UTF8.GetBytes(p.Username + "\r"));
            _recent.Clear();
            _loginStep = string.IsNullOrEmpty(p.Password) ? 0 : 2;
        }
        else if (_loginStep is 1 or 2 && PasswordPrompt().IsMatch(text) && !string.IsNullOrEmpty(p.Password))
        {
            Send(Encoding.UTF8.GetBytes(p.Password + "\r"));
            _recent.Clear();
            _loginStep = 0;
        }
    }

    public void Send(byte[] data)
    {
        // Escape IAC; a lone CR becomes CR NUL (NVT).
        var list = new List<byte>(data.Length + 8);
        for (var i = 0; i < data.Length; i++)
        {
            var b = data[i];
            list.Add(b);
            if (b == Iac) list.Add(Iac);
            else if (b == '\r' && (i + 1 >= data.Length || data[i + 1] != '\n')) list.Add(0);
        }

        Raw([.. list]);
    }

    private void Raw(params byte[] bytes)
    {
        try
        {
            lock (this) _stream?.Write(bytes);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            OnClosed(NetErrors.Describe(ex));
        }
    }

    public void Resize(int columns, int rows)
    {
        _cols = columns;
        _rows = rows;
        SendSize();
    }

    private void OnClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _closed = 1;
        _cts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
    }
}
