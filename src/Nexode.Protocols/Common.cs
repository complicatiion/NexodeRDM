using System.Net.Sockets;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Nexode.Protocols;

/// <summary>Everything a session needs to connect (credentials already resolved and decrypted).</summary>
public sealed record ConnectParams
{
    public required string Host { get; init; }
    public required int Port { get; init; }
    public string? Username { get; init; }
    public string? Password { get; init; }
    public string? Domain { get; init; }
    public string? KeyFile { get; init; }
    public IReadOnlyDictionary<string, string> Settings { get; init; } = new Dictionary<string, string>();
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(15);

    public string? Setting(string key) => Settings.TryGetValue(key, out var v) && !string.IsNullOrEmpty(v) ? v : null;
    public bool Flag(string key) => Setting(key) is "1" or "true" or "True";

    /// <summary>Real target when the connection runs through a jump host (Host/Port are then the local tunnel end).</summary>
    public string IdentityHost => Setting(TunnelHostKey) ?? Host;
    public int IdentityPort => int.TryParse(Setting(TunnelPortKey), out var p) ? p : Port;

    /// <summary>Local port of a jump host tunnel for HTTP based protocols (the URL keeps the real host).</summary>
    public int? TunnelLocalPort => int.TryParse(Setting(TunnelLocalKey), out var p) ? p : null;

    public const string TunnelHostKey = "tunnel.host";
    public const string TunnelPortKey = "tunnel.port";
    public const string TunnelLocalKey = "tunnel.local";
}

/// <summary>A server identity seen for the first time or changed since the last connection.</summary>
public sealed record HostKeyCheck(string Kind, string Host, int Port, string Algorithm, string Fingerprint, string? KnownFingerprint)
{
    public bool IsChanged => KnownFingerprint is not null;
}

/// <summary>Questions a session asks the user while connecting. Implemented by the UI.</summary>
public interface ISessionPrompts
{
    /// <summary>True = trust (and remember) the key.</summary>
    Task<bool> ConfirmHostKeyAsync(HostKeyCheck check);

    /// <summary>Asks for a secret (key passphrase, one-time code). Null = cancelled.</summary>
    Task<string?> AskSecretAsync(string title, string prompt);
}

/// <summary>Trust-on-first-use store for SSH host keys and TLS certificates (JSON file next to the settings).</summary>
public sealed class KnownHosts
{
    private readonly string _path;
    private readonly Lock _gate = new();
    private Dictionary<string, KnownHost> _items = [];

    public KnownHosts(string path)
    {
        _path = path;
        try
        {
            if (File.Exists(path)) _items = JsonSerializer.Deserialize(File.ReadAllText(path), ProtocolJson.Default.DictionaryStringKnownHost) ?? [];
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _items = [];
        }
    }

    public static string Key(string kind, string host, int port) => $"{kind}:{host.ToLowerInvariant()}:{port}";

    public KnownHost? Get(string kind, string host, int port)
    {
        lock (_gate) return _items.GetValueOrDefault(Key(kind, host, port));
    }

    public IReadOnlyList<(string Key, KnownHost Host)> All()
    {
        lock (_gate) return _items.Select(p => (p.Key, p.Value)).OrderBy(p => p.Key).ToList();
    }

    public void Trust(string kind, string host, int port, string algorithm, string fingerprint)
    {
        lock (_gate)
        {
            _items[Key(kind, host, port)] = new KnownHost { Algorithm = algorithm, Fingerprint = fingerprint, AddedUtc = DateTime.UtcNow };
            Save();
        }
    }

    public void Remove(string key) => Remove([key]);

    /// <summary>Forgets several identities at once (one write).</summary>
    public int Remove(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            var removed = keys.Count(k => _items.Remove(k));
            if (removed > 0) Save();
            return removed;
        }
    }

    private void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_items, ProtocolJson.Default.DictionaryStringKnownHost));
            File.Move(tmp, _path, true);
        }
        catch (IOException)
        {
        }
    }

    /// <summary>Checks a presented identity; asks the user when it is new or changed. True = connect.</summary>
    public async Task<bool> VerifyAsync(string kind, string host, int port, string algorithm, string fingerprint, ISessionPrompts prompts)
    {
        var known = Get(kind, host, port);
        if (known is not null && string.Equals(known.Fingerprint, fingerprint, StringComparison.Ordinal)) return true;
        var ok = await prompts.ConfirmHostKeyAsync(new HostKeyCheck(kind, host, port, algorithm, fingerprint, known?.Fingerprint)).ConfigureAwait(false);
        if (ok) Trust(kind, host, port, algorithm, fingerprint);
        return ok;
    }
}

public sealed class KnownHost
{
    public string Algorithm { get; set; } = "";
    public string Fingerprint { get; set; } = "";
    public DateTime AddedUtc { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, KnownHost>))]
internal sealed partial class ProtocolJson : JsonSerializerContext;

/// <summary>Turns socket and protocol exceptions into short user-facing messages.</summary>
public static class NetErrors
{
    public static string Describe(Exception ex) => ex switch
    {
        SocketException { SocketErrorCode: SocketError.ConnectionRefused } => "Connection refused – is the service running and the port correct?",
        SocketException { SocketErrorCode: SocketError.HostNotFound or SocketError.NoData } => "Host not found – check the host name or DNS.",
        SocketException { SocketErrorCode: SocketError.TimedOut } => "Connection timed out – host unreachable or blocked by a firewall.",
        SocketException { SocketErrorCode: SocketError.NetworkUnreachable or SocketError.HostUnreachable } => "Network or host unreachable.",
        TimeoutException or OperationCanceledException => "Connection timed out.",
        AggregateException { InnerException: { } inner } => Describe(inner),
        _ when ex.InnerException is SocketException se => Describe(se),
        _ => ex.Message
    };

    /// <summary>Opens a TCP connection with a timeout.</summary>
    public static async Task<TcpClient> ConnectAsync(string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var client = new TcpClient { NoDelay = true };
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await client.ConnectAsync(host, port, cts.Token).ConfigureAwait(false);
            return client;
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            client.Dispose();
            throw new TimeoutException();
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }
}
