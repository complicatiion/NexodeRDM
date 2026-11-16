using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Nexode.Core.Security;

namespace Nexode.Platform;

/// <summary>Picks the strongest available OS key protection.</summary>
public static class KeyProtectors
{
    public static IKeyProtector Create(string keyDirectory) =>
        OperatingSystem.IsWindows() ? new DpapiKeyProtector() : new FileKeyProtector(keyDirectory);

    public static IKeyProtector? ById(string? id, string keyDirectory) => id switch
    {
        "dpapi" when OperatingSystem.IsWindows() => new DpapiKeyProtector(),
        "file" => new FileKeyProtector(keyDirectory),
        _ => null
    };
}

/// <summary>Windows DPAPI (current user): the key can only be read by this Windows account.</summary>
public sealed partial class DpapiKeyProtector : IKeyProtector
{
    public string Id => "dpapi";
    public bool IsStrong => true;

    public byte[] Protect(byte[] key, string scope) => Crypt(key, scope, protect: true);

    public byte[] Unprotect(byte[] blob, string scope) => Crypt(blob, scope, protect: false);

    private static unsafe byte[] Crypt(byte[] data, string scope, bool protect)
    {
        var entropy = Encoding.UTF8.GetBytes("nexode:" + scope);
        fixed (byte* pData = data)
        fixed (byte* pEntropy = entropy)
        {
            var input = new DataBlob { cbData = data.Length, pbData = (nint)pData };
            var ent = new DataBlob { cbData = entropy.Length, pbData = (nint)pEntropy };
            var ok = protect
                ? CryptProtectData(ref input, null, ref ent, 0, 0, CryptprotectUiForbidden, out var output)
                : CryptUnprotectData(ref input, 0, ref ent, 0, 0, CryptprotectUiForbidden, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastPInvokeError());
            try
            {
                var result = new byte[output.cbData];
                Marshal.Copy(output.pbData, result, 0, output.cbData);
                return result;
            }
            finally
            {
                LocalFree(output.pbData);
            }
        }
    }

    private const int CryptprotectUiForbidden = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob
    {
        public int cbData;
        public nint pbData;
    }

    [LibraryImport("crypt32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptProtectData(ref DataBlob dataIn, string? description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("crypt32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CryptUnprotectData(ref DataBlob dataIn, nint description, ref DataBlob entropy, nint reserved, nint prompt, int flags, out DataBlob dataOut);

    [LibraryImport("kernel32.dll")]
    private static partial nint LocalFree(nint mem);
}

/// <summary>
/// Fallback without an OS key store: a wrap key in a file readable only by the user (0600). Weak against anyone
/// who can read the user's files – the UI recommends a master password.
/// </summary>
public sealed class FileKeyProtector(string directory) : IKeyProtector
{
    public string Id => "file";
    public bool IsStrong => false;

    public byte[] Protect(byte[] key, string scope) => VaultCrypto.Encrypt(WrapKey(scope, create: true), key, Aad(scope));

    public byte[] Unprotect(byte[] blob, string scope) => VaultCrypto.Decrypt(WrapKey(scope, create: false), blob, Aad(scope));

    private static byte[] Aad(string scope) => Encoding.UTF8.GetBytes("nexode.file-key:" + scope);

    private byte[] WrapKey(string scope, bool create)
    {
        var path = Path.Combine(directory, scope + ".key");
        if (File.Exists(path)) return File.ReadAllBytes(path);
        if (!create) throw new CryptographicException($"Key file missing: {path}");

        Directory.CreateDirectory(directory);
        var key = VaultCrypto.NewKey();
        using (var fs = new FileStream(path, FileMode.CreateNew, FileAccess.Write))
        {
            fs.Write(key);
        }

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }

        return key;
    }
}
