using Nexode.Core.Files;
using Nexode.Core.Model;
using Nexode.Storage;
using Xunit;

namespace Nexode.Core.Tests;

public class SharedTests
{
    [Fact]
    public void LegacyEnvironmentIsMigrated()
    {
        var e = System.Text.Json.JsonSerializer.Deserialize("""{"kind":"Rdp","name":"x","environment":"Production"}""", NexodeJson.Default.Entry)!;
        Assert.Equal("production", e.EnvironmentId);
        var none = System.Text.Json.JsonSerializer.Deserialize("""{"kind":"Rdp","name":"x","environment":"None"}""", NexodeJson.Default.Entry)!;
        Assert.Equal("", none.EnvironmentId);
        var json = System.Text.Json.JsonSerializer.Serialize(e, NexodeJson.Default.Entry);
        Assert.DoesNotContain("\"environment\"", json);
        Assert.Contains("\"environmentId\": \"production\"", json);
    }

    [Fact]
    public void UsageCountsLast90Days()
    {
        var e = new Entry();
        e.UseDays.Add(Usage.Today - 200);
        Usage.Record(e);
        Usage.Record(e);
        Assert.Equal(2, Usage.Count(e));
        Assert.Equal(2, e.UseDays.Count);
    }

    [Fact]
    public void OneEditorAtATime()
    {
        var dir = Directory.CreateTempSubdirectory("nexode-share").FullName;
        var remote = Path.Combine(dir, "share", "team.db");
        Directory.CreateDirectory(Path.GetDirectoryName(remote)!);
        var a = new SharedSource(remote, Path.Combine(dir, "cacheA"), "A", "alice", "PC1");
        var b = new SharedSource(remote, Path.Combine(dir, "cacheB"), "B", "bob", "PC2");

        Assert.True(a.TryAcquire());
        Assert.False(b.TryAcquire());
        Assert.Equal("alice", b.ReadLock()!.User);

        // Editor publishes, reader sees the change.
        File.WriteAllText(a.CachePath, "v1");
        Assert.Equal(PublishResult.Published, a.Publish(a.CachePath));
        Assert.True(b.RemoteChanged());
        Assert.True(b.Pull());
        Assert.Equal("v1", File.ReadAllText(b.CachePath));
        Assert.False(b.RemoteChanged());

        // Forced take-over: the old editor loses its role on the next heartbeat and cannot overwrite.
        Assert.True(b.TryAcquire(force: true));
        File.WriteAllText(b.CachePath, "v2");
        Assert.Equal(PublishResult.Published, b.Publish(b.CachePath));
        Assert.False(a.Heartbeat());
        Assert.Equal(PublishResult.NotEditor, a.Publish(a.CachePath));

        // Stale lock can be taken over without force.
        b.Release();
        Assert.Null(b.ReadLock());
        Assert.True(a.TryAcquire());
        SharedSource.StaleAfter = TimeSpan.Zero;
        try
        {
            Thread.Sleep(20);
            Assert.True(b.TryAcquire());
        }
        finally
        {
            SharedSource.StaleAfter = TimeSpan.FromMinutes(2);
        }

        // a still thinks it edits, but the file changed meanwhile: conflict copy instead of overwriting.
        File.WriteAllText(b.CachePath, "v3");
        b.Publish(b.CachePath);
        var aa = new SharedSource(remote, Path.Combine(dir, "cacheA"), "A2", "alice", "PC1");
        aa.Pull();
        Assert.True(aa.TryAcquire(force: true));
        File.WriteAllText(Path.Combine(dir, "x"), "mine");
        File.SetLastWriteTimeUtc(remote, DateTime.UtcNow.AddMinutes(1));
        File.AppendAllText(remote, "-changed");
        Assert.Equal(PublishResult.Conflict, aa.Publish(Path.Combine(dir, "x")));
        Assert.Single(Directory.GetFiles(Path.GetDirectoryName(remote)!, "team.conflict-*"));
        Directory.Delete(dir, true);
    }

    [Fact]
    public void StoreKeepsEnvironmentsAndProfiles()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexode-ep-" + Guid.NewGuid().ToString("N") + ".db");
        var id = Guid.NewGuid();
        using (var store = DataSourceStore.Open(path, "t"))
        {
            store.SaveEnvironments([new EnvironmentDef { Id = "trash", Name = "Trash", Color = "#8B5E3C" }]);
            store.SaveProfiles([new ConnectionProfile { Id = "p1", Name = "Citrix", Entries = [id], Layout = ProfileLayout.Split }]);
        }

        using (var store = DataSourceStore.Open(path, "t"))
        {
            Assert.Equal("#8B5E3C", store.LoadEnvironments().Single().Color);
            var p = store.LoadProfiles().Single();
            Assert.Equal((ProfileLayout.Split, id), (p.Layout, p.Entries.Single()));
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }
}
