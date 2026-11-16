using System.Buffers.Binary;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Agreement.JPake;
using Org.BouncyCastle.Crypto.Digests;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.Security;

namespace Nexode.Protocols.LanSync;

/// <summary>Raised when the other side does not know the sync password (or is not Nexode).</summary>
public sealed class LanSyncAuthException(string message) : Exception(message);

/// <summary>
/// Encrypted, password-authenticated connection between two Nexode instances in the LAN.
/// <para>
/// Authentication: J-PAKE (RFC 8236, 3072-bit group, SHA-256) with the sync password. Neither the password nor anything
/// derived from it that allows an offline guess goes over the wire; an active attacker gets one guess per connection,
/// and the server limits failed attempts. Both sides confirm the key (round 3) before any data is sent.
/// </para>
/// <para>
/// Data: AES-256-GCM frames with one key per direction (HKDF-SHA256 from the J-PAKE keying material and both random
/// nonces), counter nonces, so frames cannot be replayed, reordered or reflected.
/// </para>
/// </summary>
public sealed class LanSyncChannel : IDisposable
{
    public const int DefaultPort = 47810;
    public const int DiscoveryPort = 47811;
    private const int MaxFrame = 64 * 1024 * 1024;
    private const int MaxHandshakeFrame = 64 * 1024;
    private static readonly byte[] Magic = "NXLS1\n"u8.ToArray();
    private const string ClientId = "nexode-lansync-client";
    private const string ServerId = "nexode-lansync-server";

    private readonly Stream _stream;
    private readonly TcpClient? _tcp;
    private readonly AesGcm _send;
    private readonly AesGcm _receive;
    private readonly uint _sendDir;
    private readonly uint _receiveDir;
    private ulong _sendCounter;
    private ulong _receiveCounter;

    private LanSyncChannel(Stream stream, TcpClient? tcp, byte[] sendKey, byte[] receiveKey, bool isClient, EndPoint? remote)
    {
        _stream = stream;
        _tcp = tcp;
        _send = new AesGcm(sendKey, 16);
        _receive = new AesGcm(receiveKey, 16);
        _sendDir = isClient ? 1u : 2u;
        _receiveDir = isClient ? 2u : 1u;
        Remote = remote;
        CryptographicOperations.ZeroMemory(sendKey);
        CryptographicOperations.ZeroMemory(receiveKey);
    }

    public EndPoint? Remote { get; }

    /// <summary>Connects to another Nexode and authenticates with the sync password.</summary>
    public static async Task<LanSyncChannel> ConnectAsync(string host, int port, string password, CancellationToken ct)
    {
        var tcp = new TcpClient { NoDelay = true };
        try
        {
            using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                timeout.CancelAfter(TimeSpan.FromSeconds(10));
                await tcp.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            }

            var stream = tcp.GetStream();
            return await HandshakeAsync(stream, tcp, password, true, ct).ConfigureAwait(false);
        }
        catch
        {
            tcp.Dispose();
            throw;
        }
    }

    /// <summary>Server side of <see cref="ConnectAsync"/> on an accepted connection.</summary>
    public static Task<LanSyncChannel> AcceptAsync(TcpClient tcp, string password, CancellationToken ct) =>
        HandshakeAsync(tcp.GetStream(), tcp, password, false, ct);

    /// <summary>Handshake on any stream (tests).</summary>
    public static Task<LanSyncChannel> HandshakeAsync(Stream stream, string password, bool isClient, CancellationToken ct) =>
        HandshakeAsync(stream, null, password, isClient, ct);

    private static async Task<LanSyncChannel> HandshakeAsync(Stream stream, TcpClient? tcp, string password, bool isClient, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var t = timeout.Token;
        var me = isClient ? ClientId : ServerId;
        var peer = isClient ? ServerId : ClientId;
        var pw = password.Normalize(NormalizationForm.FormC).ToCharArray();
        var jpake = new JPakeParticipant(me, pw, JPakePrimeOrderGroups.NIST_3072, new Sha256Digest(), new SecureRandom());
        Array.Clear(pw);
        var myNonce = RandomNumberGenerator.GetBytes(32);
        byte[] peerNonce;

        try
        {
            // Round 1 (+ magic and nonce).
            var r1 = jpake.CreateRound1PayloadToSend();
            await WriteRawAsync(stream, Concat(Magic, myNonce, Round1(r1)), t).ConfigureAwait(false);
            var peerR1 = await ReadRawAsync(stream, t).ConfigureAwait(false);
            if (peerR1.Length < Magic.Length + 32 || !peerR1.AsSpan(0, Magic.Length).SequenceEqual(Magic)) throw new LanSyncAuthException("The other side is not a Nexode LAN sync.");
            peerNonce = peerR1[Magic.Length..(Magic.Length + 32)];
            jpake.ValidateRound1PayloadReceived(ReadRound1(peerR1[(Magic.Length + 32)..], peer));

            // Round 2.
            await WriteRawAsync(stream, Round2(jpake.CreateRound2PayloadToSend()), t).ConfigureAwait(false);
            jpake.ValidateRound2PayloadReceived(ReadRound2(await ReadRawAsync(stream, t).ConfigureAwait(false), peer));
            var keying = jpake.CalculateKeyingMaterial();

            // Round 3: explicit key confirmation. The client proves first; the server answers only to a correct proof.
            if (isClient)
            {
                await WriteRawAsync(stream, Round3(jpake.CreateRound3PayloadToSend(keying)), t).ConfigureAwait(false);
                var answer = await ReadRawAsync(stream, t).ConfigureAwait(false);
                if (answer.Length == 1) throw new LanSyncAuthException("Wrong sync password.");
                jpake.ValidateRound3PayloadReceived(ReadRound3(answer, peer), keying);
            }
            else
            {
                // Made before checking (the J-PAKE state machine wants it so), sent only after the client's proof is valid.
                var mine = Round3(jpake.CreateRound3PayloadToSend(keying));
                try
                {
                    jpake.ValidateRound3PayloadReceived(ReadRound3(await ReadRawAsync(stream, t).ConfigureAwait(false), peer), keying);
                }
                catch (CryptoException)
                {
                    await WriteRawAsync(stream, [0], t).ConfigureAwait(false);
                    throw new LanSyncAuthException("Wrong sync password.");
                }

                await WriteRawAsync(stream, mine, t).ConfigureAwait(false);
            }

            var ikm = SHA256.HashData(keying.ToByteArrayUnsigned());
            var (cNonce, sNonce) = isClient ? (myNonce, peerNonce) : (peerNonce, myNonce);
            var salt = Concat(cNonce, sNonce);
            var c2s = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, "nexode lansync c2s"u8.ToArray());
            var s2c = HKDF.DeriveKey(HashAlgorithmName.SHA256, ikm, 32, salt, "nexode lansync s2c"u8.ToArray());
            CryptographicOperations.ZeroMemory(ikm);
            return isClient
                ? new LanSyncChannel(stream, tcp, c2s, s2c, true, tcp?.Client.RemoteEndPoint)
                : new LanSyncChannel(stream, tcp, s2c, c2s, false, tcp?.Client.RemoteEndPoint);
        }
        catch (CryptoException ex)
        {
            throw new LanSyncAuthException(ex.Message.Contains("MacTag", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("keying", StringComparison.OrdinalIgnoreCase)
                ? "Wrong sync password."
                : "Handshake failed: " + ex.Message);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            throw new LanSyncAuthException("Handshake failed: " + ex.Message);
        }
    }

    // ------------------------------------------------------------------ encrypted frames

    public async Task SendAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        if (data.Length > MaxFrame) throw new InvalidOperationException("Data too large for one frame.");
        var frame = new byte[4 + data.Length + 16];
        BinaryPrimitives.WriteInt32BigEndian(frame, data.Length + 16);
        _send.Encrypt(Nonce(_sendDir, _sendCounter++), data.Span, frame.AsSpan(4, data.Length), frame.AsSpan(4 + data.Length, 16));
        await _stream.WriteAsync(frame, ct).ConfigureAwait(false);
        await _stream.FlushAsync(ct).ConfigureAwait(false);
    }

    public async Task<byte[]> ReceiveAsync(CancellationToken ct)
    {
        var head = new byte[4];
        await _stream.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len is < 16 or > MaxFrame + 16) throw new InvalidDataException("Invalid frame length.");
        var buf = new byte[len];
        await _stream.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        var plain = new byte[len - 16];
        _receive.Decrypt(Nonce(_receiveDir, _receiveCounter++), buf.AsSpan(0, len - 16), buf.AsSpan(len - 16), plain);
        return plain;
    }

    public Task SendJsonAsync<T>(T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct) =>
        SendAsync(JsonSerializer.SerializeToUtf8Bytes(value, info), ct);

    public async Task<T> ReceiveJsonAsync<T>(System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> info, CancellationToken ct) =>
        JsonSerializer.Deserialize(await ReceiveAsync(ct).ConfigureAwait(false), info) ?? throw new InvalidDataException("Empty message.");

    private static byte[] Nonce(uint direction, ulong counter)
    {
        var n = new byte[12];
        BinaryPrimitives.WriteUInt32BigEndian(n, direction);
        BinaryPrimitives.WriteUInt64BigEndian(n.AsSpan(4), counter);
        return n;
    }

    public void Dispose()
    {
        _send.Dispose();
        _receive.Dispose();
        _stream.Dispose();
        _tcp?.Dispose();
    }

    // ------------------------------------------------------------------ handshake encoding

    private static async Task WriteRawAsync(Stream s, byte[] data, CancellationToken ct)
    {
        var head = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(head, data.Length);
        await s.WriteAsync(head, ct).ConfigureAwait(false);
        await s.WriteAsync(data, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadRawAsync(Stream s, CancellationToken ct)
    {
        var head = new byte[4];
        await s.ReadExactlyAsync(head, ct).ConfigureAwait(false);
        var len = BinaryPrimitives.ReadInt32BigEndian(head);
        if (len is < 1 or > MaxHandshakeFrame) throw new LanSyncAuthException("The other side is not a Nexode LAN sync.");
        var buf = new byte[len];
        await s.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    private static byte[] Concat(params byte[][] parts)
    {
        var ms = new MemoryStream();
        foreach (var p in parts) ms.Write(p);
        return ms.ToArray();
    }

    private sealed class Writer
    {
        private readonly MemoryStream _ms = new();

        public Writer Int(BigInteger v)
        {
            var b = v.ToByteArray();
            Span<byte> len = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, b.Length);
            _ms.Write(len);
            _ms.Write(b);
            return this;
        }

        public Writer Ints(BigInteger[] v)
        {
            _ms.WriteByte((byte)v.Length);
            foreach (var x in v) Int(x);
            return this;
        }

        public byte[] ToArray() => _ms.ToArray();
    }

    private sealed class Reader(byte[] data)
    {
        private int _pos;

        public BigInteger Int()
        {
            if (_pos + 4 > data.Length) throw new FormatException("Truncated handshake.");
            var len = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(_pos));
            _pos += 4;
            if (len is < 1 or > 4096 || _pos + len > data.Length) throw new FormatException("Invalid handshake value.");
            var v = new BigInteger(data[_pos..(_pos + len)]);
            _pos += len;
            return v;
        }

        public BigInteger[] Ints()
        {
            if (_pos >= data.Length) throw new FormatException("Truncated handshake.");
            var n = data[_pos++];
            if (n > 8) throw new FormatException("Invalid handshake value.");
            var r = new BigInteger[n];
            for (var i = 0; i < n; i++) r[i] = Int();
            return r;
        }

        public void End()
        {
            if (_pos != data.Length) throw new FormatException("Trailing handshake data.");
        }
    }

    // The participant id is fixed per role and not sent: a peer claiming our own role fails validation.
    private static byte[] Round1(JPakeRound1Payload p) =>
        new Writer().Int(p.Gx1).Int(p.Gx2).Ints(p.KnowledgeProofForX1).Ints(p.KnowledgeProofForX2).ToArray();

    private static JPakeRound1Payload ReadRound1(byte[] data, string peer)
    {
        var r = new Reader(data);
        var p = new JPakeRound1Payload(peer, r.Int(), r.Int(), r.Ints(), r.Ints());
        r.End();
        return p;
    }

    private static byte[] Round2(JPakeRound2Payload p) => new Writer().Int(p.A).Ints(p.KnowledgeProofForX2s).ToArray();

    private static JPakeRound2Payload ReadRound2(byte[] data, string peer)
    {
        var r = new Reader(data);
        var p = new JPakeRound2Payload(peer, r.Int(), r.Ints());
        r.End();
        return p;
    }

    private static byte[] Round3(JPakeRound3Payload p) => new Writer().Int(p.MacTag).ToArray();

    private static JPakeRound3Payload ReadRound3(byte[] data, string peer)
    {
        var r = new Reader(data);
        var p = new JPakeRound3Payload(peer, r.Int());
        r.End();
        return p;
    }

    // ------------------------------------------------------------------ discovery

    private static readonly byte[] Probe = "NXLS?1"u8.ToArray();
    private static readonly byte[] AnswerPrefix = "NXLS!1"u8.ToArray();

    /// <summary>Answers discovery probes while running (name, id and port only; no data).</summary>
    public static async Task AnswerDiscoveryAsync(Func<LanSyncAnnouncement> announce, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        while (!ct.IsCancellationRequested)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            if (!r.Buffer.AsSpan().SequenceEqual(Probe)) continue;
            var reply = Concat(AnswerPrefix, JsonSerializer.SerializeToUtf8Bytes(announce(), LanSyncJson.Default.LanSyncAnnouncement));
            try
            {
                await udp.SendAsync(reply, r.RemoteEndPoint, ct).ConfigureAwait(false);
            }
            catch (SocketException)
            {
            }
        }
    }

    /// <summary>Broadcasts a probe on every IPv4 network and collects the answers for <paramref name="wait"/>.</summary>
    public static async Task<IReadOnlyList<LanSyncAnnouncement>> DiscoverAsync(TimeSpan wait, CancellationToken ct)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        udp.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        var targets = new HashSet<IPAddress> { IPAddress.Broadcast };
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                var ip = ua.Address.GetAddressBytes();
                var mask = ua.IPv4Mask.GetAddressBytes();
                for (var i = 0; i < 4; i++) ip[i] = (byte)(ip[i] | ~mask[i]);
                targets.Add(new IPAddress(ip));
            }
        }

        foreach (var t in targets)
        {
            for (var i = 0; i < 2; i++)
            {
                try
                {
                    await udp.SendAsync(Probe, new IPEndPoint(t, DiscoveryPort), ct).ConfigureAwait(false);
                }
                catch (SocketException)
                {
                }
            }
        }

        var found = new Dictionary<string, LanSyncAnnouncement>();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(wait);
        while (true)
        {
            UdpReceiveResult r;
            try
            {
                r = await udp.ReceiveAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                continue;
            }

            if (r.Buffer.Length <= AnswerPrefix.Length || !r.Buffer.AsSpan(0, AnswerPrefix.Length).SequenceEqual(AnswerPrefix)) continue;
            try
            {
                if (JsonSerializer.Deserialize(r.Buffer.AsSpan(AnswerPrefix.Length), LanSyncJson.Default.LanSyncAnnouncement) is not { } a || a.DeviceId.Length == 0) continue;
                a.Address = r.RemoteEndPoint.Address.ToString();
                found[a.DeviceId] = a;
            }
            catch (JsonException)
            {
            }
        }

        return [.. found.Values];
    }
}

/// <summary>What a listening Nexode tells a discovery probe.</summary>
public sealed class LanSyncAnnouncement
{
    public string DeviceId { get; set; } = "";
    public string Name { get; set; } = "";
    public int Port { get; set; }

    /// <summary>Filled from the answer's sender, never trusted from the payload.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string Address { get; set; } = "";
}

[System.Text.Json.Serialization.JsonSourceGenerationOptions(PropertyNamingPolicy = System.Text.Json.Serialization.JsonKnownNamingPolicy.CamelCase)]
[System.Text.Json.Serialization.JsonSerializable(typeof(LanSyncAnnouncement))]
internal sealed partial class LanSyncJson : System.Text.Json.Serialization.JsonSerializerContext;
