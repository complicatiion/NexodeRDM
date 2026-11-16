using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Nexode.Protocols.Server;

/// <summary>Settings of the local FTP server.</summary>
public sealed record FtpServerOptions
{
    /// <summary>Shared folder; nothing outside it can be reached.</summary>
    public required string Root { get; init; }

    public int Port { get; init; } = 2121;

    /// <summary>Address to listen on (default: all IPv4 interfaces).</summary>
    public IPAddress Bind { get; init; } = IPAddress.Any;

    /// <summary>User name; null = anonymous login (then always read-only).</summary>
    public string? User { get; init; } = "nexode";

    public string? Password { get; init; }
    public bool ReadOnly { get; init; } = true;

    /// <summary>FTPS (explicit, AUTH TLS) required before login; null certificate = plain FTP.</summary>
    public X509Certificate2? Certificate { get; init; }

    /// <summary>Passive data ports (both inclusive); 0/0 = any free port.</summary>
    public int PassiveFrom { get; init; } = 50000;

    public int PassiveTo { get; init; } = 50100;
    public int MaxClients { get; init; } = 16;
}

/// <summary>Something happened on the server (connection, login, transfer); for the UI and the log.</summary>
public sealed record FtpServerEvent(DateTime Utc, string Client, string Kind, string Detail);

/// <summary>
/// Small FTP / FTPS server to hand files from this computer to others in the LAN (e.g. firmware to a switch, logs to a
/// colleague). One shared folder; passive mode only; login with a user and password (or anonymous, read-only);
/// optional explicit TLS that is then required for login and data. Paths are confined to the folder (no "..", no links
/// pointing outside). Failed logins are slowed down; a client is dropped after five.
/// </summary>
public sealed class LocalFtpServer : IDisposable
{
    private readonly FtpServerOptions _o;
    private readonly string _root;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _clients;
    private int _nextPassive;

    public LocalFtpServer(FtpServerOptions options)
    {
        _o = options;
        _root = Path.GetFullPath(options.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(_root)) throw new DirectoryNotFoundException(_root);
        _nextPassive = options.PassiveFrom;
    }

    public event Action<FtpServerEvent>? Activity;

    public bool IsRunning => _listener is not null;
    public int Clients => Volatile.Read(ref _clients);
    public int Port => (_listener?.LocalEndpoint as IPEndPoint)?.Port ?? _o.Port;

    public void Start()
    {
        _listener = new TcpListener(_o.Bind, _o.Port);
        _listener.Start();
        _cts = new CancellationTokenSource();
        _ = AcceptLoop(_listener, _cts.Token);
        Report("-", "start", $"{Port}");
    }

    public void Stop()
    {
        _cts?.Cancel();
        _listener?.Stop();
        _listener = null;
        Report("-", "stop", "");
    }

    public void Dispose() => Stop();

    /// <summary>Self-signed certificate for FTPS (RSA 2048, 5 years), stored as PKCS#12 so clients see the same fingerprint every time.</summary>
    public static X509Certificate2 LoadOrCreateCertificate(string pfxPath)
    {
        if (File.Exists(pfxPath))
        {
            try
            {
                return X509CertificateLoader.LoadPkcs12FromFile(pfxPath, null);
            }
            catch (CryptographicException)
            {
                // Damaged: make a new one.
            }
        }

        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest($"CN={Environment.MachineName} (Nexode FTP)", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, false));
        req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], false));
        var san = new SubjectAlternativeNameBuilder();
        san.AddDnsName(Environment.MachineName);
        san.AddDnsName("localhost");
        req.CertificateExtensions.Add(san.Build());
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(5));
        var pfx = cert.Export(X509ContentType.Pkcs12);
        Directory.CreateDirectory(Path.GetDirectoryName(pfxPath)!);
        File.WriteAllBytes(pfxPath, pfx);
        return X509CertificateLoader.LoadPkcs12(pfx, null);
    }

    /// <summary>The computer's IPv4 addresses in the LAN (for "ftp://address:port" hints).</summary>
    public static IReadOnlyList<IPAddress> LocalAddresses() =>
        System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up &&
                        n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct()
            .ToList();

    private void Report(string client, string kind, string detail) => Activity?.Invoke(new FtpServerEvent(DateTime.UtcNow, client, kind, detail));

    private async Task AcceptLoop(TcpListener listener, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            if (Interlocked.Increment(ref _clients) > _o.MaxClients)
            {
                Interlocked.Decrement(ref _clients);
                try
                {
                    var bytes = Encoding.ASCII.GetBytes("421 Too many connections.\r\n");
                    await client.GetStream().WriteAsync(bytes, ct).ConfigureAwait(false);
                }
                catch (IOException)
                {
                }

                client.Dispose();
                continue;
            }

            _ = Task.Run(async () =>
            {
                var who = (client.Client.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "?";
                Report(who, "connect", "");
                try
                {
                    using var session = new Session(this, client, who);
                    await session.RunAsync(ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or AuthenticationException)
                {
                }
                finally
                {
                    Interlocked.Decrement(ref _clients);
                    client.Dispose();
                    Report(who, "disconnect", "");
                }
            }, ct);
        }
    }

    /// <summary>Virtual path ("/a/b") → full local path inside the root, or null when it would leave the root.</summary>
    internal string? Resolve(string cwd, string path)
    {
        var virt = path.StartsWith('/') ? path : cwd.TrimEnd('/') + "/" + path;
        var parts = new List<string>();
        foreach (var part in virt.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (part == ".") continue;
            if (part == "..")
            {
                if (parts.Count > 0) parts.RemoveAt(parts.Count - 1);
                continue;
            }

            if (part.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || part.Contains(':')) return null;
            parts.Add(part);
        }

        var full = Path.GetFullPath(Path.Combine([_root, .. parts]));
        if (!IsInside(full)) return null;

        // Links must not lead outside the shared folder.
        var current = _root;
        foreach (var part in parts)
        {
            current = Path.Combine(current, part);
            FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (info.Exists && info.LinkTarget is not null)
            {
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName;
                if (target is null || !IsInside(Path.GetFullPath(target))) return null;
            }
        }

        return full;
    }

    private bool IsInside(string full) =>
        string.Equals(full, _root, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase) ||
        full.StartsWith(_root + Path.DirectorySeparatorChar, OperatingSystem.IsLinux() ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase);

    internal string Virtual(string full)
    {
        var rel = Path.GetRelativePath(_root, full).Replace('\\', '/');
        return rel == "." ? "/" : "/" + rel;
    }

    internal int NextPassivePort()
    {
        if (_o.PassiveFrom <= 0 || _o.PassiveTo < _o.PassiveFrom) return 0;
        var span = _o.PassiveTo - _o.PassiveFrom + 1;
        var n = Interlocked.Increment(ref _nextPassive);
        return _o.PassiveFrom + (int)((uint)(n - _o.PassiveFrom) % (uint)span);
    }

    /// <summary>One client connection.</summary>
    private sealed class Session(LocalFtpServer server, TcpClient tcp, string who) : IDisposable
    {
        private readonly FtpServerOptions _o = server._o;
        private Stream _stream = tcp.GetStream();
        private StreamReader? _reader;
        private bool _tls;
        private bool _protect;
        private string? _user;
        private bool _loggedIn;
        private int _failures;
        private string _cwd = "/";
        private TcpListener? _passive;
        private long _restart;
        private string? _renameFrom;
        private bool ReadOnly => _o.ReadOnly || _o.User is null;

        public void Dispose()
        {
            _passive?.Stop();
            _stream.Dispose();
        }

        public async Task RunAsync(CancellationToken ct)
        {
            _reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
            await ReplyAsync(220, "Nexode FTP server ready.", ct).ConfigureAwait(false);
            while (!ct.IsCancellationRequested)
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
                idle.CancelAfter(TimeSpan.FromMinutes(10));
                var line = await _reader.ReadLineAsync(idle.Token).ConfigureAwait(false);
                if (line is null) return;
                if (line.Length > 4096) line = line[..4096];
                var space = line.IndexOf(' ');
                var cmd = (space < 0 ? line : line[..space]).ToUpperInvariant();
                var arg = space < 0 ? "" : line[(space + 1)..];
                if (!await HandleAsync(cmd, arg, ct).ConfigureAwait(false)) return;
            }
        }

        private async Task<bool> HandleAsync(string cmd, string arg, CancellationToken ct)
        {
            switch (cmd)
            {
                case "AUTH":
                    if (_o.Certificate is null || !arg.Equals("TLS", StringComparison.OrdinalIgnoreCase) && !arg.Equals("SSL", StringComparison.OrdinalIgnoreCase))
                    {
                        await ReplyAsync(504, "TLS is not enabled.", ct).ConfigureAwait(false);
                        return true;
                    }

                    await ReplyAsync(234, "Starting TLS.", ct).ConfigureAwait(false);
                    var ssl = new SslStream(_stream, false);
                    await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                    {
                        ServerCertificate = _o.Certificate,
                        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                    }, ct).ConfigureAwait(false);
                    _stream = ssl;
                    _reader = new StreamReader(_stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                    _tls = true;
                    return true;
                case "PBSZ":
                    await ReplyAsync(200, "PBSZ=0", ct).ConfigureAwait(false);
                    return true;
                case "PROT":
                    _protect = arg.Equals("P", StringComparison.OrdinalIgnoreCase) && _tls;
                    await ReplyAsync(_protect || arg.Equals("C", StringComparison.OrdinalIgnoreCase) && _o.Certificate is null ? 200 : 536,
                        _protect ? "Data channel protected." : _o.Certificate is null ? "Clear." : "Use PROT P.", ct).ConfigureAwait(false);
                    return true;
                case "USER":
                    if (_o.Certificate is not null && !_tls)
                    {
                        await ReplyAsync(530, "TLS required (AUTH TLS).", ct).ConfigureAwait(false);
                        return true;
                    }

                    _user = arg;
                    _loggedIn = false;
                    if (_o.User is null && arg is "anonymous" or "ftp")
                    {
                        await ReplyAsync(331, "Any password (anonymous, read-only).", ct).ConfigureAwait(false);
                        return true;
                    }

                    await ReplyAsync(331, "Password required.", ct).ConfigureAwait(false);
                    return true;
                case "PASS":
                {
                    var ok = _user is not null && (_o.User is null
                        ? _user is "anonymous" or "ftp"
                        : FixedEquals(_user, _o.User) && FixedEquals(arg, _o.Password ?? ""));
                    if (ok)
                    {
                        _loggedIn = true;
                        server.Report(who, "login", _user!);
                        await ReplyAsync(230, "Logged in" + (ReadOnly ? " (read-only)." : "."), ct).ConfigureAwait(false);
                        return true;
                    }

                    _failures++;
                    server.Report(who, "login-failed", _user ?? "");
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(8, _failures * 2)), ct).ConfigureAwait(false);
                    await ReplyAsync(530, "Login incorrect.", ct).ConfigureAwait(false);
                    return _failures < 5;
                }
                case "QUIT":
                    await ReplyAsync(221, "Goodbye.", ct).ConfigureAwait(false);
                    return false;
                case "NOOP":
                    await ReplyAsync(200, "OK", ct).ConfigureAwait(false);
                    return true;
                case "FEAT":
                    await RawAsync("211-Features:\r\n UTF8\r\n EPSV\r\n PASV\r\n SIZE\r\n MDTM\r\n MFMT\r\n REST STREAM\r\n MLST type*;size*;modify*;\r\n" +
                                   (_o.Certificate is not null ? " AUTH TLS\r\n PBSZ\r\n PROT\r\n" : "") + "211 End\r\n", ct).ConfigureAwait(false);
                    return true;
                case "SYST":
                    await ReplyAsync(215, "UNIX Type: L8", ct).ConfigureAwait(false);
                    return true;
                case "OPTS":
                case "CLNT":
                    await ReplyAsync(200, "OK", ct).ConfigureAwait(false);
                    return true;
            }

            if (!_loggedIn)
            {
                await ReplyAsync(530, "Please log in.", ct).ConfigureAwait(false);
                return true;
            }

            try
            {
                await LoggedInAsync(cmd, arg, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                await ReplyAsync(550, ex is UnauthorizedAccessException ? "Permission denied." : "Failed: " + ex.Message.Replace('\r', ' ').Replace('\n', ' '), ct).ConfigureAwait(false);
            }

            return true;
        }

        private async Task LoggedInAsync(string cmd, string arg, CancellationToken ct)
        {
            switch (cmd)
            {
                case "PWD":
                case "XPWD":
                    await ReplyAsync(257, "\"" + _cwd.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" is the current directory.", ct).ConfigureAwait(false);
                    break;
                case "CWD":
                case "XCWD":
                case "CDUP":
                case "XCUP":
                {
                    var full = server.Resolve(_cwd, cmd is "CDUP" or "XCUP" ? ".." : arg);
                    if (full is null || !Directory.Exists(full))
                    {
                        await ReplyAsync(550, "No such directory.", ct).ConfigureAwait(false);
                        break;
                    }

                    _cwd = server.Virtual(full);
                    await ReplyAsync(250, "OK", ct).ConfigureAwait(false);
                    break;
                }
                case "TYPE":
                case "MODE":
                case "STRU":
                    await ReplyAsync(200, "OK", ct).ConfigureAwait(false);
                    break;
                case "PASV":
                case "EPSV":
                {
                    _passive?.Stop();
                    var local = ((IPEndPoint)tcp.Client.LocalEndPoint!).Address;
                    // Dual-stack listener: an IPv4 client shows up as ::ffff:a.b.c.d – listen on the plain IPv4 address.
                    if (local.IsIPv4MappedToIPv6) local = local.MapToIPv4();
                    _passive = null;
                    for (var attempt = 0; attempt < 20 && _passive is null; attempt++)
                    {
                        var listener = new TcpListener(local, server.NextPassivePort());
                        try
                        {
                            listener.Start(1);
                            _passive = listener;
                        }
                        catch (SocketException)
                        {
                        }
                    }

                    if (_passive is null)
                    {
                        await ReplyAsync(425, "No free passive port.", ct).ConfigureAwait(false);
                        break;
                    }

                    var port = ((IPEndPoint)_passive.LocalEndpoint).Port;
                    if (cmd == "EPSV")
                    {
                        await ReplyAsync(229, $"Entering Extended Passive Mode (|||{port}|)", ct).ConfigureAwait(false);
                    }
                    else
                    {
                        var ip = local.MapToIPv4().GetAddressBytes();
                        await ReplyAsync(227, $"Entering Passive Mode ({ip[0]},{ip[1]},{ip[2]},{ip[3]},{port >> 8},{port & 255})", ct).ConfigureAwait(false);
                    }

                    break;
                }
                case "PORT":
                case "EPRT":
                    await ReplyAsync(502, "Active mode is not supported; use passive mode.", ct).ConfigureAwait(false);
                    break;
                case "LIST":
                case "NLST":
                case "MLSD":
                {
                    var target = server.Resolve(_cwd, arg.StartsWith('-') ? "" : arg);
                    if (target is null || !Directory.Exists(target))
                    {
                        await ReplyAsync(550, "No such directory.", ct).ConfigureAwait(false);
                        break;
                    }

                    var sb = new StringBuilder();
                    foreach (var info in new DirectoryInfo(target).EnumerateFileSystemInfos("*", new EnumerationOptions { IgnoreInaccessible = true, AttributesToSkip = FileAttributes.System }))
                    {
                        if (info.LinkTarget is not null && server.Resolve(server.Virtual(target), info.Name) is null) continue;
                        sb.Append(cmd switch
                        {
                            "NLST" => info.Name,
                            "MLSD" => Facts(info) + " " + info.Name,
                            _ => UnixLine(info)
                        }).Append("\r\n");
                    }

                    await SendDataAsync(Encoding.UTF8.GetBytes(sb.ToString()), ct).ConfigureAwait(false);
                    break;
                }
                case "MLST":
                {
                    var full = server.Resolve(_cwd, arg);
                    FileSystemInfo? info = full is null ? null : Directory.Exists(full) ? new DirectoryInfo(full) : File.Exists(full) ? new FileInfo(full) : null;
                    if (info is null)
                    {
                        await ReplyAsync(550, "Not found.", ct).ConfigureAwait(false);
                        break;
                    }

                    await RawAsync($"250-Listing\r\n {Facts(info)} {server.Virtual(full!)}\r\n250 End\r\n", ct).ConfigureAwait(false);
                    break;
                }
                case "SIZE":
                {
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || !File.Exists(full)) await ReplyAsync(550, "Not a file.", ct).ConfigureAwait(false);
                    else await ReplyAsync(213, new FileInfo(full).Length.ToString(CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    break;
                }
                case "MDTM":
                {
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || !File.Exists(full)) await ReplyAsync(550, "Not a file.", ct).ConfigureAwait(false);
                    else await ReplyAsync(213, File.GetLastWriteTimeUtc(full).ToString("yyyyMMddHHmmss", CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
                    break;
                }
                case "REST":
                    _restart = long.TryParse(arg, NumberStyles.None, CultureInfo.InvariantCulture, out var r) ? r : 0;
                    await ReplyAsync(350, "Restarting at " + _restart.ToString(CultureInfo.InvariantCulture) + ".", ct).ConfigureAwait(false);
                    break;
                case "RETR":
                {
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || !File.Exists(full))
                    {
                        await ReplyAsync(550, "No such file.", ct).ConfigureAwait(false);
                        break;
                    }

                    await using var file = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    file.Seek(Math.Min(_restart, file.Length), SeekOrigin.Begin);
                    _restart = 0;
                    var sent = await TransferAsync(file, toClient: true, ct).ConfigureAwait(false);
                    if (sent >= 0) server.Report(who, "download", $"{server.Virtual(full)} ({sent:N0} B)");
                    break;
                }
                case "STOR":
                case "APPE":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || Directory.Exists(full) || !Directory.Exists(Path.GetDirectoryName(full)))
                    {
                        await ReplyAsync(553, "Invalid file name.", ct).ConfigureAwait(false);
                        break;
                    }

                    var mode = cmd == "APPE" ? FileMode.Append : _restart > 0 ? FileMode.OpenOrCreate : FileMode.Create;
                    await using var file = new FileStream(full, mode, FileAccess.Write, FileShare.None);
                    if (cmd == "STOR" && _restart > 0) file.Seek(Math.Min(_restart, file.Length), SeekOrigin.Begin);
                    _restart = 0;
                    var got = await TransferAsync(file, toClient: false, ct).ConfigureAwait(false);
                    if (got >= 0) server.Report(who, "upload", $"{server.Virtual(full)} ({got:N0} B)");
                    break;
                }
                case "DELE":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || !File.Exists(full))
                    {
                        await ReplyAsync(550, "No such file.", ct).ConfigureAwait(false);
                        break;
                    }

                    File.Delete(full);
                    server.Report(who, "delete", server.Virtual(full));
                    await ReplyAsync(250, "Deleted.", ct).ConfigureAwait(false);
                    break;
                }
                case "MKD":
                case "XMKD":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || full == server._root)
                    {
                        await ReplyAsync(550, "Invalid name.", ct).ConfigureAwait(false);
                        break;
                    }

                    Directory.CreateDirectory(full);
                    await ReplyAsync(257, "\"" + server.Virtual(full) + "\" created.", ct).ConfigureAwait(false);
                    break;
                }
                case "RMD":
                case "XRMD":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || full == server._root || !Directory.Exists(full))
                    {
                        await ReplyAsync(550, "No such directory.", ct).ConfigureAwait(false);
                        break;
                    }

                    Directory.Delete(full, recursive: false);
                    await ReplyAsync(250, "Removed.", ct).ConfigureAwait(false);
                    break;
                }
                case "RNFR":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var full = server.Resolve(_cwd, arg);
                    if (full is null || full == server._root || !File.Exists(full) && !Directory.Exists(full))
                    {
                        await ReplyAsync(550, "Not found.", ct).ConfigureAwait(false);
                        break;
                    }

                    _renameFrom = full;
                    await ReplyAsync(350, "Ready for RNTO.", ct).ConfigureAwait(false);
                    break;
                }
                case "RNTO":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var full = server.Resolve(_cwd, arg);
                    if (_renameFrom is null || full is null || full == server._root)
                    {
                        await ReplyAsync(503, "RNFR first.", ct).ConfigureAwait(false);
                        break;
                    }

                    if (Directory.Exists(_renameFrom)) Directory.Move(_renameFrom, full);
                    else File.Move(_renameFrom, full);
                    _renameFrom = null;
                    await ReplyAsync(250, "Renamed.", ct).ConfigureAwait(false);
                    break;
                }
                case "MFMT":
                {
                    if (await DeniedAsync(ct).ConfigureAwait(false)) break;
                    var space = arg.IndexOf(' ');
                    var full = space < 0 ? null : server.Resolve(_cwd, arg[(space + 1)..]);
                    if (full is null || !File.Exists(full) ||
                        !DateTime.TryParseExact(arg[..space], "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var time))
                    {
                        await ReplyAsync(550, "Invalid.", ct).ConfigureAwait(false);
                        break;
                    }

                    File.SetLastWriteTimeUtc(full, time);
                    await ReplyAsync(213, "Modify=" + arg[..space] + "; " + arg[(space + 1)..], ct).ConfigureAwait(false);
                    break;
                }
                case "ABOR":
                    await ReplyAsync(226, "No transfer to abort.", ct).ConfigureAwait(false);
                    break;
                default:
                    await ReplyAsync(502, "Command not implemented.", ct).ConfigureAwait(false);
                    break;
            }
        }

        private async Task<bool> DeniedAsync(CancellationToken ct)
        {
            if (!ReadOnly) return false;
            await ReplyAsync(550, "Read-only share.", ct).ConfigureAwait(false);
            return true;
        }

        /// <summary>Data connection (passive) – TLS when PROT P was given. Returns the bytes moved, -1 on failure.</summary>
        private async Task<long> TransferAsync(Stream file, bool toClient, CancellationToken ct)
        {
            var data = await OpenDataAsync(ct).ConfigureAwait(false);
            if (data is null) return -1;
            long total;
            try
            {
                await using (data)
                {
                    var buffer = new byte[81920];
                    total = 0;
                    int n;
                    if (toClient)
                    {
                        while ((n = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await data.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            total += n;
                        }

                        if (data is OwnedStream { Inner: SslStream s })
                        {
                            // close_notify, then give the client a moment to answer it before the socket goes away
                            // (some clients, e.g. Python's ftplib, fail otherwise).
                            await s.ShutdownAsync().ConfigureAwait(false);
                            using var linger = CancellationTokenSource.CreateLinkedTokenSource(ct);
                            linger.CancelAfter(TimeSpan.FromSeconds(3));
                            try
                            {
                                while (await s.ReadAsync(buffer, linger.Token).ConfigureAwait(false) > 0)
                                {
                                }
                            }
                            catch (Exception ex) when (ex is OperationCanceledException or IOException)
                            {
                            }
                        }
                    }
                    else
                    {
                        while ((n = await data.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                        {
                            await file.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                            total += n;
                        }

                        // Answer the client's close_notify with ours.
                        if (data is OwnedStream { Inner: SslStream up })
                        {
                            try
                            {
                                await up.ShutdownAsync().ConfigureAwait(false);
                            }
                            catch (Exception ex) when (ex is IOException or InvalidOperationException or ObjectDisposedException)
                            {
                                // The client closed without close_notify: nothing to answer.
                            }
                        }
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException)
            {
                await ReplyAsync(426, "Transfer aborted.", ct).ConfigureAwait(false);
                return -1;
            }

            // Upload: close the file before 226, so the client can open, rename or delete it right away
            // (Windows locks an open file; the caller's using disposes it again harmlessly).
            if (!toClient) await file.DisposeAsync().ConfigureAwait(false);
            await ReplyAsync(226, "Transfer complete.", ct).ConfigureAwait(false);
            return total;
        }

        private async Task SendDataAsync(byte[] payload, CancellationToken ct)
        {
            using var ms = new MemoryStream(payload);
            await TransferAsync(ms, toClient: true, ct).ConfigureAwait(false);
        }

        private async Task<Stream?> OpenDataAsync(CancellationToken ct)
        {
            if (_passive is null)
            {
                await ReplyAsync(425, "Use PASV or EPSV first.", ct).ConfigureAwait(false);
                return null;
            }

            if (_o.Certificate is not null && !_protect)
            {
                await ReplyAsync(521, "Data protection required (PROT P).", ct).ConfigureAwait(false);
                return null;
            }

            await ReplyAsync(150, "Opening data connection.", ct).ConfigureAwait(false);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            TcpClient client;
            try
            {
                client = await _passive.AcceptTcpClientAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                await ReplyAsync(425, "No data connection.", ct).ConfigureAwait(false);
                return null;
            }
            finally
            {
                _passive.Stop();
                _passive = null;
            }

            // Only the client of this session may connect to its data port.
            var controlPeer = ((IPEndPoint)tcp.Client.RemoteEndPoint!).Address;
            if (!((IPEndPoint)client.Client.RemoteEndPoint!).Address.Equals(controlPeer))
            {
                client.Dispose();
                await ReplyAsync(425, "Data connection from another address.", ct).ConfigureAwait(false);
                return null;
            }

            Stream stream = client.GetStream();
            if (_protect)
            {
                var ssl = new SslStream(stream, false);
                await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                {
                    ServerCertificate = _o.Certificate,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                }, ct).ConfigureAwait(false);
                stream = ssl;
            }

            return new OwnedStream(stream, client);
        }

        private static string Facts(FileSystemInfo info) =>
            info is DirectoryInfo
                ? $"type=dir;modify={info.LastWriteTimeUtc:yyyyMMddHHmmss};"
                : $"type=file;size={((FileInfo)info).Length};modify={info.LastWriteTimeUtc:yyyyMMddHHmmss};";

        private static string UnixLine(FileSystemInfo info)
        {
            var dir = info is DirectoryInfo;
            var size = dir ? 0 : ((FileInfo)info).Length;
            var t = info.LastWriteTimeUtc;
            var date = t > DateTime.UtcNow.AddDays(-180)
                ? t.ToString("MMM dd HH:mm", CultureInfo.InvariantCulture)
                : t.ToString("MMM dd  yyyy", CultureInfo.InvariantCulture);
            return $"{(dir ? "d" : "-")}rw-r--r-- 1 nexode nexode {size,12} {date} {info.Name}";
        }

        private static bool FixedEquals(string a, string b) =>
            CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(a)), SHA256.HashData(Encoding.UTF8.GetBytes(b)));

        private Task ReplyAsync(int code, string text, CancellationToken ct) => RawAsync($"{code} {text}\r\n", ct);

        private async Task RawAsync(string text, CancellationToken ct)
        {
            var bytes = Encoding.UTF8.GetBytes(text);
            await _stream.WriteAsync(bytes, ct).ConfigureAwait(false);
            await _stream.FlushAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Data stream that closes its TCP client with it.</summary>
    private sealed class OwnedStream(Stream inner, TcpClient client) : Stream
    {
        public Stream Inner => inner;
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => inner.CanWrite;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => inner.ReadAsync(buffer, ct);
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                client.Dispose();
            }

            base.Dispose(disposing);
        }

        public override async ValueTask DisposeAsync()
        {
            await inner.DisposeAsync().ConfigureAwait(false);
            client.Dispose();
            await base.DisposeAsync().ConfigureAwait(false);
        }
    }
}
