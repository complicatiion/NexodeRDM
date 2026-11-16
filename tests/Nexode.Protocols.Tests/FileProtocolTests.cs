using Nexode.Protocols.Files;
using Xunit;

namespace Nexode.Protocols.Tests;

public class RemoteProtocolTests
{
    [Fact]
    public void SigV4MatchesAwsExample()
    {
        // AWS documentation: "Example: GET Object" (Signature Version 4, header based).
        var headers = new Dictionary<string, string>
        {
            ["Host"] = "examplebucket.s3.amazonaws.com",
            ["Range"] = "bytes=0-9",
            ["x-amz-content-sha256"] = S3Signer.EmptyHash,
            ["x-amz-date"] = "20130524T000000Z"
        };
        var auth = S3Signer.Authorization("GET", "/test.txt", [], headers, S3Signer.EmptyHash, "AKIAIOSFODNN7EXAMPLE",
            "wJalrXUtnFEMI/K7MDENG/bPxRfiCYEXAMPLEKEY", "us-east-1", new DateTime(2013, 5, 24, 0, 0, 0, DateTimeKind.Utc));
        Assert.Equal("AWS4-HMAC-SHA256 Credential=AKIAIOSFODNN7EXAMPLE/20130524/us-east-1/s3/aws4_request, SignedHeaders=host;range;x-amz-content-sha256;x-amz-date, " +
                     "Signature=f0e8bdb87c964420e857bd35b5d6ed310bd44f0170aba48dd91039c6036bdb41", auth);
    }

    [Fact]
    public void SigV4EncodesKeys()
    {
        Assert.Equal("a%20b/%C3%A4%2Bc~", S3Signer.Encode("a b/ä+c~", keepSlash: true));
        Assert.Equal("a%2Fb", S3Signer.Encode("a/b", keepSlash: false));
    }

    [Fact]
    public void S3ListingSeparatesFoldersAndFiles()
    {
        const string xml = """
            <?xml version="1.0" encoding="UTF-8"?>
            <ListBucketResult xmlns="http://s3.amazonaws.com/doc/2006-03-01/">
              <Name>bucket</Name><Prefix>logs/</Prefix><IsTruncated>true</IsTruncated><NextContinuationToken>abc</NextContinuationToken>
              <Contents><Key>logs/</Key><Size>0</Size></Contents>
              <Contents><Key>logs/app.log</Key><Size>1234</Size><LastModified>2026-01-02T03:04:05.000Z</LastModified></Contents>
              <CommonPrefixes><Prefix>logs/2025/</Prefix></CommonPrefixes>
            </ListBucketResult>
            """;
        var (items, next) = S3FileSystem.ParseListing(xml, "bucket", "logs/");
        Assert.Equal("abc", next);
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i is { Name: "2025", IsDirectory: true, FullPath: "/bucket/logs/2025" });
        Assert.Contains(items, i => i is { Name: "app.log", IsDirectory: false, Size: 1234, FullPath: "/bucket/logs/app.log" });
        Assert.Equal(("bucket", "a/b c"), S3FileSystem.Split("/bucket/a/b c"));
    }

    [Fact]
    public void WebDavMultiStatusIsParsed()
    {
        const string xml = """
            <?xml version="1.0"?>
            <d:multistatus xmlns:d="DAV:">
              <d:response><d:href>/dav/files/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>/dav/files/Sub%20Folder/</d:href><d:propstat><d:prop><d:resourcetype><d:collection/></d:resourcetype></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
              <d:response><d:href>https://cloud.example.org/dav/files/b%C3%A4r.txt</d:href><d:propstat><d:prop><d:resourcetype/><d:getcontentlength>42</d:getcontentlength>
                <d:getlastmodified>Tue, 03 Feb 2026 10:00:00 GMT</d:getlastmodified></d:prop><d:status>HTTP/1.1 200 OK</d:status></d:propstat></d:response>
            </d:multistatus>
            """;
        var items = WebDavFileSystem.ParseMultiStatus(xml, "/dav/files/");
        Assert.Equal(2, items.Count);
        Assert.Contains(items, i => i is { Name: "Sub Folder", IsDirectory: true, FullPath: "/dav/files/Sub Folder" });
        var file = Assert.Single(items, i => !i.IsDirectory);
        Assert.Equal("bär.txt", file.Name);
        Assert.Equal(42, file.Size);
        Assert.NotNull(file.Modified);
    }

    [Fact]
    public void LsOutputIsParsed()
    {
        const string gnu = """
            total 12
            drwxr-xr-x  3 root root 4096 1767225600 .
            drwxr-xr-x 20 root root 4096 1767225600 ..
            -rw-r-----  1 www  www   123 1767225600 file with spaces.txt
            lrwxrwxrwx  1 root root    7 1767225600 link -> target
            drwx------  2 root root 4096 1767225600 sub
            """;
        var items = ScpFileSystem.ParseLs(gnu, "/srv", epochTimes: true);
        Assert.Equal(3, items.Count);
        var f = Assert.Single(items, i => i.Name == "file with spaces.txt");
        Assert.Equal(123, f.Size);
        Assert.Equal("/srv/file with spaces.txt", f.FullPath);
        Assert.Equal("-rw-r-----", f.Permissions);
        Assert.True(items.Single(i => i.Name == "link").IsLink);
        Assert.True(items.Single(i => i.Name == "sub").IsDirectory);

        const string busybox = "-rw-r--r--    1 root     root          10 Jan  2  2025 a.txt\ndrwxr-xr-x    2 root     root           0 Mar 14 09:30 dir\n";
        var classic = ScpFileSystem.ParseLs(busybox, "/", epochTimes: false);
        Assert.Equal(new DateTime(2025, 1, 2), classic.Single(i => i.Name == "a.txt").Modified);
        Assert.True(classic.Single(i => i.Name == "dir").IsDirectory);
        Assert.Equal("'it'\\''s'", ScpFileSystem.Quote("it's"));
    }

    [Fact]
    public void PermissionsRoundTrip()
    {
        Assert.Equal(Convert.ToInt32("754", 8), Permissions.Parse("-rwxr-xr--"));
        Assert.Equal("rwxr-xr--", Permissions.Format(Convert.ToInt32("754", 8)));
        Assert.Equal("640", Permissions.Octal(Convert.ToInt32("640", 8)));
        Assert.Null(Permissions.Parse("bogus"));
    }

    [Fact]
    public void WebDavBaseUriFromSettings()
    {
        var p = new ConnectParams { Host = "x", Port = 443, Settings = new Dictionary<string, string> { ["web.url"] = "davs://cloud.example.org/remote.php/dav" } };
        Assert.Equal("https://cloud.example.org/remote.php/dav/", WebDavFileSystem.BaseUri(p).AbsoluteUri);
    }

    [Fact]
    public async Task StatusProbeReportsClosedPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        var on = await StatusProbe.CheckAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), CancellationToken.None);
        l.Stop();
        var off = await StatusProbe.CheckAsync("127.0.0.1", port, TimeSpan.FromSeconds(2), CancellationToken.None);
        Assert.Equal(ReachState.Online, on.State);
        Assert.Equal(ReachState.Offline, off.State);
    }
}
