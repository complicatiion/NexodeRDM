using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Protocols;
using Nexode.Protocols.Vnc;

namespace Nexode.App.ViewModels.Sessions;

/// <summary>VNC remote screen in a tab.</summary>
public sealed partial class VncTabViewModel : SessionTabViewModel
{
    public VncTabViewModel(Guid entryId, IReadOnlyDictionary<string, string>? overrides = null) : base(entryId, overrides)
    {
        _viewOnly = Entry?.Setting(SettingKeys.VncViewOnly) is "1" or "true";
        _scale = Entry?.Setting(SettingKeys.VncScale) != "0";
    }

    public RfbClient? Client { get; private set; }
    public override TabViewModel Recreate() => new VncTabViewModel(EntryId, Overrides);

    /// <summary>Raised (UI thread) when the client was replaced or the screen size changed.</summary>
    public event Action? ClientChanged;

    [ObservableProperty]
    private bool _viewOnly;

    [ObservableProperty]
    private bool _scale;

    [ObservableProperty]
    private string _desktopText = "";

    partial void OnViewOnlyChanged(bool value)
    {
        if (Client is { } c) c.ViewOnly = value;
    }

    protected override async Task ConnectCoreAsync(ConnectParams p, ISessionPrompts prompts, CancellationToken ct)
    {
        var client = new RfbClient(p, prompts, AppHost.KnownHosts, VncJpeg.Decode) { ViewOnly = ViewOnly };
        client.Closed += OnRemoteClosed;
        client.Resized += () => Dispatcher.UIThread.Post(() =>
        {
            DesktopText = $"{client.DesktopName} · {client.Width}×{client.Height}";
            ClientChanged?.Invoke();
        });
        await client.ConnectAsync(ct);
        Client = client;
        Dispatcher.UIThread.Post(() =>
        {
            DesktopText = $"{client.DesktopName} · {client.Width}×{client.Height} · {client.SecurityName}" + (client.IsEncrypted ? " (TLS)" : "");
            ClientChanged?.Invoke();
        });
    }

    protected override void DisconnectCore()
    {
        var c = Client;
        Client = null;
        if (c is null) return;
        c.Closed -= OnRemoteClosed;
        Task.Run(c.Dispose);
        Dispatcher.UIThread.Post(() => ClientChanged?.Invoke());
    }

    [RelayCommand]
    private void SendCtrlAltDel()
    {
        if (Client is not { } c) return;
        c.SendKey(KeySyms.ControlL, true);
        c.SendKey(KeySyms.AltL, true);
        c.SendKey(KeySyms.Delete, true);
        c.SendKey(KeySyms.Delete, false);
        c.SendKey(KeySyms.AltL, false);
        c.SendKey(KeySyms.ControlL, false);
    }

    [RelayCommand]
    private void ToggleScale() => Scale = !Scale;

    [RelayCommand]
    private void ToggleViewOnly() => ViewOnly = !ViewOnly;
}
