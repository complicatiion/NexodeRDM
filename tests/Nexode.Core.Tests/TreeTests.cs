using Nexode.Core.Model;
using Nexode.Core.Search;
using Nexode.Core.Tree;
using Xunit;

namespace Nexode.Core.Tests;

public class TreeTests
{
    [Fact]
    public void SortKeys_AlwaysFitBetweenNeighbours()
    {
        var rnd = new Random(7);
        var keys = new List<string>();
        for (var i = 0; i < 2000; i++)
        {
            var pos = keys.Count == 0 ? 0 : rnd.Next(keys.Count + 1);
            var before = pos > 0 ? keys[pos - 1] : null;
            var after = pos < keys.Count ? keys[pos] : null;
            var key = SortKeys.Between(before, after);
            if (before is not null) Assert.True(string.CompareOrdinal(before, key) < 0, $"{before} < {key}");
            if (after is not null) Assert.True(string.CompareOrdinal(key, after) < 0, $"{key} < {after}");
            keys.Insert(pos, key);
        }

        Assert.Equal(keys.Count, keys.Distinct().Count());
        Assert.True(keys.Max(k => k.Length) < 40);
    }

    [Fact]
    public void NaturalSort_OrdersNumbersNumerically()
    {
        var list = new List<string> { "srv10", "srv2", "SRV1", "srv02a", "alpha" };
        list.Sort(NaturalComparer.Instance);
        Assert.Equal(["alpha", "SRV1", "srv2", "srv02a", "srv10"], list);
    }

    [Fact]
    public void Resolver_InheritsCredentialsHostAndEnvironment()
    {
        var root = new Entry { Kind = EntryKind.Folder, Name = "BSAG", CredentialMode = CredentialMode.Own, Username = "admin", Domain = "CORP", EnvironmentId = "production" };
        var host = new Entry { Kind = EntryKind.Host, Name = "web01", ParentId = root.Id, Host = "web01.corp.local", Os = OsFamily.Linux };
        var ssh = new Entry { Kind = EntryKind.Ssh, Name = "SSH", ParentId = host.Id };
        var profile = new Entry { Kind = EntryKind.Credential, Name = "root", Username = "root" };
        var rdp = new Entry { Kind = EntryKind.Rdp, Name = "dc01", ParentId = root.Id, Host = "dc01", CredentialMode = CredentialMode.Profile, CredentialId = profile.Id, EnvironmentId = "test" };
        var tree = new EntryTree([root, host, ssh, rdp, profile]);
        var resolver = new Resolver(tree);

        var e = resolver.Resolve(ssh);
        Assert.Equal("web01.corp.local", e.Host);
        Assert.Equal(22, e.Port);
        Assert.Equal("admin", e.Username);
        Assert.Equal(@"CORP\admin", e.UserWithDomain);
        Assert.Equal("admin@CORP", e.Upn);
        Assert.Equal(root.Id, e.SecretOwner);
        Assert.Equal("production", e.Environment);
        Assert.Equal(OsFamily.Linux, e.Os);
        Assert.Equal("os-linux", resolver.Resolve(host).Icon);

        var r = resolver.Resolve(rdp);
        Assert.Equal("root", r.Username);
        Assert.Equal(profile.Id, r.SecretOwner);
        Assert.Equal("test", r.Environment);
        Assert.True(tree.IsSelfOrDescendant(root.Id, ssh.Id));
        Assert.False(tree.IsSelfOrDescendant(ssh.Id, root.Id));
    }

    [Fact]
    public void Tree_FoldersFirstThenManualOrder()
    {
        var a = new Entry { Kind = EntryKind.Ssh, Name = "a", SortKey = "1" };
        var b = new Entry { Kind = EntryKind.Folder, Name = "b", SortKey = "2" };
        var c = new Entry { Kind = EntryKind.Rdp, Name = "c", SortKey = "0" };
        var tree = new EntryTree([a, b, c]);
        Assert.Equal(["b", "c", "a"], tree.Roots.Select(x => x.Name));
    }

    [Fact]
    public void Search_PrefixFiltersAndFuzzy()
    {
        var f = new Entry { Kind = EntryKind.Folder, Name = "BSAG" };
        var dc = new Entry { Kind = EntryKind.Rdp, Name = "srv-dc01", Host = "srv-dc01.bsag.de", ParentId = f.Id, Tags = ["ad"], IsFavorite = true };
        var web = new Entry { Kind = EntryKind.Ssh, Name = "web01", ParentId = f.Id, Os = OsFamily.Linux };
        var resolver = new Resolver(new EntryTree([f, dc, web]));

        Assert.True(SearchQuery.Parse("dc01").Score(dc, resolver) > 0);
        Assert.Equal(0, SearchQuery.Parse("dc01").Score(web, resolver));
        Assert.True(SearchQuery.Parse("p:rdp").Score(dc, resolver) > 0);
        Assert.Equal(0, SearchQuery.Parse("p:rdp").Score(web, resolver));
        Assert.True(SearchQuery.Parse("os:linux").Score(web, resolver) > 0);
        Assert.True(SearchQuery.Parse("#ad is:fav").Score(dc, resolver) > 0);
        Assert.True(SearchQuery.Parse("bsag web").Score(web, resolver) > 0);
        Assert.True(SearchQuery.Parse("sdc").Score(dc, resolver) > 0);
    }

    [Fact]
    public void FromUri_ParsesUserPortAndDomain()
    {
        var e = EntryKinds.FromUri(@"ssh://root@10.10.1.5:2222")!;
        Assert.Equal(EntryKind.Ssh, e.Kind);
        Assert.Equal("10.10.1.5", e.Host);
        Assert.Equal(2222, e.Port);
        Assert.Equal("root", e.Username);
        var w = EntryKinds.FromUri("https://idrac-srv01.local:8443")!;
        Assert.Equal(EntryKind.Web, w.Kind);
        Assert.Equal("https://idrac-srv01.local:8443", w.Url);
        Assert.Null(EntryKinds.FromUri("srv-dc01"));
    }
}
