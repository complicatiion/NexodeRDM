using System.Buffers.Binary;
using System.IO.Pipes;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Security;

namespace Nexode.Protocols;

/// <summary>
/// SSH agent client: keys stay in the agent (OpenSSH agent, Pageant, KeePassXC, 1Password …); Nexode only asks it to
/// sign the login challenge. Windows: OpenSSH agent pipe, then Pageant; Linux/macOS: SSH_AUTH_SOCK.
/// </summary>
public static partial class SshAgent
{
    private const byte RequestIdentities = 11;
    private const byte IdentitiesAnswer = 12;
    private const byte SignRequest = 13;
    private const byte SignResponse = 14;
    private const uint RsaSha256 = 2;
    private const uint RsaSha512 = 4;

    /// <summary>One request/response exchange with the agent (message without the length prefix).</summary>
    internal delegate byte[]? Transport(byte[] request);

    /// <summary>Name of the agent that answered (for messages), null when none is reachable.</summary>
    public static string? Describe()
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(@"\\.\pipe\openssh-ssh-agent")) return "OpenSSH agent";
            return Pageant.IsRunning ? "Pageant" : null;
        }

        return Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } ? "SSH agent" : null;
    }

    /// <summary>The agent's keys as an SSH.NET key source; null when no agent runs or it holds no keys.</summary>
    public static IPrivateKeySource? TryOpen()
    {
        var transport = FindTransport();
        if (transport is null) return null;
        try
        {
            var answer = transport([RequestIdentities]);
            if (answer is null || answer.Length < 5 || answer[0] != IdentitiesAnswer) return null;
            var reader = new Reader(answer, 1);
            var count = reader.UInt32();
            var algorithms = new List<HostAlgorithm>();
            for (var i = 0; i < count && i < 64; i++)
            {
                var blob = reader.String();
                _ = reader.String(); // comment
                var type = Encoding.ASCII.GetString(new Reader(blob, 0).String());
                if (type == "ssh-rsa")
                {
                    // Prefer SHA-2 signatures; servers that still want SHA-1 get "ssh-rsa".
                    algorithms.Add(new AgentKey("rsa-sha2-512", blob, RsaSha512, transport));
                    algorithms.Add(new AgentKey("rsa-sha2-256", blob, RsaSha256, transport));
                    algorithms.Add(new AgentKey("ssh-rsa", blob, 0, transport));
                }
                else
                {
                    algorithms.Add(new AgentKey(type, blob, 0, transport));
                }
            }

            return algorithms.Count == 0 ? null : new KeySource(algorithms);
        }
        catch (Exception ex) when (ex is IOException or SocketException or UnauthorizedAccessException or TimeoutException or ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static Transport? FindTransport()
    {
        if (OperatingSystem.IsWindows())
        {
            if (File.Exists(@"\\.\pipe\openssh-ssh-agent")) return PipeExchange;
            return Pageant.IsRunning ? Pageant.Exchange : null;
        }

        return Environment.GetEnvironmentVariable("SSH_AUTH_SOCK") is { Length: > 0 } path ? request => SocketExchange(path, request) : null;
    }

    private static byte[]? PipeExchange(byte[] request)
    {
        using var pipe = new NamedPipeClientStream(".", "openssh-ssh-agent", PipeDirection.InOut);
        pipe.Connect(3000);
        return Exchange(pipe, request);
    }

    private static byte[]? SocketExchange(string path, byte[] request)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        socket.Connect(new UnixDomainSocketEndPoint(path));
        using var stream = new NetworkStream(socket);
        stream.ReadTimeout = 15000;
        return Exchange(stream, request);
    }

    /// <summary>Framed exchange: uint32 length + message, both directions.</summary>
    internal static byte[]? Exchange(Stream stream, byte[] request)
    {
        var frame = new byte[4 + request.Length];
        BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)request.Length);
        request.CopyTo(frame, 4);
        stream.Write(frame);
        stream.Flush();
        var head = new byte[4];
        stream.ReadExactly(head);
        var length = BinaryPrimitives.ReadUInt32BigEndian(head);
        if (length is 0 or > 256 * 1024) return null;
        var body = new byte[length];
        stream.ReadExactly(body);
        return body;
    }

    /// <summary>Builds a sign request (exposed for tests).</summary>
    internal static byte[] BuildSignRequest(byte[] keyBlob, byte[] data, uint flags)
    {
        var buffer = new byte[1 + 4 + keyBlob.Length + 4 + data.Length + 4];
        buffer[0] = SignRequest;
        var o = 1;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o), (uint)keyBlob.Length);
        keyBlob.CopyTo(buffer, o + 4);
        o += 4 + keyBlob.Length;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o), (uint)data.Length);
        data.CopyTo(buffer, o + 4);
        o += 4 + data.Length;
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(o), flags);
        return buffer;
    }

    private sealed class KeySource(IReadOnlyCollection<HostAlgorithm> algorithms) : IPrivateKeySource
    {
        public IReadOnlyCollection<HostAlgorithm> HostKeyAlgorithms { get; } = algorithms;
    }

    /// <summary>A key held by the agent: the public blob is known, signing is delegated.</summary>
    private sealed class AgentKey(string name, byte[] blob, uint flags, Transport transport) : HostAlgorithm(name)
    {
        public override byte[] Data => blob;

        public override byte[] Sign(byte[] data)
        {
            var answer = transport(BuildSignRequest(blob, data, flags));
            if (answer is null || answer.Length < 5 || answer[0] != SignResponse) throw new Renci.SshNet.Common.SshException("The SSH agent refused to sign.");
            // The agent returns the complete signature blob (algorithm name + signature), as the server expects it.
            return new Reader(answer, 1).String();
        }

        public override bool VerifySignature(byte[] data, byte[] signature) => throw new NotSupportedException();
    }

    private sealed class Reader(byte[] buffer, int offset)
    {
        private int _offset = offset;

        public uint UInt32()
        {
            var v = BinaryPrimitives.ReadUInt32BigEndian(buffer.AsSpan(_offset, 4));
            _offset += 4;
            return v;
        }

        public byte[] String()
        {
            var length = (int)UInt32();
            if (length < 0 || _offset + length > buffer.Length) throw new ArgumentOutOfRangeException(nameof(buffer));
            var v = buffer.AsSpan(_offset, length).ToArray();
            _offset += length;
            return v;
        }
    }

    /// <summary>PuTTY's Pageant through its WM_COPYDATA interface (works with every Pageant version).</summary>
    private static partial class Pageant
    {
        private const uint AgentCopyDataId = 0x804e50ba;
        private const int MaxMessage = 8192;

        public static bool IsRunning => OperatingSystem.IsWindows() && FindWindowW("Pageant", "Pageant") != 0;

        public static byte[]? Exchange(byte[] request)
        {
            var hwnd = FindWindowW("Pageant", "Pageant");
            if (hwnd == 0 || request.Length + 4 > MaxMessage) return null;
            var mapName = $"PageantRequest{Environment.CurrentManagedThreadId:x8}{Environment.ProcessId:x8}";
            var mapping = CreateFileMappingW(-1, 0, 0x04 /* PAGE_READWRITE */, 0, MaxMessage, mapName);
            if (mapping == 0) return null;
            try
            {
                var view = MapViewOfFile(mapping, 0x000F001F /* FILE_MAP_ALL_ACCESS */, 0, 0, 0);
                if (view == 0) return null;
                try
                {
                    var frame = new byte[4 + request.Length];
                    BinaryPrimitives.WriteUInt32BigEndian(frame, (uint)request.Length);
                    request.CopyTo(frame, 4);
                    Marshal.Copy(frame, 0, view, frame.Length);

                    var name = Encoding.ASCII.GetBytes(mapName + "\0");
                    var namePtr = Marshal.AllocHGlobal(name.Length);
                    var cds = Marshal.AllocHGlobal(3 * IntPtr.Size);
                    try
                    {
                        Marshal.Copy(name, 0, namePtr, name.Length);
                        Marshal.WriteIntPtr(cds, 0, unchecked((nint)AgentCopyDataId));
                        Marshal.WriteInt32(cds, IntPtr.Size, name.Length);
                        Marshal.WriteIntPtr(cds, 2 * IntPtr.Size, namePtr);
                        if (SendMessageW(hwnd, 0x004A /* WM_COPYDATA */, 0, cds) == 0) return null;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(cds);
                        Marshal.FreeHGlobal(namePtr);
                    }

                    var head = new byte[4];
                    Marshal.Copy(view, head, 0, 4);
                    var length = BinaryPrimitives.ReadUInt32BigEndian(head);
                    if (length is 0 or > MaxMessage - 4) return null;
                    var body = new byte[length];
                    Marshal.Copy(view + 4, body, 0, (int)length);
                    return body;
                }
                finally
                {
                    UnmapViewOfFile(view);
                }
            }
            finally
            {
                CloseHandle(mapping);
            }
        }

        [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
        private static partial nint FindWindowW(string className, string windowName);

        [LibraryImport("user32.dll")]
        private static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

        [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint CreateFileMappingW(nint file, nint attributes, uint protect, uint sizeHigh, uint sizeLow, string name);

        [LibraryImport("kernel32.dll", SetLastError = true)]
        private static partial nint MapViewOfFile(nint mapping, uint access, uint offsetHigh, uint offsetLow, nuint bytes);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnmapViewOfFile(nint view);

        [LibraryImport("kernel32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool CloseHandle(nint handle);
    }
}
