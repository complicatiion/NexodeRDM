using Nexode.Core.Model;

namespace Nexode.Core.Files;

public enum SecretExportMode
{
    /// <summary>Structure and user names only, no passwords.</summary>
    None = 0,
    /// <summary>The whole payload is encrypted with the export password.</summary>
    EncryptAll = 1,
    /// <summary>Structure readable, every secret encrypted on its own (diff-friendly).</summary>
    SecretsOnly = 2
}

public sealed class ExportedSecret
{
    public Guid EntryId { get; set; }
    public string Field { get; set; } = "";
    /// <summary>Plain value (only inside an encrypted payload).</summary>
    public string? Value { get; set; }
    /// <summary>Base64 ciphertext (secrets-only mode).</summary>
    public string? Encrypted { get; set; }
}

/// <summary>Payload of kind "connections".</summary>
public sealed class ConnectionsPayload
{
    public string SourceName { get; set; } = "";
    public SecretExportMode SecretMode { get; set; }
    /// <summary>Key derivation of secrets-only mode (salt etc.).</summary>
    public FileEncryption? SecretEncryption { get; set; }
    public List<Entry> Entries { get; set; } = [];
    public List<Category> Categories { get; set; } = [];
    public List<EnvironmentDef> Environments { get; set; } = [];
    public List<ConnectionProfile> Profiles { get; set; } = [];
    public List<ExportedSecret> Secrets { get; set; } = [];
}
