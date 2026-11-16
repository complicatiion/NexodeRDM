using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Nexode.Core.Model;

namespace Nexode.Protocols.Terminal;

/// <summary>Line settings of a serial port.</summary>
public sealed record SerialSettings(string Port, int Baud = 9600, int DataBits = 8, char Parity = 'N', int StopBits = 1, string Flow = "none")
{
    public static readonly int[] BaudRates = [1200, 2400, 4800, 9600, 19200, 38400, 57600, 115200, 230400, 460800, 921600];

    public static SerialSettings From(ConnectParams p) => new(
        p.Host,
        int.TryParse(p.Setting(SettingKeys.SerialBaud), out var b) && b > 0 ? b : 9600,
        int.TryParse(p.Setting(SettingKeys.SerialDataBits), out var d) && d is >= 5 and <= 8 ? d : 8,
        p.Setting(SettingKeys.SerialParity) is { Length: > 0 } par ? char.ToUpperInvariant(par[0]) : 'N',
        p.Setting(SettingKeys.SerialStopBits) == "2" ? 2 : 1,
        p.Setting(SettingKeys.SerialFlow) ?? "none");

    /// <summary>"9600 8N1".</summary>
    public override string ToString() => $"{Baud} {DataBits}{Parity}{StopBits}";
}

/// <summary>
/// Serial console (COM / tty) for switches, firewalls and other devices. No extra package: Win32 comm API on Windows,
/// stty + read/write on Linux and macOS.
/// </summary>
public sealed class SerialConnection(ConnectParams p) : ITerminalConnection
{
    private readonly SerialSettings _settings = SerialSettings.From(p);
    private SafeFileHandle? _handle;
    private CancellationTokenSource? _cts;
    private int _closed;
    private readonly bool _echo = p.Flag(SettingKeys.SerialEcho);
    private readonly string _newLine = p.Setting(SettingKeys.SerialNewLine) ?? "cr";

    public event Action<byte[]>? DataReceived;
    public event Action<string?>? Closed;

    public bool IsConnected => _handle is { IsClosed: false, IsInvalid: false };

    public Task ConnectAsync(int columns, int rows, CancellationToken ct) => Task.Run(() =>
    {
        _handle = OperatingSystem.IsWindows() ? OpenWindows(_settings) : OpenUnix(_settings);
        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        new Thread(() => ReadLoop(token)) { IsBackground = true, Name = "serial " + _settings.Port }.Start();
    }, ct);

    private void ReadLoop(CancellationToken ct)
    {
        var buf = new byte[4096];
        try
        {
            while (!ct.IsCancellationRequested && _handle is { IsClosed: false } h)
            {
                var n = OperatingSystem.IsWindows() ? Win.Read(h, buf) : Posix.Read(h, buf);
                if (n < 0) throw new IOException(Localization.PortGone(_settings.Port));
                if (n > 0) DataReceived?.Invoke(buf[..n]);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
        {
            if (!ct.IsCancellationRequested) Close(ex.Message);
            return;
        }

        Close(null);
    }

    public void Send(byte[] data)
    {
        if (_handle is not { IsClosed: false } h) return;
        // Enter sends the configured line ending.
        if (data is [13] && _newLine != "cr") data = _newLine == "crlf" ? [13, 10] : [10];
        try
        {
            if (OperatingSystem.IsWindows()) Win.Write(h, data);
            else Posix.Write(h, data);
            if (_echo) DataReceived?.Invoke(data is [13] or [13, 10] or [10] ? [13, 10] : data);
        }
        catch (IOException ex)
        {
            Close(ex.Message);
        }
    }

    public void Resize(int columns, int rows)
    {
    }

    private void Close(string? reason)
    {
        if (Interlocked.Exchange(ref _closed, 1) != 0) return;
        Closed?.Invoke(reason);
    }

    public void Dispose()
    {
        Interlocked.Exchange(ref _closed, 1);
        _cts?.Cancel();
        _handle?.Dispose();
        _handle = null;
    }

    /// <summary>Serial ports of this computer (COM3, /dev/ttyUSB0, /dev/cu.usbserial-…).</summary>
    public static IReadOnlyList<string> ListPorts()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"HARDWARE\DEVICEMAP\SERIALCOMM");
                return key is null ? [] : [.. key.GetValueNames().Select(n => key.GetValue(n) as string).OfType<string>().Distinct().Order(new PortComparer())];
            }

            var patterns = OperatingSystem.IsMacOS() ? new[] { "cu.*" } : ["ttyUSB*", "ttyACM*", "ttyAMA*", "rfcomm*"];
            var list = patterns.SelectMany(pt => Directory.EnumerateFiles("/dev", pt)).ToList();
            if (OperatingSystem.IsLinux() && Directory.Exists("/sys/class/tty"))
            {
                // Built-in UARTs only when they have real hardware behind them.
                list.AddRange(Directory.EnumerateDirectories("/sys/class/tty", "ttyS*").Where(d => Directory.Exists(Path.Combine(d, "device/driver")) &&
                    !File.ReadAllText(Path.Combine(d, "type")).Trim().Equals("0", StringComparison.Ordinal)).Select(d => "/dev/" + Path.GetFileName(d)));
            }

            return [.. list.Distinct().Order(new PortComparer())];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            return [];
        }
    }

    private sealed class PortComparer : IComparer<string>
    {
        public int Compare(string? x, string? y)
        {
            static (string, int) Split(string s)
            {
                var i = s.Length;
                while (i > 0 && char.IsDigit(s[i - 1])) i--;
                return (s[..i], int.TryParse(s[i..], out var n) ? n : -1);
            }

            var (a, an) = Split(x ?? "");
            var (b, bn) = Split(y ?? "");
            var c = string.CompareOrdinal(a, b);
            return c != 0 ? c : an.CompareTo(bn);
        }
    }

    // ------------------------------------------------------------------ Windows

    private static SafeFileHandle OpenWindows(SerialSettings s)
    {
        var name = s.Port.StartsWith(@"\\", StringComparison.Ordinal) ? s.Port : @"\\.\" + s.Port;
        var h = Win.CreateFileW(name, 0xC0000000, 0, IntPtr.Zero, 3, 0, IntPtr.Zero);
        if (h.IsInvalid)
        {
            var err = Marshal.GetLastPInvokeError();
            h.Dispose();
            throw err switch
            {
                2 or 3 => new IOException(Localization.PortMissing(s.Port)),
                5 => new UnauthorizedAccessException(Localization.PortBusy(s.Port)),
                _ => new IOException(new System.ComponentModel.Win32Exception(err).Message)
            };
        }

        var dcb = new Win.Dcb { DCBlength = (uint)Marshal.SizeOf<Win.Dcb>() };
        if (!Win.GetCommState(h, ref dcb)) throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
        dcb.BaudRate = (uint)s.Baud;
        dcb.ByteSize = (byte)s.DataBits;
        dcb.Parity = s.Parity switch { 'E' => (byte)2, 'O' => (byte)1, 'M' => (byte)3, 'S' => (byte)4, _ => (byte)0 };
        dcb.StopBits = s.StopBits == 2 ? (byte)2 : (byte)0;
        // fBinary | fParity(if) | fOutxCtsFlow | fDtrControl=ENABLE | fOutX/fInX | fRtsControl
        uint flags = 1;
        if (dcb.Parity != 0) flags |= 1 << 1;
        flags |= 1 << 4; // DTR enable
        switch (s.Flow)
        {
            case "rts":
                flags |= 1 << 2; // CTS out flow
                flags |= 2u << 12; // RTS handshake
                break;
            case "xon":
                flags |= (1 << 8) | (1 << 9); // fOutX, fInX
                flags |= 1u << 12; // RTS enable
                break;
            default:
                flags |= 1u << 12; // RTS enable
                break;
        }

        dcb.Flags = flags;
        dcb.XonChar = 0x11;
        dcb.XoffChar = 0x13;
        dcb.XonLim = 2048;
        dcb.XoffLim = 512;
        if (!Win.SetCommState(h, ref dcb)) throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
        // Return as soon as data is there, at the latest after 50 ms (keeps writes responsive).
        var timeouts = new Win.CommTimeouts { ReadIntervalTimeout = uint.MaxValue, ReadTotalTimeoutMultiplier = uint.MaxValue, ReadTotalTimeoutConstant = 50, WriteTotalTimeoutConstant = 2000 };
        Win.SetCommTimeouts(h, ref timeouts);
        return h;
    }

    private static class Win
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct Dcb
        {
            public uint DCBlength;
            public uint BaudRate;
            public uint Flags;
            public ushort wReserved;
            public ushort XonLim;
            public ushort XoffLim;
            public byte ByteSize;
            public byte Parity;
            public byte StopBits;
            public byte XonChar;
            public byte XoffChar;
            public byte ErrorChar;
            public byte EofChar;
            public byte EvtChar;
            public ushort wReserved1;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct CommTimeouts
        {
            public uint ReadIntervalTimeout;
            public uint ReadTotalTimeoutMultiplier;
            public uint ReadTotalTimeoutConstant;
            public uint WriteTotalTimeoutMultiplier;
            public uint WriteTotalTimeoutConstant;
        }

        [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool GetCommState(SafeFileHandle h, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetCommState(SafeFileHandle h, ref Dcb dcb);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern bool SetCommTimeouts(SafeFileHandle h, ref CommTimeouts t);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool ReadFile(SafeFileHandle h, byte* buffer, int count, out int read, IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern unsafe bool WriteFile(SafeFileHandle h, byte* buffer, int count, out int written, IntPtr overlapped);

        public static unsafe int Read(SafeFileHandle h, byte[] buf)
        {
            fixed (byte* b = buf)
            {
                return ReadFile(h, b, buf.Length, out var n, IntPtr.Zero) ? n : -1;
            }
        }

        public static unsafe void Write(SafeFileHandle h, byte[] data)
        {
            fixed (byte* b = data)
            {
                if (!WriteFile(h, b, data.Length, out _, IntPtr.Zero)) throw new IOException(new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()).Message);
            }
        }
    }

    // ------------------------------------------------------------------ Linux / macOS

    private static SafeFileHandle OpenUnix(SerialSettings s)
    {
        if (!File.Exists(s.Port) && !s.Port.StartsWith("/dev/", StringComparison.Ordinal)) throw new IOException(Localization.PortMissing(s.Port));
        var args = new List<string> { OperatingSystem.IsMacOS() ? "-f" : "-F", s.Port, s.Baud.ToString(System.Globalization.CultureInfo.InvariantCulture), "cs" + s.DataBits };
        args.AddRange(s.StopBits == 2 ? ["cstopb"] : ["-cstopb"]);
        args.AddRange(s.Parity switch { 'E' => ["parenb", "-parodd"], 'O' => ["parenb", "parodd"], _ => ["-parenb"] });
        args.AddRange(["raw", "-echo", "-echoe", "-echok", "clocal", "cread", "min", "0", "time", "2"]);
        args.AddRange(s.Flow switch { "rts" => ["crtscts", "-ixon", "-ixoff"], "xon" => ["-crtscts", "ixon", "ixoff"], _ => ["-crtscts", "-ixon", "-ixoff"] });

        var psi = new ProcessStartInfo("stty") { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using (var proc = Process.Start(psi) ?? throw new IOException("stty"))
        {
            var err = proc.StandardError.ReadToEnd();
            proc.WaitForExit(5000);
            if (proc.ExitCode != 0)
            {
                throw err.Contains("Permission denied", StringComparison.OrdinalIgnoreCase)
                    ? new UnauthorizedAccessException(Localization.PortDenied(s.Port))
                    : new IOException(err.Trim().Length > 0 ? err.Trim() : Localization.PortMissing(s.Port));
            }
        }

        var fd = Posix.open(s.Port, Posix.O_RDWR | Posix.O_NOCTTY);
        if (fd < 0)
        {
            var errno = Marshal.GetLastPInvokeError();
            throw errno == 13 ? new UnauthorizedAccessException(Localization.PortDenied(s.Port)) : new IOException(Localization.PortBusy(s.Port));
        }

        return new SafeFileHandle(fd, ownsHandle: true);
    }

    private static class Posix
    {
        public const int O_RDWR = 2;
        public static int O_NOCTTY => OperatingSystem.IsMacOS() ? 0x20000 : 0x100;

        [DllImport("libc", SetLastError = true)]
        public static extern int open(string path, int flags);

        [DllImport("libc", SetLastError = true)]
        private static extern unsafe nint read(int fd, byte* buf, nint count);

        [DllImport("libc", SetLastError = true)]
        private static extern unsafe nint write(int fd, byte* buf, nint count);

        public static unsafe int Read(SafeFileHandle h, byte[] buf)
        {
            fixed (byte* b = buf)
            {
                var n = read((int)h.DangerousGetHandle(), b, buf.Length);
                if (n >= 0) return (int)n;
                var errno = Marshal.GetLastPInvokeError();
                return errno is 4 or 11 ? 0 : -1; // EINTR, EAGAIN
            }
        }

        public static unsafe void Write(SafeFileHandle h, byte[] data)
        {
            fixed (byte* b = data)
            {
                var off = 0;
                while (off < data.Length)
                {
                    var n = write((int)h.DangerousGetHandle(), b + off, data.Length - off);
                    if (n < 0) throw new IOException("write failed (" + Marshal.GetLastPInvokeError() + ")");
                    off += (int)n;
                }
            }
        }
    }

    /// <summary>Messages (the app maps them to its language via <see cref="Texts"/>).</summary>
    public static class Localization
    {
        public static Func<string, string, string> Texts { get; set; } = (key, port) => key switch
        {
            "missing" => $"Serial port {port} not found.",
            "busy" => $"Serial port {port} is in use by another program.",
            "denied" => $"No permission for {port} (Linux: add your user to the group \"dialout\").",
            _ => $"Serial port {port} was removed."
        };

        public static string PortMissing(string port) => Texts("missing", port);
        public static string PortBusy(string port) => Texts("busy", port);
        public static string PortDenied(string port) => Texts("denied", port);
        public static string PortGone(string port) => Texts("gone", port);
    }
}
