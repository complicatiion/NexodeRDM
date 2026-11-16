using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Nexode.Core.Security;

namespace Nexode.Core.Files;

/// <summary>Content types of a .nexode file.</summary>
public static class NexodeKinds
{
    public const string Settings = "settings";
    public const string Theme = "theme";
    public const string Connections = "connections";
}

/// <summary>Key derivation and cipher of an encrypted payload.</summary>
public sealed class FileEncryption
{
    public string Kdf { get; set; } = "argon2id";
    public int Memory { get; set; }
    public int Iterations { get; set; }
    public int Parallelism { get; set; }
    public string Salt { get; set; } = "";
    public string Cipher { get; set; } = "aes-256-gcm";
}

/// <summary>
/// The .nexode envelope: a readable JSON header and a typed payload, optionally encrypted with an export password.
/// </summary>
public sealed class NexodeFile
{
    public const string Extension = ".nexode";
    public const int CurrentFormatVersion = 1;
    private static readonly byte[] PayloadAad = "nexode.file.v1"u8.ToArray();

    public string Format { get; set; } = "nexode";
    public string Kind { get; set; } = "";
    public int FormatVersion { get; set; } = CurrentFormatVersion;
    public string AppVersion { get; set; } = "";
    public DateTime Created { get; set; } = DateTime.UtcNow;
    public string? Description { get; set; }
    public FileEncryption? Encryption { get; set; }
    public JsonNode? Payload { get; set; }
    /// <summary>Base64 ciphertext when <see cref="Encryption"/> is set.</summary>
    public string? EncryptedPayload { get; set; }

    [JsonIgnore]
    public bool IsEncrypted => Encryption is not null;

    /// <summary>Upper limit for files read: protects against oversized input.</summary>
    public const long MaxFileBytes = 256L * 1024 * 1024;

    public static NexodeFile Create(string kind, string appVersion, JsonNode payload, string? password)
    {
        var file = new NexodeFile { Kind = kind, AppVersion = appVersion };
        if (string.IsNullOrEmpty(password))
        {
            file.Payload = payload;
            return file;
        }

        var kdf = KdfParams.CreateDefault();
        var key = VaultCrypto.DeriveKey(password, kdf);
        try
        {
            var plain = Encoding.UTF8.GetBytes(payload.ToJsonString());
            file.EncryptedPayload = Convert.ToBase64String(VaultCrypto.Encrypt(key, plain, PayloadAad));
            CryptographicOperations.ZeroMemory(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }

        file.Encryption = new FileEncryption
        {
            Memory = kdf.MemoryKiB, Iterations = kdf.Iterations, Parallelism = kdf.Parallelism, Salt = Convert.ToBase64String(kdf.Salt)
        };
        return file;
    }

    /// <summary>The payload; decrypts with <paramref name="password"/> when encrypted. Throws on a wrong password.</summary>
    public JsonNode ReadPayload(string? password)
    {
        if (Encryption is null)
        {
            return Payload ?? throw new InvalidDataException("The file has no content.");
        }

        if (string.IsNullOrEmpty(password)) throw new CryptographicException("Password required.");
        if (Encryption.Memory is < 8 or > 4 * 1024 * 1024 || Encryption.Iterations is < 1 or > 100 || Encryption.Parallelism is < 1 or > 64)
        {
            throw new InvalidDataException("Unsupported key derivation parameters.");
        }

        var kdf = new KdfParams(Encryption.Memory, Encryption.Iterations, Encryption.Parallelism, Convert.FromBase64String(Encryption.Salt));
        var key = VaultCrypto.DeriveKey(password, kdf);
        try
        {
            var plain = VaultCrypto.Decrypt(key, Convert.FromBase64String(EncryptedPayload ?? ""), PayloadAad);
            try
            {
                return JsonNode.Parse(plain, documentOptions: ReadOptions) ?? throw new InvalidDataException("Empty content.");
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    public static readonly JsonDocumentOptions ReadOptions = new() { MaxDepth = 64, AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public void Save(string path)
    {
        var json = JsonSerializer.Serialize(this, NexodeJson.Default.NexodeFile);
        var temp = path + ".tmp";
        File.WriteAllText(temp, json, new UTF8Encoding(false));
        File.Move(temp, path, overwrite: true);
    }

    public static NexodeFile Load(string path)
    {
        var info = new FileInfo(path);
        if (info.Length > MaxFileBytes) throw new InvalidDataException("The file is too large.");
        using var stream = File.OpenRead(path);
        var file = JsonSerializer.Deserialize(stream, NexodeJson.Default.NexodeFile)
                   ?? throw new InvalidDataException("Not a Nexode file.");
        if (!string.Equals(file.Format, "nexode", StringComparison.Ordinal)) throw new InvalidDataException("Not a Nexode file.");
        if (file.FormatVersion > CurrentFormatVersion)
        {
            throw new InvalidDataException($"The file was created by a newer Nexode version ({file.AppVersion}). Please update Nexode.");
        }

        return file;
    }
}
