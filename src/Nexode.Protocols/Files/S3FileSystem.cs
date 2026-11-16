using System.Globalization;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Xml.Linq;
using Nexode.Core.Model;

namespace Nexode.Protocols.Files;

/// <summary>
/// S3 compatible object storage (AWS, MinIO, Wasabi, Backblaze B2, Ceph …) with Signature V4. Paths are
/// "/bucket/prefix/key"; "/" lists the buckets. Folders are key prefixes ("dir/" marker objects are created for new folders).
/// Access key = user name, secret key = password (stored in the vault).
/// </summary>
public sealed class S3FileSystem(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts) : IRemoteFileSystem
{
    public const int PartSize = 8 * 1024 * 1024;
    private HttpClient? _http;

    public string Protocol => "S3";
    public bool IsConnected => _http is not null;
    public IRemoteFileSystem Clone() => new S3FileSystem(p, prompts, knownHosts);

    private string Region => p.Setting(SettingKeys.S3Region) ?? "us-east-1";
    private bool Tls => p.Setting(SettingKeys.UseTls) != "0";
    private string? FixedBucket => p.Setting(SettingKeys.S3Bucket)?.Trim('/');

    private bool PathStyle => p.Setting(SettingKeys.S3PathStyle) switch
    {
        "1" => true,
        "0" => false,
        _ => !p.Host.EndsWith(".amazonaws.com", StringComparison.OrdinalIgnoreCase)
    };

    private string Authority => p.Port is 0 || (Tls && p.Port == 443) || (!Tls && p.Port == 80) ? p.Host : $"{p.Host}:{p.Port.ToString(CultureInfo.InvariantCulture)}";

    public async Task ConnectAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(p.Username) || string.IsNullOrEmpty(p.Password)) throw new UnauthorizedAccessException("Access key and secret key are required.");
        _http = new HttpClient(HttpTrust.CreateHandler(p, prompts, knownHosts)) { Timeout = Timeout.InfiniteTimeSpan };
        // A first request checks address, certificate and keys.
        if (FixedBucket is { } b) await ListAsync("/" + b + "/", ct).ConfigureAwait(false);
        else await ListAsync("/", ct).ConfigureAwait(false);
    }

    private HttpClient H => _http ?? throw new InvalidOperationException("Not connected.");

    public Task<string> GetHomeAsync(CancellationToken ct)
    {
        var home = FixedBucket is { } b ? "/" + b : "/";
        if (p.Setting(SettingKeys.RemotePath) is { } rp && FixedBucket is not null) home = RemotePath.Combine(home, rp.Trim('/'));
        return Task.FromResult(home);
    }

    /// <summary>"/bucket/a/b" → ("bucket", "a/b").</summary>
    public static (string Bucket, string Key) Split(string path)
    {
        var t = path.TrimStart('/');
        var i = t.IndexOf('/');
        return i < 0 ? (t, "") : (t[..i], t[(i + 1)..]);
    }

    // ------------------------------------------------------------------ request

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string bucket, string key, IList<KeyValuePair<string, string>>? query,
        HttpContent? content, string payloadHash, IDictionary<string, string>? extra, CancellationToken ct,
        HttpCompletionOption option = HttpCompletionOption.ResponseContentRead)
    {
        query ??= [];
        var host = Authority;
        string path;
        if (bucket.Length == 0) path = "/";
        else if (PathStyle) path = "/" + S3Signer.Encode(bucket, false) + "/" + S3Signer.Encode(key, true);
        else
        {
            host = bucket + "." + host;
            path = "/" + S3Signer.Encode(key, true);
        }

        var now = DateTime.UtcNow;
        var headers = new Dictionary<string, string>
        {
            ["host"] = host,
            ["x-amz-date"] = now.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture),
            ["x-amz-content-sha256"] = payloadHash
        };
        if (extra is not null)
        {
            foreach (var (k, v) in extra) headers[k] = v;
        }

        var auth = S3Signer.Authorization(method.Method, path, query, headers, payloadHash, p.Username!, p.Password!, Region, now);
        var qs = query.Count == 0 ? "" : "?" + string.Join('&', query.Select(q => S3Signer.Encode(q.Key, false) + "=" + S3Signer.Encode(q.Value, false)));
        var uri = new Uri($"{(Tls ? "https" : "http")}://{host}{path}{qs}", new UriCreationOptions { DangerousDisablePathAndQueryCanonicalization = true });
        var req = new HttpRequestMessage(method, uri) { Content = content };
        foreach (var (k, v) in headers)
        {
            if (k == "host") continue;
            if (k.StartsWith("content-", StringComparison.Ordinal) && content is not null) content.Headers.TryAddWithoutValidation(k, v);
            else req.Headers.TryAddWithoutValidation(k, v);
        }

        req.Headers.TryAddWithoutValidation("Authorization", auth);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (option == HttpCompletionOption.ResponseContentRead && content is null) cts.CancelAfter(TimeSpan.FromMinutes(2));
        return await H.SendAsync(req, option, cts.Token).ConfigureAwait(false);
    }

    private static XElement? Child(XElement e, string name) => e.Elements().FirstOrDefault(x => x.Name.LocalName == name);
    private static IEnumerable<XElement> Children(XElement e, string name) => e.Elements().Where(x => x.Name.LocalName == name);

    // ------------------------------------------------------------------ listing

    public async Task<IReadOnlyList<RemoteItem>> ListAsync(string path, CancellationToken ct)
    {
        var (bucket, prefix) = Split(path);
        if (bucket.Length == 0) return await ListBucketsAsync(ct).ConfigureAwait(false);
        if (prefix.Length > 0 && !prefix.EndsWith('/')) prefix += "/";
        var list = new List<RemoteItem>();
        string? token = null;
        do
        {
            var q = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("delimiter", "/"), new("prefix", prefix) };
            if (token is not null) q.Add(new("continuation-token", token));
            using var r = await SendAsync(HttpMethod.Get, bucket, "", q, null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false);
            await HttpTrust.EnsureAsync(r, path, ct).ConfigureAwait(false);
            var page = ParseListing(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false), bucket, prefix);
            list.AddRange(page.Items);
            token = page.NextToken;
        } while (token is not null);

        return list;
    }

    public static (List<RemoteItem> Items, string? NextToken) ParseListing(string xml, string bucket, string prefix)
    {
        var root = XDocument.Parse(xml).Root!;
        var items = new List<RemoteItem>();
        foreach (var cp in Children(root, "CommonPrefixes"))
        {
            var pre = Child(cp, "Prefix")?.Value;
            if (string.IsNullOrEmpty(pre)) continue;
            var full = "/" + bucket + "/" + pre.TrimEnd('/');
            items.Add(new RemoteItem(RemotePath.Name(full), full, true, 0, null, null));
        }

        foreach (var c in Children(root, "Contents"))
        {
            var key = Child(c, "Key")?.Value;
            if (string.IsNullOrEmpty(key) || key == prefix) continue;
            var size = long.TryParse(Child(c, "Size")?.Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var s) ? s : 0;
            DateTime? mod = DateTime.TryParse(Child(c, "LastModified")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var m) ? m.ToLocalTime() : null;
            var full = "/" + bucket + "/" + key;
            if (key.EndsWith('/'))
            {
                full = full.TrimEnd('/');
                if (items.Any(i => i.FullPath == full)) continue;
                items.Add(new RemoteItem(RemotePath.Name(full), full, true, 0, mod, null));
                continue;
            }

            items.Add(new RemoteItem(RemotePath.Name(full), full, false, size, mod, null));
        }

        var truncated = Child(root, "IsTruncated")?.Value == "true";
        return (items, truncated ? Child(root, "NextContinuationToken")?.Value : null);
    }

    private async Task<IReadOnlyList<RemoteItem>> ListBucketsAsync(CancellationToken ct)
    {
        using var r = await SendAsync(HttpMethod.Get, "", "", null, null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, "/", ct).ConfigureAwait(false);
        var root = XDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
        var list = new List<RemoteItem>();
        foreach (var b in root.Descendants().Where(x => x.Name.LocalName == "Bucket"))
        {
            var name = Child(b, "Name")?.Value;
            if (string.IsNullOrEmpty(name)) continue;
            DateTime? created = DateTime.TryParse(Child(b, "CreationDate")?.Value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal, out var d) ? d.ToLocalTime() : null;
            list.Add(new RemoteItem(name, "/" + name, true, 0, created, null));
        }

        return list;
    }

    /// <summary>All object keys below a prefix (recursive).</summary>
    private async Task<List<string>> AllKeysAsync(string bucket, string prefix, CancellationToken ct)
    {
        var keys = new List<string>();
        string? token = null;
        do
        {
            var q = new List<KeyValuePair<string, string>> { new("list-type", "2"), new("prefix", prefix) };
            if (token is not null) q.Add(new("continuation-token", token));
            using var r = await SendAsync(HttpMethod.Get, bucket, "", q, null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false);
            await HttpTrust.EnsureAsync(r, prefix, ct).ConfigureAwait(false);
            var root = XDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
            keys.AddRange(Children(root, "Contents").Select(c => Child(c, "Key")?.Value).OfType<string>());
            token = Child(root, "IsTruncated")?.Value == "true" ? Child(root, "NextContinuationToken")?.Value : null;
        } while (token is not null);

        return keys;
    }

    // ------------------------------------------------------------------ transfer

    public async Task DownloadAsync(string remotePath, Stream target, IProgress<long>? progress, CancellationToken ct)
    {
        var (bucket, key) = Split(remotePath);
        using var r = await SendAsync(HttpMethod.Get, bucket, key, null, null, S3Signer.EmptyHash, null, ct, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, remotePath, ct).ConfigureAwait(false);
        await using var s = await r.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await HttpTrust.CopyAsync(s, target, progress, ct).ConfigureAwait(false);
    }

    public async Task UploadAsync(Stream source, string remotePath, IProgress<long>? progress, CancellationToken ct)
    {
        var (bucket, key) = Split(remotePath);
        var first = await ReadChunkAsync(source, ct).ConfigureAwait(false);
        if (first.Length < PartSize)
        {
            await PutAsync(bucket, key, first, ct).ConfigureAwait(false);
            progress?.Report(first.Length);
            return;
        }

        // Multipart upload (8 MiB parts, up to 10 000 parts = 80 GB).
        string uploadId;
        using (var r = await SendAsync(HttpMethod.Post, bucket, key, [new("uploads", "")], null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false))
        {
            await HttpTrust.EnsureAsync(r, remotePath, ct).ConfigureAwait(false);
            var root = XDocument.Parse(await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false)).Root!;
            uploadId = Child(root, "UploadId")?.Value ?? throw new IOException("No upload id returned.");
        }

        var etags = new List<string>();
        long done = 0;
        try
        {
            var chunk = first;
            while (chunk.Length > 0)
            {
                var part = etags.Count + 1;
                var content = new ByteArrayContent(chunk);
                using var r = await SendAsync(HttpMethod.Put, bucket, key, [new("partNumber", part.ToString(CultureInfo.InvariantCulture)), new("uploadId", uploadId)],
                    content, S3Signer.Hash(chunk), null, ct).ConfigureAwait(false);
                await HttpTrust.EnsureAsync(r, remotePath, ct).ConfigureAwait(false);
                etags.Add(r.Headers.ETag?.Tag ?? (r.Headers.TryGetValues("ETag", out var e) ? e.First() : ""));
                done += chunk.Length;
                progress?.Report(done);
                chunk = await ReadChunkAsync(source, ct).ConfigureAwait(false);
            }

            var body = new StringBuilder("<CompleteMultipartUpload>");
            for (var i = 0; i < etags.Count; i++) body.Append(CultureInfo.InvariantCulture, $"<Part><PartNumber>{i + 1}</PartNumber><ETag>{System.Security.SecurityElement.Escape(etags[i])}</ETag></Part>");
            body.Append("</CompleteMultipartUpload>");
            var bytes = Encoding.UTF8.GetBytes(body.ToString());
            using var done2 = await SendAsync(HttpMethod.Post, bucket, key, [new("uploadId", uploadId)], new ByteArrayContent(bytes), S3Signer.Hash(bytes), null, ct).ConfigureAwait(false);
            await HttpTrust.EnsureAsync(done2, remotePath, ct).ConfigureAwait(false);
            var text = await done2.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (text.Contains("<Error>", StringComparison.Ordinal)) throw new IOException("Completing the upload failed.");
        }
        catch
        {
            try
            {
                using var _ = await SendAsync(HttpMethod.Delete, bucket, key, [new("uploadId", uploadId)], null, S3Signer.EmptyHash, null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
            {
            }

            throw;
        }
    }

    private static async Task<byte[]> ReadChunkAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[PartSize];
        var total = 0;
        while (total < buf.Length)
        {
            var n = await s.ReadAsync(buf.AsMemory(total), ct).ConfigureAwait(false);
            if (n == 0) break;
            total += n;
        }

        return total == buf.Length ? buf : buf[..total];
    }

    private async Task PutAsync(string bucket, string key, byte[] data, CancellationToken ct)
    {
        var content = new ByteArrayContent(data);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var r = await SendAsync(HttpMethod.Put, bucket, key, null, content, S3Signer.Hash(data), null, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, "/" + bucket + "/" + key, ct).ConfigureAwait(false);
    }

    // ------------------------------------------------------------------ changes

    public async Task DeleteFileAsync(string path, CancellationToken ct)
    {
        var (bucket, key) = Split(path);
        using var r = await SendAsync(HttpMethod.Delete, bucket, key, null, null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, path, ct).ConfigureAwait(false);
    }

    public async Task DeleteDirectoryAsync(string path, CancellationToken ct)
    {
        var (bucket, key) = Split(path.TrimEnd('/'));
        if (key.Length == 0)
        {
            using var rb = await SendAsync(HttpMethod.Delete, bucket, "", null, null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false);
            await HttpTrust.EnsureAsync(rb, path, ct).ConfigureAwait(false);
            return;
        }

        // The folder marker (if any); deleting a missing key is not an error in S3.
        using var r = await SendAsync(HttpMethod.Delete, bucket, key + "/", null, null, S3Signer.EmptyHash, null, ct).ConfigureAwait(false);
        if ((int)r.StatusCode != 404) await HttpTrust.EnsureAsync(r, path, ct).ConfigureAwait(false);
    }

    public async Task CreateDirectoryAsync(string path, CancellationToken ct)
    {
        var (bucket, key) = Split(path.TrimEnd('/'));
        if (key.Length == 0)
        {
            HttpContent? body = null;
            var hash = S3Signer.EmptyHash;
            if (Region != "us-east-1")
            {
                var xml = Encoding.UTF8.GetBytes($"<CreateBucketConfiguration xmlns=\"http://s3.amazonaws.com/doc/2006-03-01/\"><LocationConstraint>{Region}</LocationConstraint></CreateBucketConfiguration>");
                body = new ByteArrayContent(xml);
                hash = S3Signer.Hash(xml);
            }

            using var rb = await SendAsync(HttpMethod.Put, bucket, "", null, body, hash, null, ct).ConfigureAwait(false);
            await HttpTrust.EnsureAsync(rb, path, ct).ConfigureAwait(false);
            return;
        }

        await PutAsync(bucket, key + "/", [], ct).ConfigureAwait(false);
    }

    public async Task RenameAsync(string from, string to, CancellationToken ct)
    {
        var (fb, fk) = Split(from.TrimEnd('/'));
        var (tb, tk) = Split(to.TrimEnd('/'));
        if (fk.Length == 0) throw new NotSupportedException("Buckets cannot be renamed.");
        // Folder: copy every object below the prefix.
        var keys = await AllKeysAsync(fb, fk + "/", ct).ConfigureAwait(false);
        if (keys.Count > 0)
        {
            foreach (var k in keys) await CopyObjectAsync(fb, k, tb, tk + "/" + k[(fk.Length + 1)..], ct).ConfigureAwait(false);
            foreach (var k in keys) await DeleteFileAsync("/" + fb + "/" + k, ct).ConfigureAwait(false);
            return;
        }

        await CopyObjectAsync(fb, fk, tb, tk, ct).ConfigureAwait(false);
        await DeleteFileAsync(from, ct).ConfigureAwait(false);
    }

    private async Task CopyObjectAsync(string fromBucket, string fromKey, string toBucket, string toKey, CancellationToken ct)
    {
        var extra = new Dictionary<string, string> { ["x-amz-copy-source"] = "/" + S3Signer.Encode(fromBucket, false) + "/" + S3Signer.Encode(fromKey, true) };
        using var r = await SendAsync(HttpMethod.Put, toBucket, toKey, null, null, S3Signer.EmptyHash, extra, ct).ConfigureAwait(false);
        await HttpTrust.EnsureAsync(r, "/" + fromBucket + "/" + fromKey, ct).ConfigureAwait(false);
        var text = await r.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (text.Contains("<Error>", StringComparison.Ordinal)) throw new IOException("Copy failed: " + fromKey);
    }

    public void Dispose() => _http?.Dispose();
}
