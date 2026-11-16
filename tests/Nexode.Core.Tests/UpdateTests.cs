using Nexode.Core.Model;
using Nexode.Core.Updates;
using Xunit;

namespace Nexode.Core.Tests;

public class UpdateTests
{
    [Fact]
    public void ParsesLatestRelease()
    {
        const string json = """{"tag_name":"v0.0.5","name":"Nexode RDM 0.0.5","html_url":"https://github.com/complicatiion/NexodeRDM/releases/tag/v0.0.5","draft":false,"prerelease":false,"published_at":"2026-12-01T10:00:00Z"}""";
        var r = ReleaseInfo.Parse(json);
        Assert.NotNull(r);
        Assert.Equal("0.0.5", r.Version);
        Assert.StartsWith("https://github.com/", r.Url);
        Assert.True(ReleaseInfo.IsNewer(r.Version, "0.0.4"));
        Assert.False(ReleaseInfo.IsNewer("0.0.4", "0.0.4"));
        Assert.False(ReleaseInfo.IsNewer("v0.0.3", "0.0.4"));
        Assert.True(ReleaseInfo.IsNewer("nexoderdm_v0.1", "0.0.9"));
    }

    [Fact]
    public void IgnoresDraftsAndForeignLinks()
    {
        Assert.Null(ReleaseInfo.Parse("""{"tag_name":"v1.0","prerelease":true}"""));
        Assert.Null(ReleaseInfo.Parse("not json"));
        Assert.Equal("", ReleaseInfo.Parse("""{"tag_name":"v1.0","html_url":"https://evil.example.com/"}""")!.Url);
    }

    [Fact]
    public void NewKindsFromUri()
    {
        var scp = EntryKinds.FromUri("scp://root@10.0.0.5:2222/etc")!;
        Assert.Equal(EntryKind.Scp, scp.Kind);
        Assert.Equal(2222, scp.Port);
        Assert.Equal("/etc", scp.Setting(SettingKeys.RemotePath));
        var dav = EntryKinds.FromUri("davs://cloud.example.org/remote.php/dav/files/me/")!;
        Assert.Equal(EntryKind.WebDav, dav.Kind);
        Assert.Equal("https://cloud.example.org/remote.php/dav/files/me/", dav.Url);
        var s3 = EntryKinds.FromUri("s3://minio.example.local:9000/backups")!;
        Assert.Equal(EntryKind.S3, s3.Kind);
        Assert.Equal("backups", s3.Setting(SettingKeys.S3Bucket));
        Assert.True(EntryKinds.IsFileTransfer(EntryKind.S3));
        Assert.False(EntryKinds.CanTunnel(EntryKind.Ftp));
    }
}
