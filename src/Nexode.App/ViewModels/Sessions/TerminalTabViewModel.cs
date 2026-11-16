using System.Collections.Concurrent;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Protocols;
using Nexode.Protocols.Terminal;

namespace Nexode.App.ViewModels.Sessions;

/// <summary>SSH or Telnet terminal in a tab.</summary>
public sealed partial class TerminalTabViewModel : SessionTabViewModel
{
    private readonly ConcurrentQueue<byte[]> _incoming = new();
    private ITerminalConnection? _connection;
    private int _feedQueued;

    public TerminalTabViewModel(Guid entryId, IReadOnlyDictionary<string, string>? overrides = null) : base(entryId, overrides)
    {
        Terminal = new TerminalEmulator(120, 32, AppHost.Settings.TerminalScrollback);
        Terminal.Response += bytes => _connection?.Send(bytes);
        Terminal.TitleChanged += () => Dispatcher.UIThread.Post(() => RemoteTitle = Terminal.Title);
        _fontSize = AppHost.Settings.TerminalFontSize;
    }

    public TerminalEmulator Terminal { get; }
    public bool IsSsh => Entry?.Kind == EntryKind.Ssh;
    public override TabViewModel Recreate() => new TerminalTabViewModel(EntryId, Overrides);

    [ObservableProperty]
    private string _remoteTitle = "";

    [ObservableProperty]
    private double _fontSize;

    public int Columns { get; set; } = 120;
    public int Rows { get; set; } = 32;

    protected override async Task ConnectCoreAsync(ConnectParams p, ISessionPrompts prompts, CancellationToken ct)
    {
        _connection?.Dispose();
        ITerminalConnection connection = Entry?.Kind switch
        {
            EntryKind.Telnet => new TelnetConnection(p),
            EntryKind.Serial => new SerialConnection(p),
            EntryKind.PowerShell => PowerShell(p),
            _ => new SshTerminalConnection(p, prompts, AppHost.KnownHosts)
        };
        connection.DataReceived += OnData;
        connection.Closed += OnRemoteClosed;
        _connection = connection;
        lock (Terminal.Sync)
        {
            Terminal.Feed($"\e[2m{Localization.Loc.F("Session.Connecting", Entry?.Kind == EntryKind.Serial ? $"{p.Host} ({SerialSettings.From(p)})" : TargetText)}\e[0m\r\n");
        }

        await connection.ConnectAsync(Columns, Rows, ct);
    }

    /// <summary>Local PowerShell (Windows PowerShell or PowerShell 7) that enters the remote session.</summary>
    private static LocalProcessConnection PowerShell(ConnectParams p)
    {
        var exe = PowerShellLaunch.FindExecutable(p, Services.ExternalLauncher.Which, OperatingSystem.IsWindows(), OperatingSystem.IsWindows() ? Environment.SystemDirectory : null)
                  ?? throw new InvalidOperationException(Localization.Loc.T(PowerShellLaunch.Transport(p) == "ssh" || !OperatingSystem.IsWindows() ? "Ps.NoPwsh" : "Ps.NoPowerShell"));
        var plan = PowerShellLaunch.Build(p, exe);
        return new LocalProcessConnection(plan.Executable, plan.Arguments, plan.Secret, plan.SecretPrompt);
    }

    private void OnData(byte[] data)
    {
        _incoming.Enqueue(data);
        if (Interlocked.Exchange(ref _feedQueued, 1) == 0) Dispatcher.UIThread.Post(Drain, DispatcherPriority.Render);
    }

    private void Drain()
    {
        Interlocked.Exchange(ref _feedQueued, 0);
        lock (Terminal.Sync)
        {
            while (_incoming.TryDequeue(out var chunk)) Terminal.Feed(chunk);
        }
    }

    public void SendInput(byte[] data)
    {
        if (_connection?.IsConnected == true) _connection.Send(data);
        else if (IsEnded && data is [13]) _ = ReconnectCommand.ExecuteAsync(null);
    }

    /// <summary>Typed input: repeated to the other terminals of the split view while "send to all" is on.</summary>
    public void Typed(byte[] data) => Split?.SendToOthers(this, data);

    /// <summary>Input from another pane: only to a live connection (never triggers a reconnect).</summary>
    public void SendMirrored(byte[] data)
    {
        if (_connection?.IsConnected == true) _connection.Send(data);
    }

    public void OnResized(int cols, int rows)
    {
        Columns = cols;
        Rows = rows;
        _connection?.Resize(cols, rows);
    }

    protected override void DisconnectCore()
    {
        var c = _connection;
        _connection = null;
        if (c is null) return;
        c.DataReceived -= OnData;
        c.Closed -= OnRemoteClosed;
        Task.Run(c.Dispose);
        Dispatcher.UIThread.Post(() =>
        {
            lock (Terminal.Sync) Terminal.Feed($"\r\n\e[2m{Localization.Loc.T("Session.EndedHint")}\e[0m\r\n");
        });
    }

    [RelayCommand]
    private void OpenSftp() => Owner?.Actions.OpenSftpFor(Entry);
}
