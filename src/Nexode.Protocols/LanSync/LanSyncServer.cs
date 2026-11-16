using System.Net;
using System.Net.Sockets;

namespace Nexode.Protocols.LanSync;

/// <summary>
/// Accepts LAN sync connections while "allow sync" is on. One sync at a time; failed password attempts are limited
/// per address (3 tries, then 5 minutes blocked) and overall (20 tries, then the server stops).
/// </summary>
public sealed class LanSyncServer : IDisposable
{
    private const int MaxFailuresPerAddress = 3;
    private const int MaxFailuresTotal = 20;
    private static readonly TimeSpan BlockTime = TimeSpan.FromMinutes(5);

    private readonly string _password;
    private readonly Func<LanSyncChannel, CancellationToken, Task> _handler;
    private readonly Func<LanSyncAnnouncement>? _announce;
    private readonly CancellationTokenSource _cts = new();
    private readonly Dictionary<IPAddress, (int Count, DateTime Until)> _failures = [];
    private readonly SemaphoreSlim _one = new(1, 1);
    private TcpListener? _listener;
    private int _totalFailures;

    public LanSyncServer(int port, string password, Func<LanSyncChannel, CancellationToken, Task> handler, Func<LanSyncAnnouncement>? announce, IPAddress? bind = null)
    {
        Port = port;
        Bind = bind ?? IPAddress.Any;
        _password = password;
        _handler = handler;
        _announce = announce;
    }

    public int Port { get; private set; }
    public IPAddress Bind { get; }
    public bool IsRunning => _listener is not null && !_cts.IsCancellationRequested;

    /// <summary>(kind, remote address, detail): "auth-failed", "blocked", "stopped", "error".</summary>
    public event Action<string, string, string>? Activity;

    public void Start()
    {
        var l = new TcpListener(Bind, Port);
        l.Start();
        _listener = l;
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        _ = Task.Run(AcceptLoop);
        if (_announce is not null)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await LanSyncChannel.AnswerDiscoveryAsync(_announce, _cts.Token).ConfigureAwait(false);
                }
                catch (SocketException ex)
                {
                    // Discovery port taken (second instance): manual address still works.
                    Activity?.Invoke("error", "", "discovery: " + ex.Message);
                }
            });
        }
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested && _listener is { } l)
        {
            TcpClient tcp;
            try
            {
                tcp = await l.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                break;
            }

            _ = Task.Run(() => HandleAsync(tcp));
        }
    }

    private async Task HandleAsync(TcpClient tcp)
    {
        var address = (tcp.Client.RemoteEndPoint as IPEndPoint)?.Address ?? IPAddress.None;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        lock (_failures)
        {
            if (_failures.TryGetValue(address, out var f) && f.Count >= MaxFailuresPerAddress && f.Until > DateTime.UtcNow)
            {
                tcp.Dispose();
                return;
            }
        }

        // One sync at a time; a second caller waits briefly, then gives up.
        if (!await _one.WaitAsync(TimeSpan.FromSeconds(30), _cts.Token).ConfigureAwait(false))
        {
            tcp.Dispose();
            return;
        }

        try
        {
            using var channel = await LanSyncChannel.AcceptAsync(tcp, _password, _cts.Token).ConfigureAwait(false);
            lock (_failures) _failures.Remove(address);
            await _handler(channel, _cts.Token).ConfigureAwait(false);
        }
        catch (LanSyncAuthException ex)
        {
            bool stop;
            lock (_failures)
            {
                var count = _failures.TryGetValue(address, out var f) && f.Until > DateTime.UtcNow ? f.Count + 1 : 1;
                _failures[address] = (count, DateTime.UtcNow + BlockTime);
                stop = ++_totalFailures >= MaxFailuresTotal;
                if (count >= MaxFailuresPerAddress) Activity?.Invoke("blocked", address.ToString(), "");
            }

            Activity?.Invoke("auth-failed", address.ToString(), ex.Message);
            if (stop)
            {
                Activity?.Invoke("stopped", "", "too many failed attempts");
                Dispose();
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException)
        {
            if (!_cts.IsCancellationRequested) Activity?.Invoke("error", address.ToString(), ex.Message);
        }
        finally
        {
            tcp.Dispose();
            try { _one.Release(); } catch (ObjectDisposedException) { }
        }
    }

    public void Dispose()
    {
        if (_cts.IsCancellationRequested) return;
        _cts.Cancel();
        try { _listener?.Stop(); } catch (SocketException) { }
        _listener = null;
    }
}
