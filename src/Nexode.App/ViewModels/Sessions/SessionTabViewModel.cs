using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.ViewModels.Sessions;

public enum SessionState
{
    Idle,
    Connecting,
    Connected,
    Disconnected,
    Failed
}

/// <summary>
/// A live connection in a tab. Moves between windows as it is (no reconnect), disconnects when the tab closes.
/// </summary>
public abstract partial class SessionTabViewModel : TabViewModel
{
    private readonly Guid _instance = Guid.NewGuid();
    private CancellationTokenSource? _connectCts;
    private CancellationTokenSource? _reconnectCts;
    private int _reconnectAttempt;
    private int _reconnectCountdown;
    private bool _everConnected;
    private SshTunnel? _tunnel;
    private HistoryRecord? _history;

    protected SessionTabViewModel(Guid entryId, IReadOnlyDictionary<string, string>? overrides)
    {
        EntryId = entryId;
        Overrides = overrides;
        UpdateHeader();
    }

    public Guid EntryId { get; }
    public IReadOnlyDictionary<string, string>? Overrides { get; }
    public Entry? Entry => AppHost.Workspace.Tree.Get(EntryId);
    public override string Key => $"session:{EntryId}:{_instance}";
    public override bool IsSession => true;
    public override bool CanSplit => Features.On(Features.Split);
    public override bool IsValid => true;
    public override TabViewModel ForTransfer() => this;

    [ObservableProperty]
    private SessionState _state;

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _error;

    public bool IsConnecting => State == SessionState.Connecting;
    public bool IsConnected => State == SessionState.Connected;
    public bool IsEnded => State is SessionState.Disconnected or SessionState.Failed;
    public bool HasError => !string.IsNullOrEmpty(Error);

    /// <summary>Split view this session is a pane of (null = own tab).</summary>
    public SplitTabViewModel? Split { get; set; }

    [ObservableProperty]
    private string? _reconnectText;

    public bool IsReconnecting => ReconnectText is not null;

    partial void OnReconnectTextChanged(string? value) => OnPropertyChanged(nameof(IsReconnecting));

    /// <summary>Automatic reconnect after a dropped connection (setting per entry, default from the settings).</summary>
    protected virtual bool AutoReconnectAllowed => Entry?.Setting(SettingKeys.AutoReconnect) switch
    {
        "0" => false,
        "1" => true,
        _ => AppHost.Settings.AutoReconnect
    };
    public string TargetText { get; private set; } = "";
    protected ConnectParams? Params { get; private set; }

    partial void OnStateChanged(SessionState value)
    {
        OnPropertyChanged(nameof(IsConnecting));
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsEnded));
        StatusText = value switch
        {
            SessionState.Connecting => Loc.F("Session.Connecting", TargetText),
            SessionState.Connected => Loc.F("Session.Connected", TargetText),
            SessionState.Disconnected => Loc.T("Session.Disconnected"),
            SessionState.Failed => Loc.T("Session.Failed"),
            _ => ""
        };
        Icon = value == SessionState.Connected ? EntryKinds.Icon(Entry?.Kind ?? EntryKind.Ssh) : value == SessionState.Failed ? "circle-alert" : "unplug";
        if (value == SessionState.Connected) Icon = Entry is { } e ? AppHost.Workspace.Resolver.Resolve(e).Icon : Icon;
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(HasError));

    private void UpdateHeader()
    {
        if (Entry is not { } e) return;
        Title = string.IsNullOrEmpty(e.Name) ? Loc.T("Unnamed") : e.Name;
        var eff = AppHost.Workspace.Resolver.Resolve(e);
        SetColors(eff);
        IconBrush = eff.ColorHue is { } h && e.ColorHue is not null ? AppHost.Theme.FolderBrush(h) : null;
        if (State == SessionState.Idle) Icon = eff.Icon;
    }

    public override void Refresh() => UpdateHeader();

    /// <summary>Resolves credentials (prompts on the UI thread) and connects.</summary>
    public async Task StartAsync()
    {
        if (Entry is not { } entry || Owner?.Window is not { } window) return;
        Error = null;
        State = SessionState.Connecting;
        try
        {
            Params = await ConnectionBuilder.BuildAsync(entry, window, Owner.Actions, Overrides);
            if (Params is null)
            {
                State = SessionState.Disconnected;
                return;
            }

            TargetText = entry.Kind == EntryKind.Serial ? Params.Host
                : string.IsNullOrEmpty(Params.Username) ? $"{Params.Host}:{Params.Port}" : $"{Params.Username}@{Params.Host}:{Params.Port}";
            OnStateChanged(State);
            _connectCts?.Cancel();
            _connectCts = new CancellationTokenSource();
            var prompts = new SessionPrompts(() => Owner?.Window);
            CloseTunnel();
            if (Tunnels.JumpHostOf(entry) is { } jump)
            {
                StatusText = Loc.F("Tunnel.Opening", jump.Name);
                (Params, _tunnel) = await Tunnels.ApplyAsync(entry, Params, window, Owner.Actions, prompts, _connectCts.Token);
                if (_tunnel is { } t)
                {
                    t.Closed += OnRemoteClosed;
                    TargetText += " · " + Loc.F("Tunnel.ViaName", jump.Name);
                    OnStateChanged(State);
                }
            }

            Tunnel = _tunnel;
            await Task.Run(() => ConnectCoreAsync(Params, prompts, _connectCts.Token));
            State = SessionState.Connected;
            ConnectionHistory.End(_history);
            _history = ConnectionHistory.Begin(entry, TargetText);
            _reconnectAttempt = 0;
            ReconnectText = null;
            // Usage counts once per session, not for automatic reconnects.
            if (!_everConnected) AppHost.Workspace.MarkUsed(EntryId);
            _everConnected = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Error = Describe(ex);
            State = SessionState.Failed;
            Teardown();
            if (_reconnectAttempt == 0 && ex is not OperationCanceledException) ConnectionHistory.Begin(entry, TargetText.Length > 0 ? TargetText : entry.Host ?? entry.Url ?? "", HistoryResult.Failed, Error);
            if (_reconnectAttempt > 0) ScheduleReconnect();
        }
    }

    protected virtual string Describe(Exception ex) => ex switch
    {
        Renci.SshNet.Common.SshAuthenticationException => Loc.T("Session.AuthFailed"),
        UnauthorizedAccessException => ex.Message,
        Renci.SshNet.Common.SshConnectionException { Message: var m } when m.Contains("Key exchange negotiation failed", StringComparison.Ordinal) => Loc.T("Session.HostKeyRejected"),
        System.Security.Authentication.AuthenticationException => Loc.T("Session.CertRejected"),
        _ => NetErrors.Describe(ex)
    };

    /// <summary>Background thread: connect the transport.</summary>
    protected abstract Task ConnectCoreAsync(ConnectParams p, ISessionPrompts prompts, CancellationToken ct);

    /// <summary>Releases the transport (any thread).</summary>
    protected abstract void DisconnectCore();

    /// <summary>Jump host tunnel of the current connection (null = direct).</summary>
    protected SshTunnel? Tunnel { get; private set; }

    private void Teardown()
    {
        DisconnectCore();
        CloseTunnel();
        ConnectionHistory.End(_history, State == SessionState.Failed ? Error : null);
        _history = null;
    }

    private void CloseTunnel()
    {
        var t = _tunnel;
        _tunnel = null;
        Tunnel = null;
        if (t is null) return;
        t.Closed -= OnRemoteClosed;
        Task.Run(t.Dispose);
    }

    /// <summary>Called by subclasses when the remote side closed the connection.</summary>
    protected void OnRemoteClosed(string? reason) => Dispatcher.UIThread.Post(() =>
    {
        if (State is SessionState.Disconnected or SessionState.Failed) return;
        var wasConnected = State == SessionState.Connected;
        Error = reason;
        State = reason is null ? SessionState.Disconnected : SessionState.Failed;
        Teardown();
        // A normal end (logoff, "exit") stays closed; a dropped connection is retried.
        if (reason is not null && wasConnected && AutoReconnectAllowed) ScheduleReconnect();
    });

    /// <summary>Next automatic attempt with growing delay (2, 5, 10, 30 s …) up to the configured number of attempts.</summary>
    private void ScheduleReconnect()
    {
        var max = Math.Max(0, AppHost.Settings.ReconnectAttempts);
        if (_reconnectAttempt >= max)
        {
            ReconnectText = null;
            _reconnectAttempt = 0;
            return;
        }

        _reconnectAttempt++;
        int[] delays = [2, 5, 10, 30];
        _reconnectCountdown = delays[Math.Min(_reconnectAttempt - 1, delays.Length - 1)];
        UpdateReconnectText();
        _reconnectCts?.Cancel();
        var cts = _reconnectCts = new CancellationTokenSource();
        _ = CountdownAsync(cts.Token);
    }

    private async Task CountdownAsync(CancellationToken ct)
    {
        try
        {
            while (_reconnectCountdown > 0)
            {
                await Task.Delay(1000, ct);
                _reconnectCountdown--;
                if (_reconnectCountdown > 0) UpdateReconnectText();
            }

            ReconnectText = Loc.F("Session.Reconnecting", _reconnectAttempt, AppHost.Settings.ReconnectAttempts);
            await StartAsync();
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void UpdateReconnectText() =>
        ReconnectText = Loc.F("Session.ReconnectIn", _reconnectAttempt, AppHost.Settings.ReconnectAttempts, _reconnectCountdown);

    [RelayCommand]
    private void CancelReconnect()
    {
        _reconnectCts?.Cancel();
        _reconnectAttempt = 0;
        ReconnectText = null;
    }

    [RelayCommand]
    private async Task Reconnect()
    {
        CancelReconnect();
        Teardown();
        await StartAsync();
    }

    [RelayCommand]
    private void Disconnect()
    {
        CancelReconnect();
        _connectCts?.Cancel();
        Teardown();
        State = SessionState.Disconnected;
    }

    /// <summary>Session in its own fullscreen window (monitor from the entry, else the current one); again = back to normal.</summary>
    [RelayCommand]
    private void Fullscreen()
    {
        if (Owner is { IsKiosk: true } kiosk)
        {
            kiosk.IsKiosk = false;
            return;
        }

        if (Split is not null || Owner is null) return;
        var monitor = int.TryParse(Entry?.Setting(SettingKeys.RdpMonitor), out var m) ? m : 0;
        Owner.Tabs.Detach(this);
        AppHost.Windows.OpenFullscreen(this, monitor);
    }

    [RelayCommand]
    private void CloseTab()
    {
        if (Split is { } split) split.Remove(this, close: true);
        else Owner?.Tabs.Close(this);
    }

    public override void OnClosed()
    {
        CancelReconnect();
        _connectCts?.Cancel();
        Teardown();
        if (!IsEnded) State = SessionState.Disconnected;
    }
}
