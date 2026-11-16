using System.Security.Cryptography;
using Nexode.Core.Files;
using Nexode.Core.Model;
using Nexode.Core.Security;
using Nexode.Core.Sync;
using Nexode.Core.Tree;
using Xunit;

namespace Nexode.Core.Tests;

public class SecurityTests
{
    private sealed class TestProtector : IKeyProtector
    {
        private readonly byte[] _key = VaultCrypto.NewKey();
        public string Id => "test";
        public bool IsStrong => true;
        public byte[] Protect(byte[] key, string scope) => VaultCrypto.Encrypt(_key, key, System.Text.Encoding.UTF8.GetBytes(scope));
        public byte[] Unprotect(byte[] blob, string scope) => VaultCrypto.Decrypt(_key, blob, System.Text.Encoding.UTF8.GetBytes(scope));
    }

    [Fact]
    public void Vault_PasswordLifecycle()
    {
        var protector = new TestProtector();
        var vault = Vault.CreateWithPassword("ds1", "correct horse", protector);
        var id = Guid.NewGuid();
        var blob = vault.EncryptSecret(id, "password", "S3cret!");

        var reopened = Vault.Open(vault.Header, "ds1", protector);
        Assert.False(reopened.IsUnlocked);
        Assert.False(reopened.TryUnlock("wrong"));
        Assert.True(reopened.TryUnlock("correct horse"));
        Assert.Equal("S3cret!", reopened.DecryptSecret(id, "password", blob));

        // Ciphertext is bound to its entry.
        Assert.ThrowsAny<CryptographicException>(() => reopened.DecryptSecret(Guid.NewGuid(), "password", blob));

        var header = reopened.RemovePassword();
        var system = Vault.Open(header, "ds1", protector);
        Assert.True(system.IsUnlocked);
        Assert.Equal("S3cret!", system.DecryptSecret(id, "password", blob));

        var withPwd = system.SetPassword("new pass");
        var again = Vault.Open(withPwd, "ds1", protector);
        Assert.True(again.TryUnlock("new pass"));
        Assert.Equal("S3cret!", again.DecryptSecret(id, "password", blob));
    }

    [Fact]
    public void Hlc_IsMonotonic()
    {
        long t = 1000;
        var clock = new HlcClock("a", () => t);
        var a = clock.Next();
        var b = clock.Next();
        t = 900; // clock skew backwards
        var c = clock.Next();
        Assert.True(a < b && b < c);
        Assert.Equal(c, Hlc.Parse(c.ToString()));
    }

    private static (EntryTree Tree, Entry Root, Entry Child, Entry Profile) Sample()
    {
        var root = new Entry { Kind = EntryKind.Folder, Name = "Root", CredentialMode = CredentialMode.Own, Username = "admin", SortKey = "V" };
        var sub = new Entry { Kind = EntryKind.Folder, Name = "Linux", ParentId = root.Id, SortKey = "V" };
        var child = new Entry { Kind = EntryKind.Ssh, Name = "web01", Host = "web01", ParentId = sub.Id, SortKey = "V" };
        var profile = new Entry { Kind = EntryKind.Credential, Name = "svc", Username = "svc", SortKey = "k" };
        var cmd = new Entry { Kind = EntryKind.Command, Name = "tool", ParentId = sub.Id, CredentialMode = CredentialMode.Profile, CredentialId = profile.Id, SortKey = "W" };
        return (new EntryTree([root, sub, child, profile, cmd]), root, sub, profile);
    }

    [Theory]
    [InlineData(SecretExportMode.EncryptAll)]
    [InlineData(SecretExportMode.SecretsOnly)]
    public void Export_Import_RoundTrip(SecretExportMode mode)
    {
        var (tree, root, sub, profile) = Sample();
        var secrets = new Dictionary<Guid, string> { [root.Id] = "rootpw", [profile.Id] = "svcpw" };
        var file = ExportBuilder.Build(tree, [], [], [], "Test", "0.0.1",
            new ExportOptions { Roots = [sub.Id], SecretMode = mode, Password = "export-pw" },
            (id, _) => secrets.GetValueOrDefault(id));

        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".nexode");
        file.Save(path);
        var loaded = NexodeFile.Load(path);
        File.Delete(path);
        Assert.Equal(mode == SecretExportMode.EncryptAll, loaded.IsEncrypted);
        Assert.True(ExportBuilder.NeedsPassword(loaded));
        if (mode == SecretExportMode.EncryptAll) Assert.ThrowsAny<CryptographicException>(() => loaded.ReadPayload("bad"));

        var payload = ExportBuilder.ReadConnections(loaded.ReadPayload("export-pw"), "export-pw");
        // Sub folder, ssh, command + referenced profile; root not included.
        Assert.Equal(4, payload.Entries.Count);
        var subCopy = payload.Entries.Single(e => e.Id == sub.Id);
        Assert.Null(subCopy.ParentId);
        Assert.Equal(CredentialMode.Own, subCopy.CredentialMode);
        Assert.Equal("admin", subCopy.Username);
        Assert.Contains(payload.Secrets, s => s.EntryId == sub.Id && s.Value == "rootpw");
        Assert.Contains(payload.Secrets, s => s.EntryId == profile.Id && s.Value == "svcpw");

        // Import into an empty source.
        var plan = ImportPlanner.Plan(payload, new Dictionary<Guid, Entry>());
        Assert.All(plan, p => Assert.Equal(ImportStatus.New, p.Status));
        var clock = new HlcClock("t");
        var result = ImportPlanner.Apply(payload, plan, null, null, ConflictPolicy.Overwrite, clock.Next, _ => "V");
        Assert.Equal(4, result.Added);
        Assert.Equal(1, result.NeedsReview);
        Assert.Equal(2, result.Secrets.Count);

        // Re-import: identical → skipped; keep-both on change → new ids.
        var existing = result.Upserts.ToDictionary(e => e.Id);
        Assert.All(ImportPlanner.Plan(payload, existing), p => Assert.NotEqual(ImportStatus.New, p.Status));
        existing[sub.Id].Name = "changed";
        var plan2 = ImportPlanner.Plan(payload, existing);
        var keep = ImportPlanner.Apply(payload, plan2, null, null, ConflictPolicy.KeepBoth, clock.Next, _ => "X");
        Assert.Equal(1, keep.Added);
        Assert.NotEqual(sub.Id, keep.Upserts.Single().Id);
    }
}
