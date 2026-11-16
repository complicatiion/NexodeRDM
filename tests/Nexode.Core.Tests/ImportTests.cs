using Nexode.Core.Import;
using Nexode.Core.Model;
using Xunit;

namespace Nexode.Core.Tests;

public class ImportTests
{
    private const string MRemote = """
        <?xml version="1.0" encoding="utf-8"?>
        <mrng:Connections xmlns:mrng="http://mremoteng.org" Name="Connections" Export="false" EncryptionEngine="AES" BlockCipherMode="GCM" KdfIterations="1000" FullFileEncryption="false" Protected="m9bJyl+G1INXn2yknh+1Rs/1ahChw/VaSLXkIDmxN7Z6V7dprVVGJuB+07yUdXruvV7ldDx1oyvRQYV3lqvRdIUo" ConfVersion="2.6">
          <Node Name="Customer A" Type="Container" Id="11111111-1111-1111-1111-111111111111" Username="admin" Domain="CORP" Password="" InheritUsername="false" InheritPassword="false">
            <Node Name="dc01" Type="Connection" Id="22222222-2222-2222-2222-222222222222" Hostname="dc01.corp.local" Protocol="RDP" Port="3389" Username="" Password="" InheritUsername="true" InheritPassword="true" ConnectToConsole="true" />
            <Node Name="web" Type="Connection" Id="33333333-3333-3333-3333-333333333333" Hostname="web01" Protocol="SSH2" Port="2222" Username="root" Password="IPVDUptM9ble/7SaxfQAt5YRpkmfa8tgtaGO0G1deNcyCkU3SOU5LEyGO12EqUHK40z7rpzaUw==" InheritUsername="false" InheritPassword="false" />
            <Node Name="panel" Type="Connection" Id="44444444-4444-4444-4444-444444444444" Hostname="panel.local" Protocol="HTTPS" Port="8443" />
          </Node>
        </mrng:Connections>
        """;

    [Fact]
    public void ReadsMRemoteNgWithDefaultPassword()
    {
        var r = ForeignImporters.ReadMRemoteNg(MRemote);
        Assert.Equal(4, r.Entries.Count);
        var folder = r.Entries[0];
        Assert.Equal(EntryKind.Folder, folder.Kind);
        Assert.Equal(CredentialMode.Own, folder.CredentialMode);
        var dc = r.Entries.Single(e => e.Name == "dc01");
        Assert.Equal(folder.Id, dc.ParentId);
        Assert.Equal(CredentialMode.Inherit, dc.CredentialMode);
        Assert.Null(dc.Port);
        Assert.Equal("1", dc.Setting(SettingKeys.RdpAdmin));
        var web = r.Entries.Single(e => e.Name == "web");
        Assert.Equal(EntryKind.Ssh, web.Kind);
        Assert.Equal(2222, web.Port);
        Assert.Equal("S3cret!", r.Secrets.Single(s => s.EntryId == web.Id).Value);
        Assert.Equal("https://panel.local:8443", r.Entries.Single(e => e.Name == "panel").Url);
        // Stable ids: the same file imports as "identical" the second time.
        Assert.Equal(r.Entries[1].Id, ForeignImporters.ReadMRemoteNg(MRemote).Entries[1].Id);
    }

    [Fact]
    public void MRemoteNgMasterPassword()
    {
        var xml = MRemote.Replace("m9bJyl+G1INXn2yknh+1Rs/1ahChw/VaSLXkIDmxN7Z6V7dprVVGJuB+07yUdXruvV7ldDx1oyvRQYV3lqvRdIUo",
                "wfdFQYK4i3uSTDgTu/S1Akk1Ua6Ttit95xaRvU5Hza1rtX1AR3DJTJjQn/mlOxyawwA2J/J1M/u6762SEdRO")
            .Replace("IPVDUptM9ble/7SaxfQAt5YRpkmfa8tgtaGO0G1deNcyCkU3SOU5LEyGO12EqUHK40z7rpzaUw==", "+RUtpX1cYce62Ll3E+V5tXQyFNwX277mbJAf4BXkBf701vbb0r9rdSq+TKITSeDNuRHk");
        Assert.Throws<ForeignImporters.PasswordRequiredException>(() => ForeignImporters.ReadMRemoteNg(xml));
        var r = ForeignImporters.ReadMRemoteNg(xml, "master");
        Assert.Equal("pw2", r.Secrets.Single().Value);
    }

    [Fact]
    public void ReadsRdcMan()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <RDCMan programVersion="2.90" schemaVersion="3">
              <file>
                <properties><name>Datacenter</name></properties>
                <group>
                  <properties><name>Domain Controllers</name></properties>
                  <logonCredentials inherit="None"><profileName scope="Local">Custom</profileName><userName>adm</userName><password>AQAAAA==</password><domain>CORP</domain></logonCredentials>
                  <server><properties><displayName>DC 1</displayName><name>dc1.corp.local</name></properties>
                    <connectionSettings inherit="None"><connectToConsole>True</connectToConsole><port>3390</port></connectionSettings></server>
                  <server><properties><name>dc2.corp.local</name></properties></server>
                </group>
              </file>
            </RDCMan>
            """;
        var r = ForeignImporters.ReadRdcMan(xml, b => System.Text.Encoding.Unicode.GetBytes("pw"));
        Assert.Equal("Datacenter", r.SourceName);
        Assert.Equal(3, r.Entries.Count);
        var dc1 = r.Entries.Single(e => e.Name == "DC 1");
        Assert.Equal("dc1.corp.local", dc1.Host);
        Assert.Equal(3390, dc1.Port);
        Assert.Equal(CredentialMode.Inherit, dc1.CredentialMode);
        Assert.Equal("adm", r.Entries[0].Username);
        Assert.Equal("pw", r.Secrets.Single().Value);
    }

    [Fact]
    public void ReadsRdpFileSshConfigAndPutty()
    {
        var rdp = ForeignImporters.ReadRdpFile("screen mode id:i:2\r\nfull address:s:srv01.example.com:3391\r\nusername:s:EXAMPLE\\jdoe\r\nadministrative session:i:1\r\n", "srv01.rdp", null);
        var e = rdp.Entries.Single();
        Assert.Equal(("srv01.example.com", 3391, "EXAMPLE", "jdoe"), (e.Host, e.Port, e.Domain, e.Username));
        Assert.Equal("1", e.Setting(SettingKeys.RdpAdmin));

        var ssh = ForeignImporters.ReadSshConfig("Host *\n  User nobody\nHost web web-alias\n  HostName 10.0.0.5\n  Port 2200\n  User deploy\n  IdentityFile ~/.ssh/id_ed25519\nHost db\n  HostName db.local\n");
        Assert.Equal(3, ssh.Entries.Count);
        var web = ssh.Entries[0];
        Assert.Equal(("web", "10.0.0.5", 2200, "deploy"), (web.Name, web.Host, web.Port, web.Username));
        Assert.Equal("~/.ssh/id_ed25519", web.Setting(SettingKeys.SshKeyFile));
        Assert.Equal("db.local", ssh.Entries[2].Host);

        var putty = ForeignImporters.ReadPutty([
            ("Default%20Settings", new Dictionary<string, string>()),
            ("router%20core", new Dictionary<string, string> { ["HostName"] = "admin@10.0.0.1", ["PortNumber"] = "23", ["Protocol"] = "telnet" }),
            ("web", new Dictionary<string, string> { ["HostName"] = "web.local", ["PortNumber"] = "22", ["Protocol"] = "ssh", ["UserName"] = "root" })
        ]);
        Assert.Equal(2, putty.Entries.Count);
        Assert.Equal(("router core", EntryKind.Telnet, "10.0.0.1", "admin"), (putty.Entries[0].Name, putty.Entries[0].Kind, putty.Entries[0].Host, putty.Entries[0].Username));
        Assert.Null(putty.Entries[1].Port);
    }
}

public class StoreCategoryTests
{
    [Fact]
    public void BuiltInCategoriesPersist()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexode-cat-" + Guid.NewGuid().ToString("N") + ".db");
        using (var store = Nexode.Storage.DataSourceStore.Open(path, "t"))
        {
            store.SaveCategories([new Category { Id = "server", Name = "Server", BuiltIn = true }, new Category { Id = "c-1", Name = "Mine" }]);
        }

        using (var store = Nexode.Storage.DataSourceStore.Open(path, "t"))
        {
            var list = store.LoadCategories();
            Assert.Contains(list, c => c.Id == "server" && c.BuiltIn);
            Assert.Contains(list, c => c.Id == "c-1");
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        File.Delete(path);
    }

    [Fact]
    public void FullEncryptionHidesEntryRows()
    {
        var path = Path.Combine(Path.GetTempPath(), "nexode-enc-" + Guid.NewGuid().ToString("N") + ".db");
        var vault = Nexode.Core.Security.Vault.CreateWithPassword("scope", "pw-123456", null);
        var entry = new Entry { Kind = EntryKind.Ssh, Name = "secret-host", Host = "10.1.2.3" };
        using (var store = Nexode.Storage.DataSourceStore.Open(path, "t"))
        {
            store.Reencrypt([entry], new TestCipher(vault));
            Assert.True(store.IsFullyEncrypted);
        }

        using (var store = Nexode.Storage.DataSourceStore.Open(path, "t"))
        {
            Assert.Empty(store.LoadEntries()); // no cipher: nothing readable
            store.Cipher = new TestCipher(vault);
            Assert.Equal("10.1.2.3", store.LoadEntries().Single().Host);
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        Assert.DoesNotContain("secret-host", System.Text.Encoding.UTF8.GetString(File.ReadAllBytes(path)));
        File.Delete(path);
    }

    private sealed class TestCipher(Nexode.Core.Security.Vault v) : Nexode.Storage.IEntryCipher
    {
        public string Protect(Guid id, string json) => v.EncryptText(id, "entry", json);
        public string? Unprotect(Guid id, string data) => v.DecryptText(id, "entry", data);
    }
}
