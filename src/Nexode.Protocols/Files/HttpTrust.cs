using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Nexode.Protocols.Files;

/// <summary>
/// HTTP handler for WebDAV and S3: certificates trusted by the system pass, others are checked trust-on-first-use
/// against <see cref="KnownHosts"/> (the user confirms the fingerprint once, like for SSH and FTPS).
/// </summary>
public static class HttpTrust
{
    public static SocketsHttpHandler CreateHandler(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts, ICredentials? credentials = null)
    {
        var handler = new SocketsHttpHandler
        {
            ConnectTimeout = p.Timeout,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            AllowAutoRedirect = false,
            Credentials = credentials,
            PreAuthenticate = credentials is not null
        };
        if (p.TunnelLocalPort is { } local)
        {
            // Jump host: every connection goes to the local tunnel end, the URL (Host header, SNI) stays the real one.
            handler.ConnectCallback = async (_, ct) =>
            {
                var s = new System.Net.Sockets.Socket(System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };
                try
                {
                    await s.ConnectAsync(IPAddress.Loopback, local, ct).ConfigureAwait(false);
                    return new System.Net.Sockets.NetworkStream(s, ownsSocket: true);
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            };
        }

        handler.SslOptions.RemoteCertificateValidationCallback = (_, cert, _, errors) =>
        {
            if (errors == SslPolicyErrors.None) return true;
            if (cert is null) return false;
            var fingerprint = "SHA256:" + Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
            using var x = new X509Certificate2(cert);
            // Runs on a connection thread; the prompt is marshalled to the UI by the implementation.
            return knownHosts.VerifyAsync("tls", p.Host, p.Port, x.Subject, fingerprint, prompts).GetAwaiter().GetResult();
        };
        return handler;
    }

    /// <summary>Stream wrapper that reports the number of bytes read so far.</summary>
    public sealed class ProgressStream(Stream inner, IProgress<long>? progress) : Stream
    {
        private long _done;
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = inner.Read(buffer, offset, count);
            Report(n);
            return n;
        }

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            Report(n);
            return n;
        }

        private void Report(int n)
        {
            if (n <= 0) return;
            // A resent request (authentication challenge) rewinds the stream: count from its position.
            _done = inner.CanSeek ? inner.Position : _done + n;
            progress?.Report(_done);
        }

        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Copies with progress (bytes written so far).</summary>
    public static async Task CopyAsync(Stream from, Stream to, IProgress<long>? progress, CancellationToken ct)
    {
        var buf = new byte[81920];
        long done = 0;
        int n;
        while ((n = await from.ReadAsync(buf, ct).ConfigureAwait(false)) > 0)
        {
            await to.WriteAsync(buf.AsMemory(0, n), ct).ConfigureAwait(false);
            done += n;
            progress?.Report(done);
        }
    }

    /// <summary>Throws a readable error for a failed response.</summary>
    public static async Task EnsureAsync(HttpResponseMessage r, string what, CancellationToken ct)
    {
        if (r.IsSuccessStatusCode) return;
        var code = (int)r.StatusCode;
        var detail = "";
        try
        {
            var body = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (System.Text.RegularExpressions.Regex.Match(body, "<Message>(.*?)</Message>") is { Success: true } m) detail = " – " + WebUtility.HtmlDecode(m.Groups[1].Value);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
        }

        throw r.StatusCode switch
        {
            HttpStatusCode.Unauthorized => new UnauthorizedAccessException("Authentication failed (401)." + detail),
            HttpStatusCode.Forbidden => new UnauthorizedAccessException("Access denied (403)." + detail),
            HttpStatusCode.NotFound => new FileNotFoundException($"Not found: {what}"),
            _ => new IOException($"{what}: HTTP {code} {r.ReasonPhrase}{detail}")
        };
    }
}
