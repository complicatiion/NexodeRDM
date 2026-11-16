using System.Globalization;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using Nexode.Core.Model;

namespace Nexode.Protocols.Files;

public sealed class FtpException(int code, string message) : IOException(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// FTP / FTPS client (RFC 959, 2228, 2428, 3659): explicit (AUTH TLS) and implicit TLS, protected data channel,
/// EPSV/PASV, MLSD with LIST fallback (Unix and Windows formats), UTF-8. One command at a time (serialised).
/// </summary>
public sealed partial class FtpClient(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts) : IRemoteFileSystem, ITimestampFileSystem
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private TcpClient? _tcp;
    private Stream? _stream;
    private StreamReader? _reader;
    private Encoding _encoding = Encoding.UTF8;
    private HashSet<string> _features = new(StringComparer.OrdinalIgnoreCase);
    private bool _tls;
    private bool _protectData;
    private X509Certificate2? _serverCert;
    private bool _epsvFailed;

    public string Protocol => Encryption == FtpEncryption.None ? "FTP" : "FTPS";
    public bool IsConnected => _tcp?.Connected == true;
    public FtpEncryption Encryption { get; } =
        Enum.TryParse<FtpEncryption>(p.Setting(SettingKeys.FtpEncryption), true, out var e) ? e : FtpEncryption.None;

    public IRemoteFileSystem Clone() => new FtpClient(p, prompts, knownHosts);

    // ------------------------------------------------------------------ connect

    public async Task ConnectAsync(CancellationToken ct)
    {
        _tcp = await NetErrors.ConnectAsync(p.Host, p.Port, p.Timeout, ct).ConfigureAwait(false);
        _stream = _tcp.GetStream();
        if (Encryption == FtpEncryption.Implicit)
        {
            await StartTlsAsync(ct).ConfigureAwait(false);
        }

        SetReader();
        var (code, msg) = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (code != 220) throw new FtpException(code, msg);

        if (Encryption == FtpEncryption.Explicit)
        {
            var auth = await CommandAsync("AUTH TLS", ct).ConfigureAwait(false);
            if (auth.Code != 234) throw new FtpException(auth.Code, "The server does not support FTPS (AUTH TLS): " + auth.Message);
            await StartTlsAsync(ct).ConfigureAwait(false);
            SetReader();
        }

        var user = string.IsNullOrEmpty(p.Username) ? "anonymous" : p.Username;
        var reply = await CommandAsync("USER " + user, ct).ConfigureAwait(false);
        if (reply.Code == 331)
        {
            reply = await CommandAsync("PASS " + (p.Password ?? (user == "anonymous" ? "anonymous@" : "")), ct, sensitive: true).ConfigureAwait(false);
        }

        if (reply.Code is not (230 or 202)) throw new FtpException(reply.Code, "Login failed: " + reply.Message);

        var feat = await CommandAsync("FEAT", ct).ConfigureAwait(false);
        if (feat.Code == 211)
        {
            _features = feat.Message.Split('\n').Skip(1).Select(l => l.Trim().Split(' ')[0]).Where(l => l.Length > 0 && !l.StartsWith("211", StringComparison.Ordinal))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        if (_features.Contains("UTF8")) await CommandAsync("OPTS UTF8 ON", ct).ConfigureAwait(false);
        else _encoding = Encoding.Latin1;
        SetReader();

        if (_tls)
        {
            await CommandAsync("PBSZ 0", ct).ConfigureAwait(false);
            var prot = await CommandAsync("PROT P", ct).ConfigureAwait(false);
            _protectData = prot.Code == 200;
        }

        await Expect("TYPE I", 200, ct).ConfigureAwait(false);
    }

    private async Task StartTlsAsync(CancellationToken ct)
    {
        var ssl = new SslStream(_stream!, false, (_, cert, _, _) =>
        {
            _serverCert = cert is null ? null : new X509Certificate2(cert);
            return true; // checked below (system trust or trust on first use)
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = p.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, ct).ConfigureAwait(false);
        await VerifyCertificateAsync().ConfigureAwait(false);
        _stream = ssl;
        _tls = true;
    }

    private async Task VerifyCertificateAsync()
    {
        if (_serverCert is null) throw new AuthenticationException("The server sent no certificate.");
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        var trusted = chain.Build(_serverCert) && MatchesHost(_serverCert, p.Host);
        if (trusted) return;
        var fingerprint = "SHA256:" + Convert.ToHexString(SHA256.HashData(_serverCert.RawData));
        if (!await knownHosts.VerifyAsync("tls", p.Host, p.Port, _serverCert.Subject, fingerprint, prompts).ConfigureAwait(false))
        {
            throw new AuthenticationException("Server certificate not trusted.");
        }
    }

    private static bool MatchesHost(X509Certificate2 cert, string host)
    {
        var name = cert.GetNameInfo(X509NameType.DnsName, false);
        if (string.Equals(name, host, StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var ext in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            foreach (var dns in ext.EnumerateDnsNames())
            {
                if (string.Equals(dns, host, StringComparison.OrdinalIgnoreCase)) return true;
                if (dns.StartsWith("*.", StringComparison.Ordinal) && host.EndsWith(dns[1..], StringComparison.OrdinalIgnoreCase) && host.Count(c => c == '.') == dns.Count(c => c == '.')) return true;
            }
        }

        return false;
    }

    private void SetReader() => _reader = new StreamReader(_stream!, _encoding, false, 4096, leaveOpen: true);

    // ------------------------------------------------------------------ commands

    private async Task<(int Code, string Message)> ReadReplyAsync(CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(p.Timeout + TimeSpan.FromSeconds(15));
        var first = await _reader!.ReadLineAsync(cts.Token).ConfigureAwait(false) ?? throw new IOException("Connection closed by the server.");
        if (first.Length < 3 || !int.TryParse(first.AsSpan(0, 3), out var code)) throw new IOException("Invalid server reply: " + first);
        var sb = new StringBuilder(first.Length > 4 ? first[4..] : "");
        if (first.Length > 3 && first[3] == '-')
        {
            while (true)
            {
                var line = await _reader.ReadLineAsync(cts.Token).ConfigureAwait(false) ?? throw new IOException("Connection closed by the server.");
                sb.Append('\n').Append(line);
                if (line.Length >= 4 && line.StartsWith(first[..3], StringComparison.Ordinal) && line[3] == ' ') break;
            }
        }

        return (code, sb.ToString());
    }

    /// <summary>MFMT (draft-somers-ftp-mfxx), supported by most current servers; false when the server lacks it.</summary>
    public async Task<bool> SetModifiedAsync(string path, DateTime utc, CancellationToken ct)
    {
        if (!_features.Contains("MFMT")) return false;
        var stamp = DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToString("yyyyMMddHHmmss", System.Globalization.CultureInfo.InvariantCulture);
        var r = await CommandAsync($"MFMT {stamp} {path}", ct).ConfigureAwait(false);
        return r.Code is 213;
    }

    private async Task<(int Code, string Message)> CommandAsync(string command, CancellationToken ct, bool sensitive = false)
    {
        var bytes = _encoding.GetBytes(command + "\r\n");
        await _stream!.WriteAsync(bytes, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
        _ = sensitive;
        return await ReadReplyAsync(ct).ConfigureAwait(false);
    }

    private async Task<string> Expect(string command, int code, CancellationToken ct)
    {
        var r = await CommandAsync(command, ct).ConfigureAwait(false);
        if (r.Code != code && r.Code / 100 != code / 100) throw new FtpException(r.Code, r.Message);
        return r.Message;
    }

    private async Task<T> Locked<T>(Func<Task<T>> action, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await action().ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task Locked(Func<Task> action, CancellationToken ct) => Locked(async () =>
    {
        await action().ConfigureAwait(false);
        return 0;
    }, ct);

    // ------------------------------------------------------------------ data channel

    private async Task<Stream> OpenDataAsync(CancellationToken ct)
    {
        IPEndPoint endpoint;
        if (!_epsvFailed)
        {
            var r = await CommandAsync("EPSV", ct).ConfigureAwait(false);
            if (r.Code == 229 && EpsvPort().Match(r.Message) is { Success: true } m)
            {
                endpoint = new IPEndPoint(ControlAddress(), int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                return await ConnectDataAsync(endpoint, ct).ConfigureAwait(false);
            }

            _epsvFailed = true;
        }

        var pasv = await CommandAsync("PASV", ct).ConfigureAwait(false);
        if (pasv.Code != 227 || PasvAddress().Match(pasv.Message) is not { Success: true } pm) throw new FtpException(pasv.Code, "Passive mode failed: " + pasv.Message);
        var n = Enumerable.Range(1, 6).Select(i => int.Parse(pm.Groups[i].Value, CultureInfo.InvariantCulture)).ToArray();
        var address = IPAddress.Parse($"{n[0]}.{n[1]}.{n[2]}.{n[3]}");
        // Servers behind NAT often announce a private or zero address: use the control connection's address.
        var control = ControlAddress();
        if (address.Equals(IPAddress.Any) || IsPrivate(address) && !IsPrivate(control.MapToIPv4())) address = control;
        endpoint = new IPEndPoint(address, n[4] * 256 + n[5]);
        return await ConnectDataAsync(endpoint, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Server address of the control connection. A dual-stack socket reports IPv4 peers as IPv4-mapped IPv6
    /// (::ffff:a.b.c.d); Windows cannot connect a plain IPv6 socket there, so it is turned back into IPv4.
    /// </summary>
    private IPAddress ControlAddress()
    {
        var a = ((IPEndPoint)_tcp!.Client.RemoteEndPoint!).Address;
        return a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a;
    }

    private static bool IsPrivate(IPAddress a)
    {
        var b = a.GetAddressBytes();
        return b.Length == 4 && (b[0] == 10 || b[0] == 172 && b[1] is >= 16 and <= 31 || b[0] == 192 && b[1] == 168 || b[0] == 127);
    }

    private async Task<Stream> ConnectDataAsync(IPEndPoint endpoint, CancellationToken ct)
    {
        var tcp = new TcpClient(endpoint.AddressFamily) { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(p.Timeout);
        await tcp.ConnectAsync(endpoint, cts.Token).ConfigureAwait(false);
        return new DataStream(tcp);
    }

    private async Task<Stream> SecureDataAsync(Stream data, CancellationToken ct)
    {
        if (!_protectData) return data;
        var expected = _serverCert?.RawData;
        var ssl = new SslStream(data, false, (_, cert, _, errors) =>
            errors == SslPolicyErrors.None || cert is not null && expected is not null && cert.GetRawCertData().AsSpan().SequenceEqual(expected));
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
        {
            TargetHost = p.Host,
            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
        }, ct).ConfigureAwait(false);
        return ssl;
    }

    /// <summary>Runs a transfer command: opens the data channel, sends the command, runs <paramref name="body"/>, reads the final reply.</summary>
    private async Task TransferAsync(string command, Func<Stream, Task> body, CancellationToken ct)
    {
        var raw = await OpenDataAsync(ct).ConfigureAwait(false);
        var r = await CommandAsync(command, ct).ConfigureAwait(false);
        if (r.Code is not (125 or 150))
        {
            await raw.DisposeAsync().ConfigureAwait(false);
            throw new FtpException(r.Code, r.Message);
        }

        var data = await SecureDataAsync(raw, ct).ConfigureAwait(false);
        try
        {
            await body(data).ConfigureAwait(false);
            if (data is SslStream ssl)
            {
                try { await ssl.ShutdownAsync().ConfigureAwait(false); } catch (Exception ex) when (ex is IOException or ObjectDisposedException) { }
                await DrainAsync(ssl, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            await data.DisposeAsync().ConfigureAwait(false);
        }

        var done = await ReadReplyAsync(ct).ConfigureAwait(false);
        if (done.Code / 100 != 2) throw new FtpException(done.Code, done.Message);
    }

    /// <summary>
    /// Reads what the server still sends on a TLS data channel (its close_notify, TLS 1.3 session tickets) until it
    /// closes. Closing a socket with unread data makes Windows send a reset instead of a normal close, and the
    /// server may then lose the end of an upload and abort it ("426 Transfer aborted").
    /// </summary>
    private static async Task DrainAsync(Stream data, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(5));
        var buf = new byte[4096];
        try
        {
            while (await data.ReadAsync(buf, cts.Token).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
        {
            // Closed hard or nothing more within the time: the reply on the control connection decides.
        }
    }

    // ------------------------------------------------------------------ IRemoteFileSystem

    public Task<string> GetHomeAsync(CancellationToken ct) => Locked(async () =>
    {
        var r = await CommandAsync("PWD", ct).ConfigureAwait(false);
        var m = Quoted().Match(r.Message);
        return m.Success ? m.Groups[1].Value.Replace("\"\"", "\"") : "/";
    }, ct);

    public Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct) => Locked(async () =>
    {
        var lines = new List<string>();
        var mlsd = _features.Contains("MLSD") || _features.Contains("MLST");
        await TransferAsync((mlsd ? "MLSD " : "LIST -a ") + path, async data =>
        {
            using var reader = new StreamReader(data, _encoding, false, 8192, leaveOpen: true);
            while (await reader.ReadLineAsync(ct).ConfigureAwait(false) is { } line) lines.Add(line);
        }, ct).ConfigureAwait(false);

        var items = lines.Select(l => mlsd ? ParseMlsd(l, path) : ParseList(l, path)).OfType<RemoteItem>()
            .Where(i => i.Name is not ("." or "..")).ToList();
        return (IReadOnlyList<RemoteItem>)items;
    }, ct);

    public Task DownloadAsync(string remotePath, Stream target, IProgress<long>? progress, CancellationToken ct) => Locked(() =>
        TransferAsync("RETR " + remotePath, data => Copy(data, target, progress, ct), ct), ct);

    public Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct) => Locked(() =>
        TransferAsync("STOR " + remotePath, data => Copy(source, data, progress, ct), ct), ct);

    private static async Task Copy(Stream from, Stream to, IProgress<long>? progress, CancellationToken ct)
    {
        var buf = new byte[81920];
        long total = 0;
        int n;
        while ((n = await from.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            total += n;
            progress?.Report(total);
        }

        await to.FlushAsync(ct).ConfigureAwait(false);
    }

    public Task DeleteFileAsync(string path, CancellationToken ct) => Locked(() => Expect("DELE " + path, 250, ct), ct);
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => Locked(() => Expect("RMD " + path, 250, ct), ct);
    public Task CreateDirectoryAsync(string path, CancellationToken ct) => Locked(() => Expect("MKD " + path, 257, ct), ct);

    public Task RenameAsync(string from, string to, CancellationToken ct) => Locked(async () =>
    {
        await Expect("RNFR " + from, 350, ct).ConfigureAwait(false);
        await Expect("RNTO " + to, 250, ct).ConfigureAwait(false);
    }, ct);

    // ------------------------------------------------------------------ listing parsers

    internal static RemoteItem? ParseMlsd(string line, string dir)
    {
        var space = line.IndexOf(' ');
        if (space < 0) return null;
        var name = line[(space + 1)..];
        var facts = line[..space].Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(f => f.Split('=', 2)).Where(f => f.Length == 2).ToDictionary(f => f[0].ToLowerInvariant(), f => f[1]);
        var type = facts.GetValueOrDefault("type", "file").ToLowerInvariant();
        if (type is "cdir" or "pdir") return null;
        DateTime? modified = facts.TryGetValue("modify", out var m) && DateTime.TryParseExact(m.Split('.')[0], "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt) ? dt : null;
        var size = facts.TryGetValue("size", out var s) && long.TryParse(s, out var l) ? l : 0;
        var isLink = type.StartsWith("os.unix=slink", StringComparison.Ordinal) || type.StartsWith("os.unix=symlink", StringComparison.Ordinal);
        return new RemoteItem(name, RemotePath.Combine(dir, name), type == "dir" || isLink && !facts.ContainsKey("size"), size, modified,
            facts.GetValueOrDefault("unix.mode") ?? facts.GetValueOrDefault("perm"), isLink);
    }

    [GeneratedRegex(@"^([\-dlbcps])([rwxsStTl\-]{9})\S*\s+\d+\s+\S+\s+\S+\s+(\d+)\s+(\w{3}\s+\d{1,2}\s+(?:\d{1,2}:\d{2}|\d{4}))\s+(.+)$")]
    private static partial Regex UnixList();

    [GeneratedRegex(@"^(\d{2}-\d{2}-\d{2,4})\s+(\d{1,2}:\d{2}(?:AM|PM)?)\s+(<DIR>|\d+)\s+(.+)$", RegexOptions.IgnoreCase)]
    private static partial Regex WindowsList();

    internal static RemoteItem? ParseList(string line, string dir)
    {
        if (UnixList().Match(line) is { Success: true } u)
        {
            var name = u.Groups[5].Value;
            var isLink = u.Groups[1].Value == "l";
            if (isLink && name.IndexOf(" -> ", StringComparison.Ordinal) is var arrow and > 0) name = name[..arrow];
            DateTime? modified = null;
            var date = Regex.Replace(u.Groups[4].Value, @"\s+", " ");
            if (DateTime.TryParseExact(date, ["MMM d HH:mm", "MMM d yyyy", "MMM dd HH:mm", "MMM dd yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d))
            {
                if (!date.Contains(':') || d <= DateTime.Now.AddDays(2)) modified = d;
                else modified = d.AddYears(-1);
            }

            return new RemoteItem(name, RemotePath.Combine(dir, name), u.Groups[1].Value == "d" || isLink, long.Parse(u.Groups[3].Value, CultureInfo.InvariantCulture),
                modified, u.Groups[1].Value + u.Groups[2].Value, isLink);
        }

        if (WindowsList().Match(line) is { Success: true } w)
        {
            var isDir = w.Groups[3].Value.Equals("<DIR>", StringComparison.OrdinalIgnoreCase);
            DateTime? modified = DateTime.TryParse(w.Groups[1].Value + " " + w.Groups[2].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var d) ? d : null;
            var name = w.Groups[4].Value;
            return new RemoteItem(name, RemotePath.Combine(dir, name), isDir, isDir ? 0 : long.Parse(w.Groups[3].Value, CultureInfo.InvariantCulture), modified, null);
        }

        return null;
    }

    [GeneratedRegex(@"\(\|\|\|(\d+)\|\)")]
    private static partial Regex EpsvPort();

    [GeneratedRegex(@"(\d+),(\d+),(\d+),(\d+),(\d+),(\d+)")]
    private static partial Regex PasvAddress();

    [GeneratedRegex("\"((?:[^\"]|\"\")*)\"")]
    private static partial Regex Quoted();

    public void Dispose()
    {
        try
        {
            if (_stream is not null && _tcp?.Connected == true)
            {
                _stream.Write(_encoding.GetBytes("QUIT\r\n"));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        _stream?.Dispose();
        _tcp?.Dispose();
        _serverCert?.Dispose();
    }

    /// <summary>Data socket stream that owns its TcpClient.</summary>
    private sealed class DataStream(TcpClient tcp) : Stream
    {
        private readonly NetworkStream _inner = tcp.GetStream();
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) => _inner.ReadAsync(buffer, ct);
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default) => _inner.WriteAsync(buffer, ct);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => _inner.Write(buffer, offset, count);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
                tcp.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
