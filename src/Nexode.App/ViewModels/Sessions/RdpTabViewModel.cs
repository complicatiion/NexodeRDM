using System.Runtime.Versioning;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Nexode.App.Localization;
using Nexode.App.Sessions;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.ViewModels.Sessions;

/// <summary>Embedded RDP session (Windows). The ActiveX window is owned here and parked while the tab is hidden.</summary>
[SupportedOSPlatform("windows")]
public sealed partial class RdpTabViewModel : SessionTabViewModel
{
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private TaskCompletionSource<bool>? _connected;
    private bool _wasConnected;

    public RdpTabViewModel(Guid entryId, IReadOnlyDictionary<string, string>? overrides = null) : base(entryId, overrides)
    {
        _poll.Tick += (_, _) => Poll();
    }

    internal RdpActiveX? Control { get; private set; }
    public override TabViewModel Recreate() => new RdpTabViewModel(EntryId, Overrides);

    /// <summary>Size of the host area in physical pixels (set by the view).</summary>
    public (int Width, int Height) HostSize { get; set; } = (1280, 800);

    public bool IsDynamicResolution => Entry?.Setting(SettingKeys.RdpResolution) is null or "fit";

    [ObservableProperty]
    private string _clientText = "";

    /// <summary>The view creates the control inside its native host before connecting.</summary>
    internal RdpActiveX EnsureControl(nint parent)
    {
        if (Control is { IsCreated: true } existing)
        {
            existing.Reparent(parent);
            return existing;
        }

        var control = new RdpActiveX();
        control.Create(parent, HostSize.Width, HostSize.Height);
        control.Connected += () => Dispatcher.UIThread.Post(() => _connected?.TrySetResult(true));
        control.Disconnected += reason => Dispatcher.UIThread.Post(() => OnDisconnected(reason));
        Control = control;
        ClientText = control.ClassId ?? "";
        if (State == SessionState.Idle) Dispatcher.UIThread.Post(() => _ = StartAsync(), DispatcherPriority.Background);
        return control;
    }

    protected override async Task ConnectCoreAsync(ConnectParams p, ISessionPrompts prompts, CancellationToken ct)
    {
        _connected = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var control = Control ?? throw new InvalidOperationException(Loc.T("Rdp.NoControl"));
            var (w, h) = HostSize;
            var smart = false;
            if (p.Setting(SettingKeys.RdpResolution) is { } res && res.Split('x') is [var rw, var rh] && int.TryParse(rw, out var fw) && int.TryParse(rh, out var fh))
            {
                (w, h) = (fw, fh);
                smart = p.Setting(SettingKeys.RdpSmartSizing) != "0";
            }

            control.Configure(new RdpActiveX.Options(p.Host, p.Port, p.Username, p.Domain, p.Password, Math.Clamp(w, 640, 8192), Math.Clamp(h, 480, 8192),
                int.TryParse(p.Setting(SettingKeys.RdpColorDepth), out var depth) ? depth : 32, smart, p.Flag(SettingKeys.RdpAdmin),
                p.Setting(SettingKeys.RdpClipboard) != "0", p.Flag(SettingKeys.RdpDrives), p.Flag(SettingKeys.RdpPrinters),
                int.TryParse(p.Setting(SettingKeys.RdpAudio), out var audio) ? audio : 0, p.Setting(SettingKeys.RdpGateway),
                p.Flag(SettingKeys.RdpSmartCards), p.Flag(SettingKeys.RdpGatewayBypass), p.Setting(SettingKeys.RdpGatewaySameCreds) != "0",
                p.Setting(SettingKeys.RdpHyperVId) is { Length: > 0 } vm ? vm : null));
            _wasConnected = false;
            control.Connect();
            _poll.Start();
        });

        // The RDP handshake (NLA, certificate prompt of the control) can take a while.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        await using (timeout.Token.Register(() => _connected.TrySetCanceled()))
        {
            await _connected.Task;
        }
    }

    private void Poll()
    {
        if (Control is not { } c) return;
        var state = c.ConnectedState;
        if (state == 1)
        {
            _wasConnected = true;
            _connected?.TrySetResult(true);
        }
        else if (state == 0 && _wasConnected && State == SessionState.Connected)
        {
            OnDisconnected(0);
        }
    }

    private void OnDisconnected(int reason)
    {
        _poll.Stop();
        var text = Control?.DescribeDisconnect(reason) ?? "";
        if (_connected is { Task.IsCompleted: false })
        {
            _connected.TrySetException(new IOException(string.IsNullOrEmpty(text) ? Loc.T("Rdp.Failed") : text));
            return;
        }

        if (State == SessionState.Connected) OnRemoteClosed(string.IsNullOrEmpty(text) ? null : text);
    }

    public void ReportHostError(string message)
    {
        Error = message;
        State = SessionState.Failed;
    }

    public void OnHostResized(int width, int height)
    {
        HostSize = (width, height);
        Control?.Resize(width, height);
        if (IsConnected && IsDynamicResolution) Control?.UpdateResolution(width, height);
    }

    protected override void DisconnectCore()
    {
        _poll.Stop();
        Control?.Disconnect();
    }

    public override void OnClosed()
    {
        base.OnClosed();
        Control?.Dispose();
        Control = null;
    }
}
