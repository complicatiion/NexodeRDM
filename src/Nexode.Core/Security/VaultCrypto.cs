using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;

namespace Nexode.Core.Security;

/// <summary>Argon2id parameters stored next to the wrapped key.</summary>
public sealed record KdfParams(int MemoryKiB, int Iterations, int Parallelism, byte[] Salt)
{
    /// <summary>~0.5 s on a current desktop CPU (managed Argon2id).</summary>
    public static KdfParams CreateDefault() => new(65536, 3, 1, RandomNumberGenerator.GetBytes(16));
}

/// <summary>
/// Crypto primitives: Argon2id (BouncyCastle) for password keys, AES-256-GCM (.NET) for data.
/// Blob layout: nonce (12) | ciphertext | tag (16).
/// </summary>
public static class VaultCrypto
{
    public const int KeySize = 32;
    private const int NonceSize = 12;
    private const int TagSize = 16;

    public static byte[] NewKey() => RandomNumberGenerator.GetBytes(KeySize);

    public static byte[] DeriveKey(string password, KdfParams p)
    {
        var builder = new Argon2Parameters.Builder(Argon2Parameters.Argon2id)
            .WithVersion(Argon2Parameters.Version13)
            .WithMemoryAsKB(p.MemoryKiB)
            .WithIterations(p.Iterations)
            .WithParallelism(p.Parallelism)
            .WithSalt(p.Salt);

        var generator = new Argon2BytesGenerator();
        generator.Init(builder.Build());
        var key = new byte[KeySize];
        var pwd = Encoding.UTF8.GetBytes(password);
        try
        {
            generator.GenerateBytes(pwd, key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pwd);
        }

        return key;
    }

    public static byte[] Encrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> plain, ReadOnlySpan<byte> aad)
    {
        var blob = new byte[NonceSize + plain.Length + TagSize];
        var nonce = blob.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var gcm = new AesGcm(key, TagSize);
        gcm.Encrypt(nonce, plain, blob.AsSpan(NonceSize, plain.Length), blob.AsSpan(NonceSize + plain.Length), aad);
        return blob;
    }

    /// <summary>Throws <see cref="CryptographicException"/> on a wrong key or tampered data.</summary>
    public static byte[] Decrypt(ReadOnlySpan<byte> key, ReadOnlySpan<byte> blob, ReadOnlySpan<byte> aad)
    {
        if (blob.Length < NonceSize + TagSize) throw new CryptographicException("Invalid encrypted data.");
        var length = blob.Length - NonceSize - TagSize;
        var plain = new byte[length];
        using var gcm = new AesGcm(key, TagSize);
        gcm.Decrypt(blob[..NonceSize], blob.Slice(NonceSize, length), blob[(NonceSize + length)..], plain, aad);
        return plain;
    }

    /// <summary>AAD binding a secret to its entry and field, so ciphertexts cannot be swapped between entries.</summary>
    public static byte[] SecretAad(Guid entryId, string field)
    {
        var aad = new byte[16 + Encoding.UTF8.GetByteCount(field)];
        entryId.TryWriteBytes(aad);
        Encoding.UTF8.GetBytes(field, aad.AsSpan(16));
        return aad;
    }

    public static readonly byte[] DekAad = "nexode.dek.v1"u8.ToArray();
}

/// <summary>Protects the data key without a master password (DPAPI, key file …).</summary>
public interface IKeyProtector
{
    /// <summary>Stable id stored in the data source ("dpapi", "file").</summary>
    string Id { get; }

    /// <summary>False when the protection is weak (plain key file) – the UI warns.</summary>
    bool IsStrong { get; }

    byte[] Protect(byte[] key, string scope);

    byte[] Unprotect(byte[] blob, string scope);
}
