using System.Security.Cryptography;
using System.Text;

namespace Nexode.Core.Security;

public enum VaultMode
{
    /// <summary>Data key protected by the operating system (no master password).</summary>
    System = 0,
    /// <summary>Data key wrapped with a key derived from the master password.</summary>
    Password = 1
}

/// <summary>Persisted vault header (stored in the data source meta table).</summary>
public sealed class VaultHeader
{
    public VaultMode Mode { get; set; }
    public string? ProtectorId { get; set; }
    public byte[]? ProtectedKey { get; set; }
    public KdfParams? Kdf { get; set; }
    public byte[]? WrappedKey { get; set; }
}

/// <summary>
/// Envelope encryption: one random data key (DEK) per data source encrypts all secrets; the DEK itself is wrapped
/// by the master password (Argon2id) or by the OS. Changing or removing the password only re-wraps the DEK.
/// </summary>
public sealed class Vault : IDisposable
{
    private readonly IKeyProtector? _protector;
    private byte[]? _dek;

    private Vault(VaultHeader header, string scope, IKeyProtector? protector)
    {
        Header = header;
        Scope = scope;
        _protector = protector;
    }

    public VaultHeader Header { get; private set; }
    public string Scope { get; }
    public bool IsUnlocked => _dek is not null;
    public bool HasPassword => Header.Mode == VaultMode.Password;
    public bool IsWeak => Header.Mode == VaultMode.System && _protector is { IsStrong: false };

    public event Action? StateChanged;

    public static Vault CreateSystem(string scope, IKeyProtector protector)
    {
        var dek = VaultCrypto.NewKey();
        var header = new VaultHeader { Mode = VaultMode.System, ProtectorId = protector.Id, ProtectedKey = protector.Protect(dek, scope) };
        return new Vault(header, scope, protector) { _dek = dek };
    }

    public static Vault CreateWithPassword(string scope, string password, IKeyProtector? protector)
    {
        var dek = VaultCrypto.NewKey();
        var vault = new Vault(new VaultHeader(), scope, protector) { _dek = dek };
        vault.Header = vault.Wrap(dek, password);
        return vault;
    }

    /// <summary>Opens an existing vault; system vaults unlock immediately.</summary>
    public static Vault Open(VaultHeader header, string scope, IKeyProtector? protector)
    {
        var vault = new Vault(header, scope, protector);
        if (header.Mode == VaultMode.System && protector is not null && header.ProtectedKey is not null)
        {
            vault._dek = protector.Unprotect(header.ProtectedKey, scope);
        }

        return vault;
    }

    public bool TryUnlock(string password)
    {
        if (Header.Mode != VaultMode.Password || Header.Kdf is null || Header.WrappedKey is null) return IsUnlocked;
        var kek = VaultCrypto.DeriveKey(password, Header.Kdf);
        try
        {
            _dek = VaultCrypto.Decrypt(kek, Header.WrappedKey, VaultCrypto.DekAad);
            StateChanged?.Invoke();
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>Unlocks without raising <see cref="StateChanged"/> (for background threads; call <see cref="RaiseStateChanged"/> on the UI thread).</summary>
    public bool TryUnlockQuiet(string password)
    {
        if (Header.Mode != VaultMode.Password || Header.Kdf is null || Header.WrappedKey is null) return IsUnlocked;
        var kek = VaultCrypto.DeriveKey(password, Header.Kdf);
        try
        {
            _dek = VaultCrypto.Decrypt(kek, Header.WrappedKey, VaultCrypto.DekAad);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    /// <summary>True when the password opens the data key (state unchanged).</summary>
    public bool VerifyPassword(string password)
    {
        if (Header.Mode != VaultMode.Password || Header.Kdf is null || Header.WrappedKey is null) return true;
        var kek = VaultCrypto.DeriveKey(password, Header.Kdf);
        try
        {
            var dek = VaultCrypto.Decrypt(kek, Header.WrappedKey, VaultCrypto.DekAad);
            CryptographicOperations.ZeroMemory(dek);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public void RaiseStateChanged() => StateChanged?.Invoke();

    public void Lock()
    {
        if (Header.Mode != VaultMode.Password || _dek is null) return;
        CryptographicOperations.ZeroMemory(_dek);
        _dek = null;
        StateChanged?.Invoke();
    }

    /// <summary>Sets or changes the master password (vault must be unlocked).</summary>
    public VaultHeader SetPassword(string password)
    {
        var dek = _dek ?? throw new InvalidOperationException("Vault is locked.");
        Header = Wrap(dek, password);
        StateChanged?.Invoke();
        return Header;
    }

    /// <summary>Removes the master password; the OS protects the data key from now on.</summary>
    public VaultHeader RemovePassword()
    {
        var dek = _dek ?? throw new InvalidOperationException("Vault is locked.");
        var protector = _protector ?? throw new InvalidOperationException("No system key protection available.");
        Header = new VaultHeader { Mode = VaultMode.System, ProtectorId = protector.Id, ProtectedKey = protector.Protect(dek, Scope) };
        StateChanged?.Invoke();
        return Header;
    }

    public byte[] EncryptSecret(Guid entryId, string field, string value)
    {
        var dek = _dek ?? throw new InvalidOperationException("Vault is locked.");
        var plain = Encoding.UTF8.GetBytes(value);
        try
        {
            return VaultCrypto.Encrypt(dek, plain, VaultCrypto.SecretAad(entryId, field));
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    public string DecryptSecret(Guid entryId, string field, byte[] blob)
    {
        var dek = _dek ?? throw new InvalidOperationException("Vault is locked.");
        var plain = VaultCrypto.Decrypt(dek, blob, VaultCrypto.SecretAad(entryId, field));
        try
        {
            return Encoding.UTF8.GetString(plain);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    /// <summary>Encrypts arbitrary text bound to an entry and purpose (used for full data source encryption).</summary>
    public string EncryptText(Guid id, string purpose, string text) => Convert.ToBase64String(EncryptSecret(id, purpose, text));

    public string? DecryptText(Guid id, string purpose, string base64)
    {
        try
        {
            return DecryptSecret(id, purpose, Convert.FromBase64String(base64));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private VaultHeader Wrap(byte[] dek, string password)
    {
        var kdf = KdfParams.CreateDefault();
        var kek = VaultCrypto.DeriveKey(password, kdf);
        try
        {
            return new VaultHeader { Mode = VaultMode.Password, Kdf = kdf, WrappedKey = VaultCrypto.Encrypt(kek, dek, VaultCrypto.DekAad) };
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
    }

    public void Dispose()
    {
        if (_dek is not null) CryptographicOperations.ZeroMemory(_dek);
        _dek = null;
    }
}
