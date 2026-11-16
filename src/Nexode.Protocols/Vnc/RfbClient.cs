using System.Buffers.Binary;
using System.IO.Compression;
using System.Net.Security;
using System.Net.Sockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Security.Authentication;
using Nexode.Core.Model;

namespace Nexode.Protocols.Vnc;

/// <summary>Decodes a JPEG (Tight encoding) to BGRA pixels; supplied by the UI layer (SkiaSharp). Null = not decodable.</summary>
public delegate uint[]? JpegDecoder(byte[] jpeg, int width, int height);

/// <summary>
/// VNC client (RFB 3.3 / 3.7 / 3.8, RFC 6143). Security: None, VNC authentication, VeNCrypt with an X.509 certificate
/// (X509None / X509Vnc / X509Plain – TigerVNC, LibVNCServer, QEMU; the certificate is checked by the system or pinned
/// trust-on-first-use) and Apple Remote Desktop (Diffie-Hellman + AES, user name and password – macOS screen sharing).
/// Encodings: Tight (zlib, palette, gradient, JPEG), ZRLE, Hextile, CopyRect, Raw, DesktopSize. The framebuffer is
/// 32-bit BGRA (opaque).
/// </summary>
public sealed class RfbClient(ConnectParams p, ISessionPrompts? prompts = null, KnownHosts? knownHosts = null, JpegDecoder? jpeg = null) : IDisposable
{
    private const int EncRaw = 0, EncCopyRect = 1, EncHextile = 5, EncTight = 7, EncZrle = 16, EncDesktopSize = -223, EncLastRect = -224;
    private const int SecNone = 1, SecVnc = 2, SecVeNCrypt = 19, SecArd = 30;
    private const uint VcPlain = 256, VcTlsNone = 257, VcTlsVnc = 258, VcTlsPlain = 259, VcX509None = 260, VcX509Vnc = 261, VcX509Plain = 262;

    private TcpClient? _tcp;
    private Stream? _stream;
    private BufferedStream? _in;
    private readonly Lock _sendGate = new();
    private CancellationTokenSource? _cts;
    private ZrleInflater? _zrle;
    private TightDecoder? _tight;
    private int _closed;

    public int Width { get; private set; }
    public int Height { get; private set; }
    public string DesktopName { get; private set; } = "";
    public bool ViewOnly { get; set; }

    /// <summary>Security that was used ("VNC", "VeNCrypt X509Vnc", "Apple Remote Desktop", "None").</summary>
    public string SecurityName { get; private set; } = "";

    /// <summary>True when the RFB stream runs inside TLS.</summary>
    public bool IsEncrypted { get; private set; }

    /// <summary>BGRA pixels, row-major, <see cref="Width"/> × <see cref="Height"/>. Lock <see cref="FrameLock"/> while reading.</summary>
    public uint[] Frame { get; private set; } = [];
    public Lock FrameLock { get; } = new();

    public event Action? FrameUpdated;
    public event Action? Resized;
    public event Action<string>? ClipboardReceived;
    public event Action? Bell;
    public event Action<string?>? Closed;

    /// <summary>"vnc.security": auto (default), vnc, vencrypt, ard, none.</summary>
    private string SecurityChoice => p.Setting(SettingKeys.VncSecurity) ?? "auto";

    /// <summary>"vnc.quality": lan (ZRLE, lossless), balanced (Tight + JPEG, default), low (Tight, strong JPEG compression).</summary>
    private string Quality => p.Setting(SettingKeys.VncQuality) ?? "balanced";

    // ------------------------------------------------------------------ connect

    public async Task ConnectAsync(CancellationToken ct)
    {
        try
        {
            await ConnectOnceAsync(avoidVeNCrypt: false, ct).ConfigureAwait(false);
        }
        catch (VeNCryptUnusableException ex) when (ex.VncAuthOffered && SecurityChoice == "auto")
        {
            // The server offers VeNCrypt only with anonymous TLS (not available in .NET) but also plain VNC
            // authentication (TigerVNC's default): connect again with VNC authentication.
            CloseTransport();
            await ConnectOnceAsync(avoidVeNCrypt: true, ct).ConfigureAwait(false);
        }
    }

    private async Task ConnectOnceAsync(bool avoidVeNCrypt, CancellationToken ct)
    {
        _tcp = await NetErrors.ConnectAsync(p.Host, p.Port, p.Timeout, ct).ConfigureAwait(false);
        _tcp.ReceiveBufferSize = 1 << 20;
        _tcp.NoDelay = true;
        _stream = _tcp.GetStream();
        _in = new BufferedStream(_stream, 1 << 16);

        // Version
        var version = Encoding.ASCII.GetString(await ReadAsync(12, ct).ConfigureAwait(false));
        if (!version.StartsWith("RFB ", StringComparison.Ordinal)) throw new IOException("Not a VNC server.");
        var minor = int.TryParse(version.AsSpan(8, 3), out var m) ? m : 3;
        var major = int.TryParse(version.AsSpan(4, 3), out var mj) ? mj : 3;
        if (major > 3) minor = 8;
        minor = minor >= 8 ? 8 : minor >= 7 ? 7 : 3;
        await WriteAsync(Encoding.ASCII.GetBytes($"RFB 003.00{minor}\n"), ct).ConfigureAwait(false);

        // Security
        int security;
        if (minor == 3)
        {
            security = (int)await ReadU32Async(ct).ConfigureAwait(false);
            if (security == 0) throw new IOException(await ReadReasonAsync(ct).ConfigureAwait(false));
        }
        else
        {
            var count = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
            if (count == 0) throw new IOException(await ReadReasonAsync(ct).ConfigureAwait(false));
            var types = await ReadAsync(count, ct).ConfigureAwait(false);
            security = ChooseSecurity(types, avoidVeNCrypt);
            if (security == 0)
            {
                throw new NotSupportedException("The server offers no security type Nexode supports (" + string.Join(", ", types.Select(SecurityTypeName)) +
                                                "). Supported: VNC password, VeNCrypt with an X.509 certificate, Apple Remote Desktop, none.");
            }

            await WriteAsync([(byte)security], ct).ConfigureAwait(false);
            if (security == SecVeNCrypt)
            {
                await VeNCryptAsync(types.Contains((byte)SecVnc), ct).ConfigureAwait(false);
            }
        }

        switch (security)
        {
            case SecVnc:
                SecurityName = "VNC";
                await VncAuthAsync(ct).ConfigureAwait(false);
                break;
            case SecArd:
                SecurityName = "Apple Remote Desktop";
                await ArdAuthAsync(ct).ConfigureAwait(false);
                break;
            case SecNone:
                SecurityName = "None";
                break;
        }

        if (security != SecNone || minor == 8)
        {
            var result = await ReadU32Async(ct).ConfigureAwait(false);
            if (result != 0)
            {
                var reason = minor == 8 ? await ReadReasonAsync(ct).ConfigureAwait(false) : "";
                throw new UnauthorizedAccessException("VNC authentication failed" + (reason.Length > 0 ? ": " + reason : "."));
            }
        }

        // ClientInit (shared) / ServerInit
        await WriteAsync([1], ct).ConfigureAwait(false);
        var init = await ReadAsync(24, ct).ConfigureAwait(false);
        Width = BinaryPrimitives.ReadUInt16BigEndian(init);
        Height = BinaryPrimitives.ReadUInt16BigEndian(init.AsSpan(2));
        var nameLength = (int)BinaryPrimitives.ReadUInt32BigEndian(init.AsSpan(20));
        DesktopName = Encoding.UTF8.GetString(await ReadAsync(Math.Min(nameLength, 4096), ct).ConfigureAwait(false));
        if (nameLength > 4096) await ReadAsync(nameLength - 4096, ct).ConfigureAwait(false);
        lock (FrameLock) Frame = new uint[Width * Height];

        // 32 bpp, little endian, true color: R<<16 | G<<8 | B -> BGRA in memory.
        var pf = new byte[20];
        pf[0] = 0; // SetPixelFormat
        pf[4] = 32;
        pf[5] = 24;
        pf[6] = 0; // little endian
        pf[7] = 1;
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(8), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(10), 255);
        BinaryPrimitives.WriteUInt16BigEndian(pf.AsSpan(12), 255);
        pf[14] = 16;
        pf[15] = 8;
        pf[16] = 0;
        await WriteAsync(pf, ct).ConfigureAwait(false);

        var encodings = Encodings(Quality, jpeg is not null);
        var enc = new byte[4 + encodings.Count * 4];
        enc[0] = 2;
        BinaryPrimitives.WriteUInt16BigEndian(enc.AsSpan(2), (ushort)encodings.Count);
        for (var i = 0; i < encodings.Count; i++) BinaryPrimitives.WriteInt32BigEndian(enc.AsSpan(4 + i * 4), encodings[i]);
        await WriteAsync(enc, ct).ConfigureAwait(false);

        _cts = new CancellationTokenSource();
        RequestUpdate(false);
        _ = Task.Run(() => ReceiveLoop(_cts.Token));
    }

    /// <summary>Encodings in order of preference for a quality setting (pseudo-encodings for JPEG quality and compression).</summary>
    public static IReadOnlyList<int> Encodings(string quality, bool canJpeg)
    {
        var list = new List<int>();
        switch (quality)
        {
            case "lan":
                // Lossless, cheap on the CPU: best on a fast network.
                list.AddRange([EncZrle, EncTight, EncHextile, EncCopyRect, EncRaw]);
                list.Add(-256 + 1); // compression level 1
                break;
            case "low":
                list.AddRange([EncTight, EncZrle, EncHextile, EncCopyRect, EncRaw]);
                list.Add(-256 + 9);
                if (canJpeg) list.Add(-32 + 3); // JPEG quality level 3
                break;
            default:
                list.AddRange([EncTight, EncZrle, EncHextile, EncCopyRect, EncRaw]);
                list.Add(-256 + 6);
                if (canJpeg) list.Add(-32 + 7);
                break;
        }

        list.AddRange([EncDesktopSize, EncLastRect]);
        return list;
    }

    private int ChooseSecurity(byte[] types, bool avoidVeNCrypt)
    {
        bool Has(int t) => types.Contains((byte)t);
        var hasPassword = !string.IsNullOrEmpty(p.Password);
        switch (SecurityChoice)
        {
            case "vnc": return Has(SecVnc) ? SecVnc : 0;
            case "vencrypt": return Has(SecVeNCrypt) ? SecVeNCrypt : 0;
            case "ard": return Has(SecArd) ? SecArd : 0;
            case "none": return Has(SecNone) ? SecNone : 0;
        }

        // Automatic: encrypted first, then a password, then nothing.
        if (Has(SecVeNCrypt) && !avoidVeNCrypt) return SecVeNCrypt;
        if (Has(SecArd) && hasPassword && !string.IsNullOrEmpty(p.Username)) return SecArd;
        if (Has(SecVnc) && hasPassword) return SecVnc;
        if (Has(SecNone)) return SecNone;
        if (Has(SecVnc)) return SecVnc;
        return Has(SecArd) ? SecArd : 0;
    }

    public static string SecurityTypeName(byte t) => t switch
    {
        1 => "None",
        2 => "VNC",
        5 => "RA2",
        6 => "RA2ne",
        13 => "RA2_256",
        16 => "Tight",
        18 => "TLS",
        19 => "VeNCrypt",
        30 => "Apple Remote Desktop",
        _ => t.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    private async Task VncAuthAsync(CancellationToken ct)
    {
        var challenge = await ReadAsync(16, ct).ConfigureAwait(false);
        await WriteAsync(VncAuthResponse(challenge, p.Password ?? ""), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// VeNCrypt: version 0.2, then a sub-type. Only certificate-based TLS (X509*) is possible – .NET has no anonymous
    /// TLS. The certificate is accepted when the system trusts it, otherwise after the user confirmed its fingerprint.
    /// </summary>
    private async Task VeNCryptAsync(bool vncAuthOffered, CancellationToken ct)
    {
        var v = await ReadAsync(2, ct).ConfigureAwait(false);
        if (v[0] != 0 || v[1] < 2) throw new NotSupportedException($"VeNCrypt {v[0]}.{v[1]} is not supported.");
        await WriteAsync([0, 2], ct).ConfigureAwait(false);
        if ((await ReadAsync(1, ct).ConfigureAwait(false))[0] != 0) throw new IOException("The server rejected VeNCrypt 0.2.");
        var n = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
        var raw = await ReadAsync(n * 4, ct).ConfigureAwait(false);
        var subtypes = Enumerable.Range(0, n).Select(i => BinaryPrimitives.ReadUInt32BigEndian(raw.AsSpan(i * 4))).ToList();
        var hasPassword = !string.IsNullOrEmpty(p.Password);
        uint chosen = 0;
        foreach (var candidate in hasPassword && !string.IsNullOrEmpty(p.Username)
                     ? new[] { VcX509Plain, VcX509Vnc, VcX509None }
                     : hasPassword ? new[] { VcX509Vnc, VcX509Plain, VcX509None } : new[] { VcX509None, VcX509Vnc, VcX509Plain })
        {
            if (subtypes.Contains(candidate))
            {
                chosen = candidate;
                break;
            }
        }

        if (chosen == 0)
        {
            var names = string.Join(", ", subtypes.Select(VeNCryptName));
            throw new VeNCryptUnusableException(vncAuthOffered,
                $"The server offers VeNCrypt only as {names}. Nexode needs a certificate (X509None, X509Vnc or X509Plain); anonymous TLS is not supported. " +
                "Configure a certificate on the server (TigerVNC: -X509Cert/-X509Key) or allow VNC authentication.");
        }

        var choice = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(choice, chosen);
        await WriteAsync(choice, ct).ConfigureAwait(false);
        if ((await ReadAsync(1, ct).ConfigureAwait(false))[0] != 1) throw new IOException("The server rejected the VeNCrypt sub-type.");

        // TLS over the TCP stream; everything after the handshake goes through it.
        var ssl = new SslStream(_tcp!.GetStream(), false, (_, cert, _, errors) =>
        {
            if (errors == SslPolicyErrors.None) return true;
            if (cert is null || knownHosts is null || prompts is null) return false;
            var fingerprint = "SHA256:" + Convert.ToHexString(SHA256.HashData(cert.GetRawCertData()));
            using var x = new X509Certificate2(cert);
            return knownHosts.VerifyAsync("tls", p.Host, p.Port, x.Subject, fingerprint, prompts).GetAwaiter().GetResult();
        });
        await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions { TargetHost = p.Host }, ct).ConfigureAwait(false);
        _stream = ssl;
        _in = new BufferedStream(ssl, 1 << 16);
        IsEncrypted = true;
        SecurityName = "VeNCrypt " + VeNCryptName(chosen);

        if (chosen == VcX509Vnc)
        {
            await VncAuthAsync(ct).ConfigureAwait(false);
        }
        else if (chosen == VcX509Plain)
        {
            var user = Encoding.UTF8.GetBytes(p.Username ?? "");
            var pass = Encoding.UTF8.GetBytes(p.Password ?? "");
            var msg = new byte[8 + user.Length + pass.Length];
            BinaryPrimitives.WriteUInt32BigEndian(msg, (uint)user.Length);
            BinaryPrimitives.WriteUInt32BigEndian(msg.AsSpan(4), (uint)pass.Length);
            user.CopyTo(msg, 8);
            pass.CopyTo(msg, 8 + user.Length);
            await WriteAsync(msg, ct).ConfigureAwait(false);
            CryptographicOperations.ZeroMemory(msg);
            CryptographicOperations.ZeroMemory(pass);
        }
    }

    public static string VeNCryptName(uint t) => t switch
    {
        VcPlain => "Plain",
        VcTlsNone => "TLSNone",
        VcTlsVnc => "TLSVnc",
        VcTlsPlain => "TLSPlain",
        VcX509None => "X509None",
        VcX509Vnc => "X509Vnc",
        VcX509Plain => "X509Plain",
        _ => t.ToString(System.Globalization.CultureInfo.InvariantCulture)
    };

    /// <summary>
    /// Apple Remote Desktop (type 30): Diffie-Hellman with the server's generator and prime, MD5 of the shared secret
    /// as AES-128 key, user name and password (64 bytes each, random padding) encrypted with AES-ECB.
    /// </summary>
    private async Task ArdAuthAsync(CancellationToken ct)
    {
        var head = await ReadAsync(4, ct).ConfigureAwait(false);
        var generator = BinaryPrimitives.ReadUInt16BigEndian(head);
        var keyLength = BinaryPrimitives.ReadUInt16BigEndian(head.AsSpan(2));
        if (keyLength is < 16 or > 1024) throw new IOException("Invalid Apple Remote Desktop key length.");
        var prime = await ReadAsync(keyLength, ct).ConfigureAwait(false);
        var serverPublic = await ReadAsync(keyLength, ct).ConfigureAwait(false);
        var (response, clientPublic) = ArdResponse(generator, prime, serverPublic, p.Username ?? "", p.Password ?? "");
        await WriteAsync([.. response, .. clientPublic], ct).ConfigureAwait(false);
    }

    /// <summary>ARD response (encrypted credentials, client public key); exposed for tests.</summary>
    public static (byte[] Credentials, byte[] ClientPublic) ArdResponse(int generator, byte[] prime, byte[] serverPublic, string user, string password, byte[]? privateKey = null)
    {
        var keyLength = prime.Length;
        var mod = new BigInteger(prime, isUnsigned: true, isBigEndian: true);
        var priv = new BigInteger(privateKey ?? RandomNumberGenerator.GetBytes(keyLength), isUnsigned: true, isBigEndian: true) % mod;
        var pub = BigInteger.ModPow(generator, priv, mod);
        var shared = BigInteger.ModPow(new BigInteger(serverPublic, isUnsigned: true, isBigEndian: true), priv, mod);
        var key = MD5.HashData(Pad(shared.ToByteArray(isUnsigned: true, isBigEndian: true), keyLength));

        var creds = RandomNumberGenerator.GetBytes(128);
        var u = Encoding.UTF8.GetBytes(user);
        var pw = Encoding.UTF8.GetBytes(password);
        if (u.Length > 63 || pw.Length > 63) throw new ArgumentException("User name and password may have at most 63 bytes for Apple Remote Desktop.");
        u.CopyTo(creds, 0);
        creds[u.Length] = 0;
        pw.CopyTo(creds, 64);
        creds[64 + pw.Length] = 0;
        using var aes = Aes.Create();
        aes.Key = key;
        var encrypted = aes.EncryptEcb(creds, PaddingMode.None);
        CryptographicOperations.ZeroMemory(creds);
        return (encrypted, Pad(pub.ToByteArray(isUnsigned: true, isBigEndian: true), keyLength));

        static byte[] Pad(byte[] v, int length)
        {
            if (v.Length == length) return v;
            var r = new byte[length];
            v.AsSpan(Math.Max(0, v.Length - length)).CopyTo(r.AsSpan(Math.Max(0, length - v.Length)));
            return r;
        }
    }

    /// <summary>DES with the bit-reversed password as key (classic VNC authentication).</summary>
    public static byte[] VncAuthResponse(byte[] challenge, string password)
    {
        var key = new byte[8];
        var pw = Encoding.Latin1.GetBytes(password);
        for (var i = 0; i < 8 && i < pw.Length; i++)
        {
            var b = pw[i];
            byte r = 0;
            for (var bit = 0; bit < 8; bit++) r |= (byte)(((b >> bit) & 1) << (7 - bit));
            key[i] = r;
        }

        using var des = DES.Create();
        des.Key = key;
        return des.EncryptEcb(challenge, PaddingMode.None);
    }

    private sealed class VeNCryptUnusableException(bool vncAuthOffered, string message) : NotSupportedException(message)
    {
        public bool VncAuthOffered { get; } = vncAuthOffered;
    }

    // ------------------------------------------------------------------ receive

    private async Task ReceiveLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var type = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
                switch (type)
                {
                    case 0:
                        await FramebufferUpdateAsync(ct).ConfigureAwait(false);
                        FrameUpdated?.Invoke();
                        RequestUpdate(true);
                        break;
                    case 1:
                    {
                        // Color map: not used in true-color mode, skip.
                        var h = await ReadAsync(5, ct).ConfigureAwait(false);
                        await ReadAsync(BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(3)) * 6, ct).ConfigureAwait(false);
                        break;
                    }
                    case 2:
                        Bell?.Invoke();
                        break;
                    case 3:
                    {
                        var h = await ReadAsync(7, ct).ConfigureAwait(false);
                        var len = (int)BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(3));
                        var text = await ReadAsync(Math.Min(len, 1 << 20), ct).ConfigureAwait(false);
                        if (len > 1 << 20) await ReadAsync(len - (1 << 20), ct).ConfigureAwait(false);
                        ClipboardReceived?.Invoke(Encoding.Latin1.GetString(text));
                        break;
                    }
                    default:
                        throw new IOException($"Unsupported server message {type}.");
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidDataException or AuthenticationException)
        {
            OnClosed(ct.IsCancellationRequested ? null : NetErrors.Describe(ex));
        }
    }

    private async Task FramebufferUpdateAsync(CancellationToken ct)
    {
        var h = await ReadAsync(3, ct).ConfigureAwait(false);
        var count = BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(1));
        for (var r = 0; r < count; r++)
        {
            var rh = await ReadAsync(12, ct).ConfigureAwait(false);
            int x = BinaryPrimitives.ReadUInt16BigEndian(rh), y = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(2));
            int w = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(4)), hh = BinaryPrimitives.ReadUInt16BigEndian(rh.AsSpan(6));
            var encoding = BinaryPrimitives.ReadInt32BigEndian(rh.AsSpan(8));
            switch (encoding)
            {
                case EncRaw:
                {
                    var data = await ReadAsync(w * hh * 4, ct).ConfigureAwait(false);
                    lock (FrameLock) Blit(data, x, y, w, hh);
                    break;
                }
                case EncCopyRect:
                {
                    var src = await ReadAsync(4, ct).ConfigureAwait(false);
                    lock (FrameLock) CopyRect(BinaryPrimitives.ReadUInt16BigEndian(src), BinaryPrimitives.ReadUInt16BigEndian(src.AsSpan(2)), x, y, w, hh);
                    break;
                }
                case EncHextile:
                    await HextileAsync(x, y, w, hh, ct).ConfigureAwait(false);
                    break;
                case EncTight:
                    _tight ??= new TightDecoder(jpeg);
                    await _tight.DecodeAsync(this, x, y, w, hh, ct).ConfigureAwait(false);
                    break;
                case EncZrle:
                {
                    var len = (int)await ReadU32Async(ct).ConfigureAwait(false);
                    var data = await ReadAsync(len, ct).ConfigureAwait(false);
                    _zrle ??= new ZrleInflater();
                    lock (FrameLock) _zrle.Decode(data, this, x, y, w, hh);
                    break;
                }
                case EncDesktopSize:
                    Width = w;
                    Height = hh;
                    lock (FrameLock) Frame = new uint[Width * Height];
                    Resized?.Invoke();
                    break;
                case EncLastRect:
                    return;
                default:
                    throw new IOException($"Unsupported encoding {encoding}.");
            }
        }
    }

    internal void Blit(byte[] bgra, int x, int y, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            var dy = y + row;
            if (dy >= Height) break;
            var src = bgra.AsSpan(row * w * 4, w * 4);
            for (var col = 0; col < w && x + col < Width; col++)
            {
                Frame[dy * Width + x + col] = 0xFF000000u | BinaryPrimitives.ReadUInt32LittleEndian(src[(col * 4)..]);
            }
        }
    }

    /// <summary>Opaque BGRA pixels (already decoded) into the frame.</summary>
    internal void BlitPixels(uint[] pixels, int x, int y, int w, int h)
    {
        for (var row = 0; row < h && y + row < Height; row++)
        {
            var n = Math.Max(0, Math.Min(w, Width - x));
            pixels.AsSpan(row * w, n).CopyTo(Frame.AsSpan((y + row) * Width + x, n));
        }
    }

    internal void Fill(int x, int y, int w, int h, uint color)
    {
        color |= 0xFF000000u;
        for (var row = y; row < y + h && row < Height; row++)
        {
            Frame.AsSpan(row * Width + x, Math.Max(0, Math.Min(w, Width - x))).Fill(color);
        }
    }

    internal void SetPixel(int x, int y, uint color)
    {
        if (x < Width && y < Height) Frame[y * Width + x] = color | 0xFF000000u;
    }

    private void CopyRect(int sx, int sy, int dx, int dy, int w, int h)
    {
        var tmp = new uint[w * h];
        for (var row = 0; row < h; row++) Frame.AsSpan((sy + row) * Width + sx, w).CopyTo(tmp.AsSpan(row * w, w));
        for (var row = 0; row < h; row++) tmp.AsSpan(row * w, w).CopyTo(Frame.AsSpan((dy + row) * Width + dx, w));
    }

    private async Task HextileAsync(int rx, int ry, int rw, int rh, CancellationToken ct)
    {
        uint bg = 0, fg = 0;
        for (var ty = ry; ty < ry + rh; ty += 16)
        {
            for (var tx = rx; tx < rx + rw; tx += 16)
            {
                var tw = Math.Min(16, rx + rw - tx);
                var th = Math.Min(16, ry + rh - ty);
                var mask = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
                if ((mask & 1) != 0)
                {
                    var raw = await ReadAsync(tw * th * 4, ct).ConfigureAwait(false);
                    lock (FrameLock) Blit(raw, tx, ty, tw, th);
                    continue;
                }

                if ((mask & 2) != 0) bg = await ReadU32LeAsync(ct).ConfigureAwait(false);
                lock (FrameLock) Fill(tx, ty, tw, th, bg);
                if ((mask & 4) != 0) fg = await ReadU32LeAsync(ct).ConfigureAwait(false);
                if ((mask & 8) == 0) continue;
                var n = (await ReadAsync(1, ct).ConfigureAwait(false))[0];
                var colored = (mask & 16) != 0;
                var sub = await ReadAsync(n * (colored ? 6 : 2), ct).ConfigureAwait(false);
                lock (FrameLock)
                {
                    for (int i = 0, o = 0; i < n; i++)
                    {
                        var color = fg;
                        if (colored)
                        {
                            color = BinaryPrimitives.ReadUInt32LittleEndian(sub.AsSpan(o));
                            o += 4;
                        }

                        var xy = sub[o];
                        var wh = sub[o + 1];
                        o += 2;
                        Fill(tx + (xy >> 4), ty + (xy & 15), (wh >> 4) + 1, (wh & 15) + 1, color);
                    }
                }
            }
        }
    }

    // ------------------------------------------------------------------ send

    public void RequestUpdate(bool incremental)
    {
        var b = new byte[10];
        b[0] = 3;
        b[1] = (byte)(incremental ? 1 : 0);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(6), (ushort)Width);
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(8), (ushort)Height);
        Send(b);
    }

    public void SendKey(uint keysym, bool down)
    {
        if (ViewOnly) return;
        var b = new byte[8];
        b[0] = 4;
        b[1] = (byte)(down ? 1 : 0);
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), keysym);
        Send(b);
    }

    /// <summary>Button mask: 1 left, 2 middle, 4 right, 8/16 wheel up/down.</summary>
    public void SendPointer(int x, int y, int buttons)
    {
        if (ViewOnly) return;
        var b = new byte[6];
        b[0] = 5;
        b[1] = (byte)buttons;
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(2), (ushort)Math.Clamp(x, 0, Math.Max(0, Width - 1)));
        BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(4), (ushort)Math.Clamp(y, 0, Math.Max(0, Height - 1)));
        Send(b);
    }

    public void SendClipboard(string text)
    {
        if (ViewOnly) return;
        var data = Encoding.Latin1.GetBytes(text.Replace("\r\n", "\n"));
        var b = new byte[8 + data.Length];
        b[0] = 6;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(4), (uint)data.Length);
        data.CopyTo(b, 8);
        Send(b);
    }

    private void Send(byte[] data)
    {
        try
        {
            lock (_sendGate)
            {
                _stream?.Write(data);
                _stream?.Flush();
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or SocketException)
        {
            OnClosed(NetErrors.Describe(ex));
        }
    }

    // ------------------------------------------------------------------ helpers

    internal async Task<byte[]> ReadAsync(int count, CancellationToken ct)
    {
        var buf = new byte[count];
        await _in!.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    private async Task<uint> ReadU32Async(CancellationToken ct) => BinaryPrimitives.ReadUInt32BigEndian(await ReadAsync(4, ct).ConfigureAwait(false));
    private async Task<uint> ReadU32LeAsync(CancellationToken ct) => BinaryPrimitives.ReadUInt32LittleEndian(await ReadAsync(4, ct).ConfigureAwait(false));

    private async Task<string> ReadReasonAsync(CancellationToken ct)
    {
        var len = (int)await ReadU32Async(ct).ConfigureAwait(false);
        return Encoding.UTF8.GetString(await ReadAsync(Math.Min(len, 4096), ct).ConfigureAwait(false));
    }

    private async Task WriteAsync(byte[] data, CancellationToken ct)
    {
        await _stream!.WriteAsync(data, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    private void CloseTransport()
    {
        _stream?.Dispose();
        _tcp?.Dispose();
        _stream = null;
        _in = null;
        _tcp = null;
        IsEncrypted = false;
    }

    private void OnClosed(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) == 0) Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        _closed = 1;
        _cts?.Cancel();
        _stream?.Dispose();
        _tcp?.Dispose();
        _zrle?.Dispose();
        _tight?.Dispose();
    }
}

/// <summary>ZRLE decoder with the persistent zlib stream the protocol requires (CPIXEL = 3 bytes for 32 bpp / depth 24).</summary>
internal sealed class ZrleInflater : IDisposable
{
    private readonly FeedStream _feed = new();
    private readonly ZLibStream _zlib;

    public ZrleInflater() => _zlib = new ZLibStream(_feed, CompressionMode.Decompress);

    public void Decode(byte[] compressed, RfbClient c, int rx, int ry, int rw, int rh)
    {
        _feed.Append(compressed);
        Span<uint> palette = stackalloc uint[128];
        for (var ty = ry; ty < ry + rh; ty += 64)
        {
            for (var tx = rx; tx < rx + rw; tx += 64)
            {
                var tw = Math.Min(64, rx + rw - tx);
                var th = Math.Min(64, ry + rh - ty);
                var mode = Byte();
                var rle = (mode & 128) != 0;
                var size = mode & 127;
                if (!rle && size == 0)
                {
                    for (var y = 0; y < th; y++)
                    for (var x = 0; x < tw; x++) c.SetPixel(tx + x, ty + y, CPixel());
                }
                else if (!rle && size == 1)
                {
                    c.Fill(tx, ty, tw, th, CPixel());
                }
                else if (!rle && size <= 16)
                {
                    for (var i = 0; i < size; i++) palette[i] = CPixel();
                    var bits = size == 2 ? 1 : size <= 4 ? 2 : 4;
                    for (var y = 0; y < th; y++)
                    {
                        int b = 0, left = 0;
                        for (var x = 0; x < tw; x++)
                        {
                            if (left == 0)
                            {
                                b = Byte();
                                left = 8;
                            }

                            left -= bits;
                            c.SetPixel(tx + x, ty + y, palette[(b >> left) & ((1 << bits) - 1)]);
                        }
                    }
                }
                else if (rle && size == 0)
                {
                    var i = 0;
                    var total = tw * th;
                    while (i < total)
                    {
                        var color = CPixel();
                        var run = RunLength();
                        for (var k = 0; k < run && i < total; k++, i++) c.SetPixel(tx + i % tw, ty + i / tw, color);
                    }
                }
                else if (rle && size >= 2)
                {
                    for (var i = 0; i < size; i++) palette[i] = CPixel();
                    var p = 0;
                    var total = tw * th;
                    while (p < total)
                    {
                        var index = Byte();
                        var run = 1;
                        if ((index & 128) != 0)
                        {
                            index &= 127;
                            run = RunLength();
                        }

                        for (var k = 0; k < run && p < total; k++, p++) c.SetPixel(tx + p % tw, ty + p / tw, palette[index]);
                    }
                }
                else
                {
                    throw new InvalidDataException("Invalid ZRLE subencoding.");
                }
            }
        }
    }

    private int RunLength()
    {
        var run = 1;
        int b;
        do
        {
            b = Byte();
            run += b;
        } while (b == 255);

        return run;
    }

    private int Byte()
    {
        var b = _zlib.ReadByte();
        if (b < 0) throw new InvalidDataException("ZRLE data ended early.");
        return b;
    }

    private readonly byte[] _px = new byte[3];

    private uint CPixel()
    {
        _zlib.ReadExactly(_px);
        return (uint)(_px[0] | _px[1] << 8 | _px[2] << 16);
    }

    public void Dispose() => _zlib.Dispose();

    /// <summary>Stream the inflater reads from; chunks are appended per rectangle.</summary>
    internal sealed class FeedStream : Stream
    {
        private readonly Queue<byte[]> _chunks = new();
        private byte[]? _current;
        private int _pos;

        public void Append(byte[] data)
        {
            if (data.Length > 0) _chunks.Enqueue(data);
        }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            while (_current is null || _pos >= _current.Length)
            {
                if (_chunks.Count == 0) return 0;
                _current = _chunks.Dequeue();
                _pos = 0;
            }

            var n = Math.Min(buffer.Length, _current.Length - _pos);
            _current.AsSpan(_pos, n).CopyTo(buffer);
            _pos += n;
            return n;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

/// <summary>X11 keysyms for keys without a printable character.</summary>
public static class KeySyms
{
    public const uint BackSpace = 0xFF08, Tab = 0xFF09, Return = 0xFF0D, Escape = 0xFF1B, Delete = 0xFFFF, Home = 0xFF50, Left = 0xFF51, Up = 0xFF52,
        Right = 0xFF53, Down = 0xFF54, PageUp = 0xFF55, PageDown = 0xFF56, End = 0xFF57, Insert = 0xFF63, F1 = 0xFFBE, ShiftL = 0xFFE1,
        ControlL = 0xFFE3, AltL = 0xFFE9, SuperL = 0xFFEB, CapsLock = 0xFFE5, Menu = 0xFF67, Print = 0xFF61, Pause = 0xFF13, ScrollLock = 0xFF14, NumLock = 0xFF7F;

    /// <summary>Keysym of a printable character (Latin-1 directly, other Unicode via 0x01000000 + code point).</summary>
    public static uint FromChar(int codePoint) => codePoint is >= 0x20 and <= 0xFF ? (uint)codePoint : 0x01000000u + (uint)codePoint;
}
