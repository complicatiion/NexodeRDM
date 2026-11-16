using System.Net;
using System.Net.Sockets;

namespace Nexode.Protocols;

/// <summary>Wake-on-LAN magic packet (6 × FF, 16 × MAC) as UDP broadcast.</summary>
public static class WakeOnLan
{
    /// <summary>Parses "00:11:22:33:44:55", "00-11-22-33-44-55", "0011.2233.4455" or "001122334455".</summary>
    public static byte[]? ParseMac(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var hex = new string(text.Where(Uri.IsHexDigit).ToArray());
        if (hex.Length != 12) return null;
        return Convert.FromHexString(hex);
    }

    public static string FormatMac(byte[] mac) => string.Join(":", mac.Select(b => b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture)));

    public static byte[] Packet(byte[] mac)
    {
        var p = new byte[102];
        for (var i = 0; i < 6; i++) p[i] = 0xFF;
        for (var i = 1; i <= 16; i++) Buffer.BlockCopy(mac, 0, p, i * 6, 6);
        return p;
    }

    /// <summary>Sends the packet to the broadcast address (default 255.255.255.255) on <paramref name="port"/> (default 9).</summary>
    public static async Task SendAsync(byte[] mac, string? broadcast = null, int port = 9)
    {
        var target = IPAddress.Broadcast;
        if (!string.IsNullOrWhiteSpace(broadcast))
        {
            target = IPAddress.TryParse(broadcast.Trim(), out var ip) ? ip : (await Dns.GetHostAddressesAsync(broadcast.Trim()).ConfigureAwait(false))
                .First(a => a.AddressFamily == AddressFamily.InterNetwork);
        }

        using var udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        var packet = Packet(mac);
        // Three times: UDP can get lost, waking twice does no harm.
        for (var i = 0; i < 3; i++)
        {
            await udp.SendAsync(packet, new IPEndPoint(target, port)).ConfigureAwait(false);
            await Task.Delay(60).ConfigureAwait(false);
        }
    }

    /// <summary>Polls a TCP port until it accepts connections or <paramref name="timeout"/> passes.</summary>
    public static async Task<bool> WaitForPortAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until && !ct.IsCancellationRequested)
        {
            try
            {
                using var tcp = new TcpClient();
                using var attempt = CancellationTokenSource.CreateLinkedTokenSource(ct);
                attempt.CancelAfter(TimeSpan.FromSeconds(2));
                await tcp.ConnectAsync(host, port, attempt.Token).ConfigureAwait(false);
                return true;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException)
            {
                if (ct.IsCancellationRequested) return false;
                await Task.Delay(1500, CancellationToken.None).ConfigureAwait(false);
            }
        }

        return false;
    }
}
