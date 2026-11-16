using System.Net;
using System.Net.Sockets;
using System.Text;
using Nexode.Core.Model;
using Nexode.Protocols;
using Nexode.Protocols.Files;
using Nexode.Protocols.Server;
using Xunit;

namespace Nexode.Protocols.Tests;

public class FtpServerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nx-ftpsrv-" + Guid.NewGuid().ToString("N"));
    private readonly string _outside;

    public FtpServerTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "sub"));
        File.WriteAllText(Path.Combine(_root, "hello.txt"), "hello world");
        _outside = _root + "-secret.txt";
        File.WriteAllText(_outside, "secret");
    }

    public void Dispose()
    {
        Directory.Delete(_root, true);
        File.Delete(_outside);
        if (Directory.Exists(_root + "-cert")) Directory.Delete(_root + "-cert", true);
    }

    private sealed class Prompts : ISessionPrompts
    {
        public int Confirmed;

        public Task<bool> ConfirmHostKeyAsync(HostKeyCheck check)
        {
            Confirmed++;
            return Task.FromResult(true);
        }

        public Task<string?> AskSecretAsync(string title, string prompt) => Task.FromResult<string?>(null);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private static FtpClient Client(int port, string user, string pw, bool tls, Prompts prompts, string knownPath) =>
        new(new ConnectParams
        {
            Host = "127.0.0.1", Port = port, Username = user, Password = pw,
            Settings = tls ? new Dictionary<string, string> { [SettingKeys.FtpEncryption] = "Explicit" } : new Dictionary<string, string>()
        }, prompts, new KnownHosts(knownPath));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReadWriteShareWithPasswordAndTls(bool tls)
    {
        var port = FreePort();
        var known = Path.Combine(_root + "-known.json");
        var cert = tls ? LocalFtpServer.LoadOrCreateCertificate(Path.Combine(_root + "-cert", "ftp.pfx")) : null;
        using var server = new LocalFtpServer(new FtpServerOptions
        {
            Root = _root, Port = port, Bind = IPAddress.Loopback, User = "share", Password = "pw123", ReadOnly = false, Certificate = cert,
            PassiveFrom = 0, PassiveTo = 0
        });
        var events = new List<string>();
        server.Activity += e => { lock (events) events.Add(e.Kind); };
        server.Start();
        try
        {
            var prompts = new Prompts();
            using var c = Client(port, "share", "pw123", tls, prompts, known);
            await c.ConnectAsync(CancellationToken.None);
            Assert.Equal(tls ? 1 : 0, prompts.Confirmed);
            var items = await c.ListAsync("/", CancellationToken.None);
            Assert.Contains(items, i => i.Name == "hello.txt" && i.Size == 11 && !i.IsDirectory);
            Assert.Contains(items, i => i.Name == "sub" && i.IsDirectory);

            var ms = new MemoryStream();
            await c.DownloadAsync("/hello.txt", ms, null, CancellationToken.None);
            Assert.Equal("hello world", Encoding.UTF8.GetString(ms.ToArray()));

            await c.UploadAsync(new MemoryStream("üpload"u8.ToArray()), "/sub/ü.txt", null, CancellationToken.None);
            Assert.Equal("üpload", File.ReadAllText(Path.Combine(_root, "sub", "ü.txt")));
            Assert.True(await c.SetModifiedAsync("/sub/ü.txt", new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), CancellationToken.None));
            Assert.Equal(new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(_root, "sub", "ü.txt")));
            await c.RenameAsync("/sub/ü.txt", "/sub/renamed.txt", CancellationToken.None);
            await c.CreateDirectoryAsync("/new", CancellationToken.None);
            await c.DeleteFileAsync("/sub/renamed.txt", CancellationToken.None);
            await c.DeleteDirectoryAsync("/new", CancellationToken.None);
            Assert.False(Directory.Exists(Path.Combine(_root, "new")));

            // Nothing outside the shared folder.
            await Assert.ThrowsAnyAsync<Exception>(() => c.DownloadAsync("/../" + Path.GetFileName(_outside), new MemoryStream(), null, CancellationToken.None));
            var rootList = await c.ListAsync("/../..", CancellationToken.None);
            Assert.Contains(rootList, i => i.Name == "hello.txt");
            lock (events) Assert.Contains("upload", events);
        }
        finally
        {
            File.Delete(known);
        }
    }

    [Fact]
    public async Task ReadOnlyAndWrongPassword()
    {
        var port = FreePort();
        using var server = new LocalFtpServer(new FtpServerOptions { Root = _root, Port = port, Bind = IPAddress.Loopback, User = "u", Password = "right", ReadOnly = true, PassiveFrom = 0, PassiveTo = 0 });
        server.Start();
        var known = Path.Combine(_root + "-known2.json");
        using (var bad = Client(port, "u", "wrong", false, new Prompts(), known))
        {
            await Assert.ThrowsAnyAsync<Exception>(() => bad.ConnectAsync(CancellationToken.None));
        }

        using var c = Client(port, "u", "right", false, new Prompts(), known);
        await c.ConnectAsync(CancellationToken.None);
        await Assert.ThrowsAnyAsync<Exception>(() => c.UploadAsync(new MemoryStream([1]), "/x.bin", null, CancellationToken.None));
        Assert.False(File.Exists(Path.Combine(_root, "x.bin")));
        await Assert.ThrowsAnyAsync<Exception>(() => c.DeleteFileAsync("/hello.txt", CancellationToken.None));
        Assert.True(File.Exists(Path.Combine(_root, "hello.txt")));
    }

    [Fact]
    public async Task TlsIsRequiredWhenEnabled()
    {
        var port = FreePort();
        var cert = LocalFtpServer.LoadOrCreateCertificate(Path.Combine(_root + "-cert2", "ftp.pfx"));
        var again = LocalFtpServer.LoadOrCreateCertificate(Path.Combine(_root + "-cert2", "ftp.pfx"));
        Assert.Equal(cert.Thumbprint, again.Thumbprint);
        using var server = new LocalFtpServer(new FtpServerOptions { Root = _root, Port = port, Bind = IPAddress.Loopback, User = "u", Password = "p", Certificate = cert, PassiveFrom = 0, PassiveTo = 0 });
        server.Start();
        using var plain = Client(port, "u", "p", false, new Prompts(), Path.Combine(_root + "-known3.json"));
        var ex = await Assert.ThrowsAnyAsync<Exception>(() => plain.ConnectAsync(CancellationToken.None));
        Assert.Contains("TLS", ex.Message);
        Directory.Delete(_root + "-cert2", true);
    }
}
