using System.Buffers.Binary;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using Org.BouncyCastle.Crypto.Generators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Security;
using Nexode.Protocols.Terminal;
using Renci.SshNet;

namespace Nexode.Protocols;

public enum SshKeyType
{
    Ed25519,
    Rsa4096,
    Rsa3072
}

/// <summary>A new key pair: private key in OpenSSH format (optionally passphrase-protected), public key as one line.</summary>
public sealed record SshKeyPair(string PrivateKey, string PublicKey, string Fingerprint);

/// <summary>
/// Key generation like ssh-keygen: "openssh-key-v1" private keys, with a passphrase encrypted as aes256-ctr with a
/// bcrypt-pbkdf derived key (16 rounds, the ssh-keygen default). Ed25519 via BouncyCastle (already used by SSH.NET),
/// RSA via .NET.
/// </summary>
public static class SshKeyGen
{
    private const int KdfRounds = 16;

    public static SshKeyPair Generate(SshKeyType type, string comment, string? passphrase)
    {
        byte[] publicBlob;
        byte[] privatePart;
        string keyType;
        if (type == SshKeyType.Ed25519)
        {
            var gen = new Ed25519KeyPairGenerator();
            gen.Init(new Ed25519KeyGenerationParameters(new SecureRandom()));
            var pair = gen.GenerateKeyPair();
            var pub = ((Ed25519PublicKeyParameters)pair.Public).GetEncoded();
            var seed = ((Ed25519PrivateKeyParameters)pair.Private).GetEncoded();
            keyType = "ssh-ed25519";
            publicBlob = Blob(w => { w.String(keyType); w.Bytes(pub); });
            privatePart = Blob(w =>
            {
                w.String(keyType);
                w.Bytes(pub);
                w.Bytes([.. seed, .. pub]);
            });
            CryptographicOperations.ZeroMemory(seed);
        }
        else
        {
            using var rsa = RSA.Create(type == SshKeyType.Rsa3072 ? 3072 : 4096);
            var k = rsa.ExportParameters(true);
            keyType = "ssh-rsa";
            publicBlob = Blob(w => { w.String(keyType); w.MpInt(k.Exponent!); w.MpInt(k.Modulus!); });
            privatePart = Blob(w =>
            {
                w.String(keyType);
                w.MpInt(k.Modulus!);
                w.MpInt(k.Exponent!);
                w.MpInt(k.D!);
                w.MpInt(k.InverseQ!);
                w.MpInt(k.P!);
                w.MpInt(k.Q!);
            });
        }

        var encrypted = !string.IsNullOrEmpty(passphrase);
        var blockSize = encrypted ? 16 : 8;
        var check = RandomNumberGenerator.GetBytes(4);
        var section = new Writer();
        section.Raw(check);
        section.Raw(check);
        section.Raw(privatePart);
        section.String(comment);
        for (byte pad = 1; section.Length % blockSize != 0; pad++) section.Raw([pad]);
        var plain = section.ToArray();
        CryptographicOperations.ZeroMemory(privatePart);

        var file = new Writer();
        file.Raw(Encoding.ASCII.GetBytes("openssh-key-v1\0"));
        if (encrypted)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            var derived = BcryptPbkdf.Derive(Encoding.UTF8.GetBytes(passphrase!), salt, 48, KdfRounds);
            var cipher = AesCtr(derived.AsSpan(0, 32), derived.AsSpan(32, 16), plain);
            CryptographicOperations.ZeroMemory(derived);
            file.String("aes256-ctr");
            file.String("bcrypt");
            file.Bytes(Blob(w => { w.Bytes(salt); w.UInt32(KdfRounds); }));
            file.UInt32(1);
            file.Bytes(publicBlob);
            file.Bytes(cipher);
        }
        else
        {
            file.String("none");
            file.String("none");
            file.Bytes([]);
            file.UInt32(1);
            file.Bytes(publicBlob);
            file.Bytes(plain);
        }

        CryptographicOperations.ZeroMemory(plain);
        var b64 = Convert.ToBase64String(file.ToArray());
        var pem = new StringBuilder("-----BEGIN OPENSSH PRIVATE KEY-----\n");
        for (var i = 0; i < b64.Length; i += 70) pem.Append(b64, i, Math.Min(70, b64.Length - i)).Append('\n');
        pem.Append("-----END OPENSSH PRIVATE KEY-----\n");

        var publicLine = $"{keyType} {Convert.ToBase64String(publicBlob)}" + (string.IsNullOrWhiteSpace(comment) ? "" : " " + comment.Trim());
        return new SshKeyPair(pem.ToString(), publicLine, Fingerprint(publicBlob));
    }

    /// <summary>"SHA256:…" like ssh-keygen -l.</summary>
    public static string Fingerprint(byte[] publicBlob) => "SHA256:" + Convert.ToBase64String(SHA256.HashData(publicBlob)).TrimEnd('=');

    /// <summary>Fingerprint of a public key line ("ssh-ed25519 AAAA… comment"); null when it is not one.</summary>
    public static string? FingerprintOfLine(string line)
    {
        var parts = line.Trim().Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;
        try
        {
            return Fingerprint(Convert.FromBase64String(parts[1]));
        }
        catch (FormatException)
        {
            return null;
        }
    }

    /// <summary>Public key line of an existing private key file (OpenSSH, PEM, PuTTY formats SSH.NET reads).</summary>
    public static string PublicKeyOf(string privateKeyPath, string? passphrase, string comment)
    {
        using var key = new PrivateKeyFile(privateKeyPath, passphrase);
        var host = key.HostKeyAlgorithms.First();
        var blob = host.Data;
        var type = Encoding.ASCII.GetString(new ReaderSpan(blob).String());
        return $"{type} {Convert.ToBase64String(blob)}" + (string.IsNullOrWhiteSpace(comment) ? "" : " " + comment.Trim());
    }

    /// <summary>
    /// Shell command that appends the key to ~/.ssh/authorized_keys once (like ssh-copy-id), creating the folder with
    /// safe permissions. The key is passed as a single-quoted literal.
    /// </summary>
    public static string AuthorizeCommand(string publicKeyLine)
    {
        var key = publicKeyLine.Trim().Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
        var quoted = "'" + key.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return "umask 077; mkdir -p ~/.ssh && touch ~/.ssh/authorized_keys && chmod 700 ~/.ssh && chmod 600 ~/.ssh/authorized_keys && " +
               $"(grep -qxF {quoted} ~/.ssh/authorized_keys || printf '%s\\n' {quoted} >> ~/.ssh/authorized_keys) && echo NEXODE-KEY-OK";
    }

    /// <summary>Connects with the given credentials and runs <see cref="AuthorizeCommand"/>; true when the key is installed.</summary>
    public static async Task<string?> DeployAsync(ConnectParams p, ISessionPrompts prompts, KnownHosts knownHosts, string publicKeyLine, CancellationToken ct)
    {
        var info = await SshConnections.CreateConnectionInfoAsync(p, prompts).ConfigureAwait(false);
        using var client = new SshClient(info);
        SshConnections.AttachHostKeyCheck(client, p, prompts, knownHosts);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        try
        {
            using var cmd = client.CreateCommand(AuthorizeCommand(publicKeyLine));
            cmd.CommandTimeout = TimeSpan.FromSeconds(20);
            var output = await Task.Run(cmd.Execute, ct).ConfigureAwait(false);
            return output.Contains("NEXODE-KEY-OK", StringComparison.Ordinal) ? null : (cmd.Error is { Length: > 0 } err ? err.Trim() : "authorized_keys could not be updated.");
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static byte[] AesCtr(ReadOnlySpan<byte> key, ReadOnlySpan<byte> iv, byte[] data)
    {
        using var aes = Aes.Create();
        aes.Key = key.ToArray();
        var counter = iv.ToArray();
        var output = new byte[data.Length];
        var block = new byte[16];
        for (var offset = 0; offset < data.Length; offset += 16)
        {
            aes.EncryptEcb(counter, block, PaddingMode.None);
            for (var i = 0; i < 16 && offset + i < data.Length; i++) output[offset + i] = (byte)(data[offset + i] ^ block[i]);
            for (var i = 15; i >= 0 && ++counter[i] == 0; i--)
            {
            }
        }

        return output;
    }

    private static byte[] Blob(Action<Writer> build)
    {
        var w = new Writer();
        build(w);
        return w.ToArray();
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();
        public long Length => _ms.Length;
        public void Raw(ReadOnlySpan<byte> b) => _ms.Write(b);

        public void UInt32(uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            _ms.Write(b);
        }

        public void Bytes(ReadOnlySpan<byte> b)
        {
            UInt32((uint)b.Length);
            _ms.Write(b);
        }

        public void String(string s) => Bytes(Encoding.UTF8.GetBytes(s));

        /// <summary>SSH mpint from an unsigned big-endian magnitude.</summary>
        public void MpInt(byte[] magnitude)
        {
            var v = new BigInteger(magnitude, isUnsigned: true, isBigEndian: true);
            Bytes(v.IsZero ? [] : v.ToByteArray(isUnsigned: false, isBigEndian: true));
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    private ref struct ReaderSpan(ReadOnlySpan<byte> data)
    {
        private readonly ReadOnlySpan<byte> _data = data;
        private int _offset;

        public byte[] String()
        {
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(_data[_offset..]);
            var v = _data.Slice(_offset + 4, length).ToArray();
            _offset += 4 + length;
            return v;
        }
    }
}

/// <summary>bcrypt_pbkdf as used by OpenSSH for passphrase-protected keys (OpenBSD bcrypt_pbkdf.c).</summary>
public static class BcryptPbkdf
{
    public static byte[] Derive(byte[] password, byte[] salt, int keyLength, int rounds)
    {
        if (rounds < 1 || keyLength is < 1 or > 1024) throw new ArgumentOutOfRangeException(nameof(rounds));
        var key = new byte[keyLength];
        var stride = (keyLength + 31) / 32;
        var amount = (keyLength + stride - 1) / stride;
        var sha2pass = SHA512.HashData(password);
        var remaining = keyLength;
        var countSalt = new byte[salt.Length + 4];
        salt.CopyTo(countSalt, 0);
        for (var count = 1; remaining > 0; count++)
        {
            BinaryPrimitives.WriteUInt32BigEndian(countSalt.AsSpan(salt.Length), (uint)count);
            var sha2salt = SHA512.HashData(countSalt);
            var tmp = Hash(sha2pass, sha2salt);
            var output = (byte[])tmp.Clone();
            for (var i = 1; i < rounds; i++)
            {
                sha2salt = SHA512.HashData(tmp);
                tmp = Hash(sha2pass, sha2salt);
                for (var j = 0; j < output.Length; j++) output[j] ^= tmp[j];
            }

            amount = Math.Min(amount, remaining);
            int n;
            for (n = 0; n < amount; n++)
            {
                var dest = n * stride + (count - 1);
                if (dest >= keyLength) break;
                key[dest] = output[n];
            }

            remaining -= n;
        }

        return key;
    }

    private static readonly byte[] Magic = Encoding.ASCII.GetBytes("OxychromaticBlowfishSwatDynamite");

    /// <summary>bcrypt_hash: 64 rounds of the expensive Blowfish key schedule, then 64× encrypt the magic text.</summary>
    internal static byte[] Hash(byte[] sha2pass, byte[] sha2salt)
    {
        var bf = new Blowfish();
        bf.ExpandState(sha2salt, sha2pass);
        for (var i = 0; i < 64; i++)
        {
            bf.Expand0State(sha2salt);
            bf.Expand0State(sha2pass);
        }

        var data = new uint[8];
        var j = 0;
        for (var i = 0; i < 8; i++) data[i] = Blowfish.StreamToWord(Magic, ref j);
        for (var i = 0; i < 64; i++)
        {
            for (var b = 0; b < 8; b += 2) bf.Encipher(ref data[b], ref data[b + 1]);
        }

        var result = new byte[32];
        for (var i = 0; i < 8; i++) BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(i * 4), data[i]);
        return result;
    }

    /// <summary>Blowfish with the bcrypt ("eksblowfish") key schedule.</summary>
    internal sealed class Blowfish
    {
        private readonly uint[] _p = new uint[18];
        private readonly uint[] _s = new uint[1024];

        public Blowfish()
        {
            var pi = PiWords.Value;
            Array.Copy(pi, 0, _p, 0, 18);
            Array.Copy(pi, 18, _s, 0, 1024);
        }

        public static uint StreamToWord(byte[] data, ref int current)
        {
            uint word = 0;
            for (var i = 0; i < 4; i++)
            {
                if (current >= data.Length) current = 0;
                word = word << 8 | data[current++];
            }

            return word;
        }

        private uint F(uint x) => ((_s[x >> 24] + _s[256 + (x >> 16 & 0xFF)]) ^ _s[512 + (x >> 8 & 0xFF)]) + _s[768 + (x & 0xFF)];

        public void Encipher(ref uint xl, ref uint xr)
        {
            var l = xl ^ _p[0];
            var r = xr;
            for (var i = 1; i <= 16; i += 2)
            {
                r ^= F(l) ^ _p[i];
                l ^= F(r) ^ _p[i + 1];
            }

            xl = r ^ _p[17];
            xr = l;
        }

        public void ExpandState(byte[] data, byte[] key)
        {
            var j = 0;
            for (var i = 0; i < 18; i++) _p[i] ^= StreamToWord(key, ref j);
            j = 0;
            uint l = 0, r = 0;
            for (var i = 0; i < 18; i += 2)
            {
                l ^= StreamToWord(data, ref j);
                r ^= StreamToWord(data, ref j);
                Encipher(ref l, ref r);
                _p[i] = l;
                _p[i + 1] = r;
            }

            for (var i = 0; i < 1024; i += 2)
            {
                l ^= StreamToWord(data, ref j);
                r ^= StreamToWord(data, ref j);
                Encipher(ref l, ref r);
                _s[i] = l;
                _s[i + 1] = r;
            }
        }

        public void Expand0State(byte[] key)
        {
            var j = 0;
            for (var i = 0; i < 18; i++) _p[i] ^= StreamToWord(key, ref j);
            uint l = 0, r = 0;
            for (var i = 0; i < 18; i += 2)
            {
                Encipher(ref l, ref r);
                _p[i] = l;
                _p[i + 1] = r;
            }

            for (var i = 0; i < 1024; i += 2)
            {
                Encipher(ref l, ref r);
                _s[i] = l;
                _s[i + 1] = r;
            }
        }

        /// <summary>Initial P-array and S-boxes: the hexadecimal digits of pi.</summary>
        private static readonly Lazy<uint[]> PiWords = new(() =>
        {
            var bytes = Convert.FromBase64String(PiBase64);
            var words = new uint[bytes.Length / 4];
            for (var i = 0; i < words.Length; i++) words[i] = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(i * 4));
            return words;
        });

        private const string PiBase64 =
        "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" +
        "plzeoD8JJS3CCOaft05hMs534ltXj9/jOsNy5g==";
    }
}
