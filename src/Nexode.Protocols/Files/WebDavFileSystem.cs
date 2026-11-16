using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Nexode.Core.Model;

namespace Nexode.Protocols.Files;

/// <summary>
/// WebDAV (RFC 4918) over HttpClient: PROPFIND, GET, PUT, DELETE, MKCOL, MOVE. Basic, Digest and NTLM/Negotiate are
/// negotiated by the handler. The base address comes from the URL setting (e.g. https://cloud.example.org/remote.php/dav/files/me/).
/// </summary>
public sealed class WebDavFileSystem(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts) : IRemoteFileSystem, IPermissionFileSystem
{
    private static readonly XNamespace D = "DAV:";
    private HttpClient? _http;
    private Uri? _base;

    public string Protocol => "WebDAV";
    public bool IsConnected => _http is not null;
    public bool CanChangePermissions => false;
    public IRemoteFileSystem Clone() => new WebDavFileSystem(p, prompts, knownHosts);

    /// <summary>Base URL from settings (url) or host/port/TLS/path.</summary>
    public static Uri BaseUri(ConnectParams p)
    {
        if (p.Setting(SettingKeys.WebUrl) is { } url && Uri.TryCreate(url, UriKind.Absolute, out var u))
        {
            var scheme = u.Scheme is "davs" or "webdavs" ? "https" : u.Scheme is "dav" or "webdav" ? "http" : u.Scheme;
            return new UriBuilder(u) { Scheme = scheme, Port = u.IsDefaultPort ? -1 : u.Port, Path = u.AbsolutePath.EndsWith('/') ? u.AbsolutePath : u.AbsolutePath + "/" }.Uri;
        }

        var tls = p.Setting(SettingKeys.UseTls) != "0";
        var path = p.Setting(SettingKeys.RemotePath) ?? "/";
        return new UriBuilder(tls ? "https" : "http", p.Host, p.Port is 0 or 80 or 443 ? -1 : p.Port, path.EndsWith('/') ? path : path + "/").Uri;
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _base = BaseUri(p);
        var creds = string.IsNullOrEmpty(p.Username) ? null : new NetworkCredential(p.Username, p.Password ?? "", p.Domain ?? "");
        var handler = HttpTrust.CreateHandler(p with { Host = _base.Host, Port = _base.Port }, prompts, knownHosts, creds);
        _http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Nexode", "1.0"));
        // Probe the base collection (also triggers authentication and the certificate check).
        await ListAsync(_base.AbsolutePath, ct).ConfigureAwait(false);
    }

    private HttpClient H => _http ?? throw new InvalidOperationException("Not connected.");

    public Task<string> GetHomeAsync(CancellationToken ct) => Task.FromResult(Uri.UnescapeDataString(_base!.AbsolutePath));

    /// <summary>Absolute URL for a server path ("/remote.php/dav/files/me/a b.txt").</summary>
    public Uri Url(string path)
    {
        var segments = path.Split('/');
        var escaped = string.Join('/', segments.Select(Uri.EscapeDataString));
        return new Uri(_base!, escaped.StartsWith('/') ? escaped : "/" + escaped);
    }

    private async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> make, CancellationToken ct, HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromMinutes(2));
        var r = await H.SendAsync(make(), option, option == HttpCompletionOption.ResponseHeadersRead ? ct : cts.Token).ConfigureAwait(false);
        // Some servers redirect collections without a trailing slash.
        if (r.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Redirect or HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect
            && r.Headers.Location is { } loc)
        {
            var req = make();
            req.RequestUri = loc.IsAbsoluteUri ? loc : new Uri(req.RequestUri!, loc);
            r.Dispose();
            r = await H.SendAsync(req, option, ct).ConfigureAwait(false);
        }

        return r;
    }

    private const string PropfindBody = """<?xml version="1.0" encoding="utf-8"?><d:propfind xmlns:d="DAV:"><d:prop><d:resourcetype/><d:getcontentlength/><d:getlastmodified/></d:prop></d:propfind>""";

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        if (!path.EndsWith('/')) path += "/";
        using var r = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(new HttpMethod("PROPFIND"), Url(path)) { Content = new StringContent(PropfindBody, Encoding.UTF8, "application/xml") };
            req.Headers.Add("Depth", "1");
            return req;
        }, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, path, ct).ConfigureAwait(false);
        var xml = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        return ParseMultiStatus(xml, path);
    }

    /// <summary>Parses a PROPFIND multistatus answer; the entry for <paramref name="self"/> is skipped.</summary>
    public static IReadOnlyList<RemoteItem> ParseMultiStatus(string xml, string self)
    {
        var doc = XDocument.Parse(xml);
        var list = new List<RemoteItem>();
        var selfNorm = Uri.UnescapeDataString(self).TrimEnd('/');
        foreach (var resp in doc.Descendants(D + "response"))
        {
            var href = resp.Element(D + "href")?.Value;
            if (string.IsNullOrEmpty(href)) continue;
            // Note: on Unix "/path" parses as an absolute file URI – only http(s) hrefs carry a host.
            var path = href.StartsWith("http", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(href, UriKind.Absolute, out var abs) ? abs.AbsolutePath : href;
            path = Uri.UnescapeDataString(path);
            if (path.TrimEnd('/') == selfNorm) continue;
            var prop = resp.Elements(D + "propstat")
                .Where(ps => ps.Element(D + "status")?.Value.Contains(" 200", StringComparison.Ordinal) != false)
                .Select(ps => ps.Element(D + "prop")).FirstOrDefault(x => x is not null);
            var isDir = prop?.Element(D + "resourcetype")?.Element(D + "collection") is not null || path.EndsWith('/');
            long size = long.TryParse(prop?.Element(D + "getcontentlength")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            DateTime? modified = DateTime.TryParse(prop?.Element(D + "getlastmodified")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var m) ? m.ToLocalTime() : null;
            var clean = path.TrimEnd('/');
            list.Add(new RemoteItem(RemotePath.Name(clean), clean, isDir, isDir ? 0 : size, modified, null));
        }

        return list;
    }

    public async Task DownloadAsync(string remotePath, Stream target, IProgress<long>? progress, CancellationToken ct)
    {
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, Url(remotePath)), ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, remotePath, ct).ConfigureAwait(false);
        await using var s = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await HttpTrust.CopyAsync(s, target, progress, ct).ConfigureAwait(false);
    }

    public async Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct)
    {
        var content = new StreamContent(new HttpTrust.ProgressStream(source, progress), 81920);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        if (source.CanSeek) content.Headers.ContentLength = source.Length - source.Position;
        using var req = new HttpRequestMessage(HttpMethod.Put, Url(remotePath)) { Content = content };
        using var r = await H.SendAsync(req, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, remotePath, ct).ConfigureAwait(false);
    }

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        using var r = await SendAsync(() => new HttpRequestMessage(HttpMethod.Delete, Url(path)), ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, path, ct).ConfigureAwait(false);
    }

    // DELETE on a collection removes it with its content (RFC 4918 9.6.1).
    public Task DeleteDirectoryAsync(string path, CancellationToken ct) => DeleteFileAsync(path.EndsWith('/') ? path : path + "/", ct);

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        using var r = await SendAsync(() => new HttpRequestMessage(new HttpMethod("MKCOL"), Url(path.EndsWith('/') ? path : path + "/")), ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, path, ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(new HttpMethod("MOVE"), Url(from));
            req.Headers.Add("Destination", Url(to).AbsoluteUri);
            req.Headers.Add("Overwrite", "F");
            return req;
        }, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, from, ct).ConfigureAwait(false);
    }

    /// <summary>Server-side copy (COPY method).</summary>
    public async Task CopyAsync(string from, string to, CancellationToken ct)
    {
        using var r = await SendAsync(() =>
        {
            var req = new HttpRequestMessage(new HttpMethod("COPY"), Url(from));
            req.Headers.Add("Destination", Url(to).AbsoluteUri);
            req.Headers.Add("Overwrite", "F");
            return req;
        }, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, from, ct).ConfigureAwait(false);
    }

    public Task SetPermissionsAsync(string path, int mode, CancellationToken ct) => throw new NotSupportedException();

    public void Dispose() => _http?.Dispose();
}
