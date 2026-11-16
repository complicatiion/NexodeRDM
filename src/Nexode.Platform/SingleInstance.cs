using System.IO.Pipes;
using System.Text;

namespace Nexode.Platform;

/// <summary>
/// One Nexode process per user: a second start hands its arguments (files to import, --new-window) to the running
/// instance over a named pipe and exits. A named mutex tells whether an instance runs at all, so the first start
/// does not wait for a pipe that nobody serves.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _cts = new();
    private Mutex? _mutex;

    /// <summary>True when this process holds the instance mutex (it is the running instance, no pipe was probed).</summary>
    public bool IsFirst => _mutex is not null;

    public SingleInstance(string appId)
    {
        _pipeName = $"{appId}-{Environment.UserName}".Replace(' ', '_');
    }

    public event Action<string[]>? ArgumentsReceived;

    /// <summary>True when another instance took the arguments (this process should exit).</summary>
    public bool TryForward(string[] args)
    {
        var running = false;
        try
        {
            _mutex = new Mutex(true, "Local\\" + _pipeName, out var first);
            if (first) return false;
            running = true;
            _mutex.Dispose();
            _mutex = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        {
            // No mutex (restricted session): fall back to probing the pipe.
        }

        try
        {
            using var client = new NamedPipeClientStream(".", _pipeName, PipeDirection.Out);
            // An instance runs: its pipe may still be starting up.
            client.Connect(running ? 2000 : 250);
            var payload = Encoding.UTF8.GetBytes(string.Join('\n', args.Length == 0 ? ["--activate"] : args));
            client.Write(payload);
            client.Flush();
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Serves the pipe. The first pipe instance exists when this returns, and the next one is opened as soon as a
    /// start connects – a second start never finds the pipe missing (that made it start as its own instance).
    /// </summary>
    public void StartListening()
    {
        NamedPipeServerStream? first;
        try
        {
            first = Create();
        }
        catch (IOException)
        {
            first = null;
        }

        _ = Task.Run(async () =>
        {
            var server = first;
            while (!_cts.IsCancellationRequested)
            {
                try
                {
                    server ??= Create();
                    await server.WaitForConnectionAsync(_cts.Token);
                    var current = server;
                    server = null;
                    server = Create();
                    _ = Task.Run(() => ReadAsync(current));
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    server?.Dispose();
                    server = null;
                    await Task.Delay(200);
                }
            }

            server?.Dispose();
        });
    }

    private NamedPipeServerStream Create() => new(_pipeName, PipeDirection.In, NamedPipeServerStream.MaxAllowedServerInstances,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

    private async Task ReadAsync(NamedPipeServerStream pipe)
    {
        try
        {
            await using (pipe)
            {
                using var ms = new MemoryStream();
                await pipe.CopyToAsync(ms, _cts.Token);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                ArgumentsReceived?.Invoke(text.Split('\n', StringSplitOptions.RemoveEmptyEntries));
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
        _cts.Cancel();
        _cts.Dispose();
    }
}
