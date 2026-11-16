using System.Runtime.InteropServices;
using System.Text;
using Nexode.Core.Model;
using Nexode.Protocols.Terminal;
using Xunit;

namespace Nexode.Protocols.Tests;

public class SerialTests
{
    [DllImport("libc", SetLastError = true)]
    private static extern int openpty(out int master, out int slave, byte[]? name, IntPtr termp, IntPtr winp);

    [DllImport("libc", SetLastError = true)]
    private static extern nint read(int fd, byte[] buf, nint count);

    [DllImport("libc", SetLastError = true)]
    private static extern nint write(int fd, byte[] buf, nint count);

    [DllImport("libc")]
    private static extern int close(int fd);

    [Fact]
    public void WakeOnLanPacket()
    {
        var mac = WakeOnLan.ParseMac("00-11-22-aa-bb-cc")!;
        Assert.Equal("00:11:22:AA:BB:CC", WakeOnLan.FormatMac(mac));
        Assert.Equal(mac, WakeOnLan.ParseMac("0011.22AA.BBCC"));
        Assert.Null(WakeOnLan.ParseMac("00:11:22"));
        var p = WakeOnLan.Packet(mac);
        Assert.Equal(102, p.Length);
        Assert.All(p[..6], b => Assert.Equal(0xFF, b));
        Assert.Equal(mac, p[96..102]);
    }

    [Fact]
    public async Task SerialRoundTripOverPty()
    {
        if (!OperatingSystem.IsLinux()) return;
        var name = new byte[128];
        Assert.Equal(0, openpty(out var master, out var slave, name, IntPtr.Zero, IntPtr.Zero));
        var path = Encoding.ASCII.GetString(name, 0, Array.IndexOf(name, (byte)0));
        try
        {
            var p = new ConnectParams
            {
                Host = path, Port = 0,
                Settings = new Dictionary<string, string> { [SettingKeys.SerialBaud] = "115200", [SettingKeys.SerialNewLine] = "crlf" }
            };
            using var conn = new SerialConnection(p);
            var received = new StringBuilder();
            var got = new TaskCompletionSource();
            conn.DataReceived += d =>
            {
                lock (received)
                {
                    received.Append(Encoding.ASCII.GetString(d));
                    if (received.ToString().Contains("Switch>", StringComparison.Ordinal)) got.TrySetResult();
                }
            };
            await conn.ConnectAsync(80, 24, CancellationToken.None);
            Assert.Equal(0, close(slave));
            slave = -1;

            var prompt = Encoding.ASCII.GetBytes("\r\nSwitch>");
            write(master, prompt, prompt.Length);
            await got.Task.WaitAsync(TimeSpan.FromSeconds(5));

            conn.Send("show\r"u8.ToArray()[..4]);
            conn.Send([13]);
            var buf = new byte[64];
            var text = "";
            for (var i = 0; i < 20 && !text.Contains("\r\n", StringComparison.Ordinal); i++)
            {
                var n = read(master, buf, buf.Length);
                if (n > 0) text += Encoding.ASCII.GetString(buf, 0, (int)n);
            }

            Assert.Equal("show\r\n", text);
            _ = SerialConnection.ListPorts();
        }
        finally
        {
            if (slave >= 0) close(slave);
            close(master);
        }
    }
}
