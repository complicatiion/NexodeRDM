using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Nexode.Core.Model;
using Nexode.Core.Security;
using Nexode.Core.Tree;

namespace Nexode.Core.Files;

public sealed class ExportOptions
{
    /// <summary>Roots to export; empty = everything.</summary>
    public IReadOnlyCollection<Guid> Roots { get; init; } = [];
    public SecretExportMode SecretMode { get; init; }
    public string? Password { get; init; }
    /// <summary>Write inherited credentials into the export roots so a partial export works on its own.</summary>
    public bool FlattenInheritance { get; init; } = true;
}

/// <summary>Builds the "connections" payload from a tree.</summary>
public static class ExportBuilder
{
    public static readonly string[] SecretFields = ["password"];

    /// <param name="readSecret">Returns the plain secret of an entry field, or null.</param>
    public static NexodeFile Build(EntryTree tree, IEnumerable<Category> categories, IEnumerable<EnvironmentDef> environments, IEnumerable<ConnectionProfile> profiles, string sourceName, string appVersion,
        ExportOptions options, Func<Guid, string, string?> readSecret)
    {
        var resolver = new Resolver(tree);
        var selected = new List<Entry>();
        var ids = new HashSet<Guid>();

        void Add(Entry e)
        {
            if (ids.Add(e.Id)) selected.Add(e.Clone());
        }

        var roots = options.Roots.Count == 0
            ? tree.Roots.ToList()
            : options.Roots.Select(id => tree.Get(id)).OfType<Entry>()
                // Drop roots that sit below another selected root.
                .Where(r => !options.Roots.Any(o => o != r.Id && tree.IsSelfOrDescendant(o, r.Id))).ToList();

        foreach (var root in roots)
        {
            Add(root);
            foreach (var d in tree.Descendants(root.Id)) Add(d);
        }

        var partial = options.Roots.Count > 0;
        foreach (var e in selected.ToList())
        {
            // Referenced credential profiles outside the selection travel along (at the export root).
            if (e.CredentialMode == CredentialMode.Profile && e.CredentialId is { } cid && !ids.Contains(cid) && tree.Get(cid) is { } profile)
            {
                var copy = profile.Clone();
                copy.ParentId = null;
                ids.Add(copy.Id);
                selected.Add(copy);
            }
        }

        var flattenedSecrets = new Dictionary<Guid, Guid>();
        if (partial && options.FlattenInheritance)
        {
            foreach (var root in selected.Where(e => e.ParentId is null || !ids.Contains(e.ParentId.Value)).ToList())
            {
                var original = tree.Get(root.Id);
                if (original is null || root.CredentialMode != CredentialMode.Inherit) continue;
                var eff = resolver.Resolve(original);
                if (eff.CredentialFrom is { } from && !ids.Contains(from) && eff.CredentialMode == CredentialMode.Own)
                {
                    root.CredentialMode = CredentialMode.Own;
                    root.Username = eff.Username;
                    root.Domain = eff.Domain;
                    if (eff.SecretOwner is { } owner) flattenedSecrets[root.Id] = owner;
                }
            }
        }

        foreach (var root in selected.Where(e => e.ParentId is { } p && !ids.Contains(p)))
        {
            root.ParentId = null;
        }

        var payload = new ConnectionsPayload
        {
            SourceName = sourceName,
            SecretMode = options.SecretMode,
            Entries = selected,
            Categories = categories.ToList(),
            Environments = environments.ToList()
        };
        var exported = selected.Select(e => e.Id).ToHashSet();
        foreach (var p in profiles)
        {
            var kept = p.Entries.Where(exported.Contains).ToList();
            if (kept.Count == 0) continue;
            payload.Profiles.Add(new ConnectionProfile
            {
                Id = p.Id, Name = p.Name, Icon = p.Icon, ColorHue = p.ColorHue, Entries = kept, Layout = p.Layout, Sequential = p.Sequential,
                DelayMs = p.DelayMs, Order = p.Order
            });
        }

        byte[]? secretKey = null;
        if (options.SecretMode == SecretExportMode.SecretsOnly)
        {
            var kdf = KdfParams.CreateDefault();
            secretKey = VaultCrypto.DeriveKey(options.Password ?? throw new ArgumentException("Password required."), kdf);
            payload.SecretEncryption = new FileEncryption
            {
                Memory = kdf.MemoryKiB, Iterations = kdf.Iterations, Parallelism = kdf.Parallelism, Salt = Convert.ToBase64String(kdf.Salt)
            };
        }

        try
        {
            if (options.SecretMode != SecretExportMode.None)
            {
                foreach (var e in selected)
                {
                    var owner = flattenedSecrets.GetValueOrDefault(e.Id, e.Id);
                    foreach (var field in SecretFields)
                    {
                        var value = readSecret(owner, field);
                        if (string.IsNullOrEmpty(value)) continue;
                        var secret = new ExportedSecret { EntryId = e.Id, Field = field };
                        if (secretKey is not null)
                        {
                            secret.Encrypted = Convert.ToBase64String(VaultCrypto.Encrypt(secretKey, Encoding.UTF8.GetBytes(value), VaultCrypto.SecretAad(e.Id, field)));
                        }
                        else
                        {
                            secret.Value = value;
                        }

                        payload.Secrets.Add(secret);
                    }
                }
            }
        }
        finally
        {
            if (secretKey is not null) CryptographicOperations.ZeroMemory(secretKey);
        }

        var node = JsonSerializer.SerializeToNode(payload, NexodeJson.Default.ConnectionsPayload)!;
        var password = options.SecretMode == SecretExportMode.EncryptAll ? options.Password : null;
        var file = NexodeFile.Create(NexodeKinds.Connections, appVersion, node, password);
        file.Description = $"{selected.Count} entries from \"{sourceName}\"";
        return file;
    }

    /// <summary>Reads a connections payload; decrypts secrets-only values with <paramref name="password"/>.</summary>
    public static ConnectionsPayload ReadConnections(JsonNode node, string? password)
    {
        var payload = node.Deserialize(NexodeJson.Default.ConnectionsPayload) ?? throw new InvalidDataException("Invalid content.");
        if (payload.SecretMode == SecretExportMode.SecretsOnly && payload.SecretEncryption is { } enc && payload.Secrets.Count > 0)
        {
            if (string.IsNullOrEmpty(password)) throw new CryptographicException("Password required.");
            var kdf = new KdfParams(enc.Memory, enc.Iterations, enc.Parallelism, Convert.FromBase64String(enc.Salt));
            var key = VaultCrypto.DeriveKey(password, kdf);
            try
            {
                foreach (var s in payload.Secrets.Where(s => s.Encrypted is not null))
                {
                    var plain = VaultCrypto.Decrypt(key, Convert.FromBase64String(s.Encrypted!), VaultCrypto.SecretAad(s.EntryId, s.Field));
                    s.Value = Encoding.UTF8.GetString(plain);
                    s.Encrypted = null;
                }
            }
            finally
            {
                CryptographicOperations.ZeroMemory(key);
            }
        }

        return payload;
    }

    /// <summary>True when reading needs a password (encrypted payload or encrypted secrets).</summary>
    public static bool NeedsPassword(NexodeFile file) =>
        file.IsEncrypted || (file.Payload?["secretMode"]?.GetValue<string>() is { } mode && mode == nameof(SecretExportMode.SecretsOnly));
}
