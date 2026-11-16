using System.Net;
using System.Net.Sockets;
using Nexode.Protocols.Terminal;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Nexode.Protocols;

/// <summary>
/// Jump host: an SSH connection that forwards local ports (127.0.0.1 only) to targets behind the jump host, or
/// offers a SOCKS5 proxy (dynamic forwarding) for web panels.
/// </summary>
public sealed class SshTunnel : IDisposable
{
    private readonly SshClient _client;
    private readonly List<ForwardedPort> _ports = [];

    private SshTunnel(SshClient client) => _client = client;

    public string JumpHost { get; private init; } = "";
    public bool IsConnected => _client.IsConnected;
    public event Action<string?>? Closed;

    public static async Task<SshTunnel> OpenAsync(ConnectParams jump, ISessionPrompts prompts, KnownHosts knownHosts, CancellationToken ct)
    {
        var info = await SshConnections.CreateConnectionInfoAsync(jump, prompts).ConfigureAwait(false);
        var client = new SshClient(info) { KeepAliveInterval = TimeSpan.FromSeconds(30) };
        SshConnections.AttachHostKeyCheck(client, jump, prompts, knownHosts);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }

        var tunnel = new SshTunnel(client) { JumpHost = $"{jump.Host}:{jump.Port}" };
        client.ErrorOccurred += (_, e) => tunnel.Closed?.Invoke(NetErrors.Describe(e.Exception));
        return tunnel;
    }

    /// <summary>A free local TCP port (loopback).</summary>
    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>Forwards 127.0.0.1:&lt;local&gt; → host:port (seen from the jump host). Returns the local port.</summary>
    public int ForwardLocal(string host, int port)
    {
        for (var attempt = 0; ; attempt++)
        {
            var local = FreePort();
            var fwd = new ForwardedPortLocal("127.0.0.1", (uint)local, host, (uint)port);
            try
            {
                _client.AddForwardedPort(fwd);
                fwd.Start();
                _ports.Add(fwd);
                return local;
            }
            catch (Exception ex) when (attempt < 3 && ex is SocketException or SshException)
            {
                // Port taken in between: try another one.
                try { _client.RemoveForwardedPort(fwd); } catch (Exception ex2) when (ex2 is SshException or InvalidOperationException) { }
            }
        }
    }

    /// <summary>SOCKS5 proxy on 127.0.0.1 through the jump host. Returns the local port.</summary>
    public int StartSocks()
    {
        var local = FreePort();
        var fwd = new ForwardedPortDynamic("127.0.0.1", (uint)local);
        _client.AddForwardedPort(fwd);
        fwd.Start();
        _ports.Add(fwd);
        return local;
    }

    public void Dispose()
    {
        foreach (var p in _ports)
        {
            try { p.Stop(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException or InvalidOperationException) { }
        }

        try { _client.Disconnect(); } catch (Exception ex) when (ex is SshException or ObjectDisposedException or SocketException) { }
        _client.Dispose();
    }
}

public enum ReachState
{
    Unknown,
    Checking,
    Online,
    Offline
}

/// <summary>Reachability check: TCP connect to the service port, ICMP ping when there is no port.</summary>
public static class StatusProbe
{
    public static async Task<(ReachState State, int? Millis)> CheckAsync(string host, int? port, TimeSpan timeout, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            if (port is > 0)
            {
                using var tcp = await NetErrors.ConnectAsync(host, port.Value, timeout, ct).ConfigureAwait(false);
                return (ReachState.Online, (int)sw.ElapsedMilliseconds);
            }

            using var ping = new System.Net.NetworkInformation.Ping();
            var reply = await ping.SendPingAsync(host, timeout, null, null, ct).ConfigureAwait(false);
            return reply.Status == System.Net.NetworkInformation.IPStatus.Success ? (ReachState.Online, (int)reply.RoundtripTime) : (ReachState.Offline, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return (ReachState.Unknown, null);
        }
        catch (Exception ex) when (ex is SocketException or TimeoutException or OperationCanceledException or System.Net.NetworkInformation.PingException or InvalidOperationException or ArgumentException)
        {
            return (ReachState.Offline, null);
        }
    }
}
