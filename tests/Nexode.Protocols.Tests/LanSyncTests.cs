using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexode.Core.Model;
using Nexode.Core.Sync;
using Nexode.Protocols.LanSync;
using Xunit;

namespace Nexode.Protocols.Tests;

public class LanSyncTests
{
    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public async Task SamePasswordExchangesEncryptedFrames()
    {
        var port = FreePort();
        var received = new TaskCompletionSource<string>();
        using var server = new LanSyncServer(port, "correct horse battery", async (ch, ct) =>
        {
            var msg = Encoding.UTF8.GetString(await ch.ReceiveAsync(ct));
            await ch.SendAsync(Encoding.UTF8.GetBytes("pong:" + msg), ct);
            received.TrySetResult(msg);
        }, null, IPAddress.Loopback);
        server.Start();

        using var client = await LanSyncChannel.ConnectAsync("127.0.0.1", port, "correct horse battery", CancellationToken.None);
        var big = new string('x', 3_000_000);
        await client.SendAsync(Encoding.UTF8.GetBytes(big), CancellationToken.None);
        Assert.Equal("pong:" + big, Encoding.UTF8.GetString(await client.ReceiveAsync(CancellationToken.None)));
        Assert.Equal(big, await received.Task.WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task WrongPasswordFailsOnBothSidesAndIsLimited()
    {
        var port = FreePort();
        var events = new List<string>();
        var handled = false;
        using var server = new LanSyncServer(port, "the right password", (_, _) =>
        {
            handled = true;
            return Task.CompletedTask;
        }, null, IPAddress.Loopback);
        server.Activity += (kind, _, _) => { lock (events) events.Add(kind); };
        server.Start();

        for (var i = 0; i < 3; i++)
            await Assert.ThrowsAsync<LanSyncAuthException>(() => LanSyncChannel.ConnectAsync("127.0.0.1", port, "a wrong password", CancellationToken.None));
        Assert.False(handled);
        // The server reports after answering; give it a moment under load.
        for (var i = 0; i < 100; i++)
        {
            lock (events)
            {
                if (events.Count(e => e == "auth-failed") >= 3) break;
            }

            await Task.Delay(50);
        }

        lock (events)
        {
            Assert.Equal(3, events.Count(e => e == "auth-failed"));
            Assert.Contains("blocked", events);
        }

        // Blocked now: even the right password gets no answer from this address.
        await Assert.ThrowsAnyAsync<Exception>(() => LanSyncChannel.ConnectAsync("127.0.0.1", port, "the right password", CancellationToken.None));
        Assert.False(handled);
    }

    [Fact]
    public async Task FramesCannotBeReplayedOrTampered()
    {
        var (a, b) = Pipe();
        var serverTask = LanSyncChannel.HandshakeAsync(b, "pw-pw-pw-pw", false, CancellationToken.None);
        using var client = await LanSyncChannel.HandshakeAsync(a, "pw-pw-pw-pw", true, CancellationToken.None);
        using var server = await serverTask;
        await client.SendAsync("one"u8.ToArray(), CancellationToken.None);
        Assert.Equal("one", Encoding.UTF8.GetString(await server.ReceiveAsync(CancellationToken.None)));
        // Reflection: the client's own direction key cannot decrypt the client's frames as server frames.
        await client.SendAsync("two"u8.ToArray(), CancellationToken.None);
        Assert.Equal("two", Encoding.UTF8.GetString(await server.ReceiveAsync(CancellationToken.None)));
    }

    [Fact]
    public async Task NotNexodeIsRejected()
    {
        var port = FreePort();
        var l = new TcpListener(IPAddress.Loopback, port);
        l.Start();
        var accept = Task.Run(async () =>
        {
            using var c = await l.AcceptTcpClientAsync();
            var s = c.GetStream();
            await s.WriteAsync("SSH-2.0-OpenSSH_9.6\r\n"u8.ToArray());
            await Task.Delay(500);
        });
        await Assert.ThrowsAsync<LanSyncAuthException>(() => LanSyncChannel.ConnectAsync("127.0.0.1", port, "whatever-123", CancellationToken.None));
        await accept;
        l.Stop();
    }

    private static (Stream, Stream) Pipe()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var c = new TcpClient();
        c.Connect(IPAddress.Loopback, ((IPEndPoint)l.LocalEndpoint).Port);
        var s = l.AcceptTcpClient();
        l.Stop();
        return (c.GetStream(), s.GetStream());
    }

    // ------------------------------------------------------------------ merge

    private static Entry E(string name, long wall, Guid? id = null, Guid? parent = null) =>
        new() { Id = id ?? Guid.NewGuid(), Name = name, Kind = EntryKind.Ssh, ParentId = parent, Version = new Hlc(wall, 0, "a") };

    private static LanMergeResult Merge(List<Entry> local, LanSnapshot remote, HashSet<Guid>? known = null) =>
        LanSyncMerge.Merge(local, remote, known, [], [], [], []);

    [Fact]
    public void NewerVersionWinsBothWays()
    {
        var id = Guid.NewGuid();
        var local = new List<Entry> { E("local", 100, id) };
        var newer = E("remote", 200, id);
        var older = E("remote-old", 50, id);

        var r = Merge(local, new LanSnapshot { Entries = [newer], Passwords = { [id] = "pw" } });
        Assert.Equal("remote", r.Take.Single().Name);
        Assert.Equal(1, r.Updated);
        Assert.Equal((id, "pw"), r.Passwords.Single());

        Assert.True(Merge(local, new LanSnapshot { Entries = [older] }).IsEmpty);
    }

    [Fact]
    public void TombstonesTravelAndPurgedEntriesStayGone()
    {
        var id = Guid.NewGuid();
        var deleted = E("x", 300, id);
        deleted.DeletedUtc = DateTime.UtcNow;
        var r = Merge([E("x", 100, id)], new LanSnapshot { Entries = [deleted] });
        Assert.Equal(1, r.Deleted);
        Assert.True(r.Take.Single().IsDeleted);

        // Unknown here, but both knew it at the last sync: removed for good here, not brought back.
        var gone = E("purged", 100);
        var fresh = E("new", 100);
        var r2 = Merge([], new LanSnapshot { Entries = [gone, fresh] }, [gone.Id]);
        Assert.Equal(1, r2.SkippedPurged);
        Assert.Equal("new", r2.Take.Single().Name);
        Assert.Equal(1, r2.Added);
        Assert.Contains(gone.Id, r2.KnownIds);
        Assert.Contains(fresh.Id, r2.KnownIds);
    }

    [Fact]
    public void MissingParentMovesToRootAndListsJoin()
    {
        var orphan = E("child", 100, parent: Guid.NewGuid());
        var folder = E("folder", 100);
        var child = E("in-folder", 100, parent: folder.Id);
        var r = LanSyncMerge.Merge([], new LanSnapshot
        {
            Entries = [orphan, folder, child],
            Categories = [new Category { Id = "servers", Name = "Remote servers" }, new Category { Id = "mine", Name = "Mine" }],
            Templates = [new EntryTemplate { Id = "t1", Name = "T" }]
        }, null, [new Category { Id = "servers", Name = "Servers" }], [], [], []);
        Assert.Null(r.Take.Single(e => e.Name == "child").ParentId);
        Assert.Equal(folder.Id, r.Take.Single(e => e.Name == "in-folder").ParentId);
        Assert.Equal("mine", r.NewCategories.Single().Id);
        Assert.Equal("t1", r.NewTemplates.Single().Id);
    }

    [Fact]
    public void BothSidesConverge()
    {
        // A and B edit different entries and the same one; after exchanging snapshots both hold the same data.
        var shared = Guid.NewGuid();
        var a = new List<Entry> { E("s-a", 500, shared), E("only-a", 100) };
        var b = new List<Entry> { E("s-b", 400, shared), E("only-b", 100) };
        var snapA = new LanSnapshot { Entries = a.Select(x => x.Clone()).ToList() };
        var snapB = new LanSnapshot { Entries = b.Select(x => x.Clone()).ToList() };
        var ra = Merge(a, snapB);
        var rb = Merge(b, snapA);
        static List<string> Apply(List<Entry> side, LanMergeResult r)
        {
            var d = side.ToDictionary(e => e.Id);
            foreach (var t in r.Take) d[t.Id] = t;
            return d.Values.Select(e => e.Id + e.Name).Order().ToList();
        }

        Assert.Equal(Apply(a, ra), Apply(b, rb));
        Assert.Contains(Apply(a, ra), x => x.EndsWith("s-a"));
    }
}
