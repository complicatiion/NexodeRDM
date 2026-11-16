using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Protocols.LanSync;

namespace Nexode.App.ViewModels;

/// <summary>A computer to sync with: found in the LAN now, synced before, or both.</summary>
public sealed partial class LanPeerRow : ObservableObject
{
    public required string DeviceId { get; init; }
    public required string Name { get; init; }
    public required string Address { get; init; }
    public required int Port { get; init; }
    public bool IsOnline { get; init; }
    public bool IsKnown { get; init; }
    public string Detail { get; init; } = "";
    public string Icon => IsOnline ? "monitor-check" : "monitor";
}

public sealed record LanEventRow(string Time, string Icon, string Text, bool IsWarning);

/// <summary>LAN sync between own computers (optional feature "lansync").</summary>
public sealed partial class LanSyncTabViewModel : TabViewModel
{
    private CancellationTokenSource? _cts;
    private IReadOnlyList<LanSyncAnnouncement> _found = [];

    public LanSyncTabViewModel()
    {
        Icon = "arrow-left-right";
        Title = Loc.T("LanSync.Title");
        _deviceName = LanSyncService.DeviceName;
        _port = AppHost.Settings.LanSync.Port.ToString(CultureInfo.InvariantCulture);
        LanSyncService.Changed += OnServiceChanged;
        Loc.LanguageChanged += Relocalize;
        Refresh();
    }

    public override string Key => "lansync";
    public override TabViewModel Recreate() => new LanSyncTabViewModel();

    [ObservableProperty] private string _deviceName;
    [ObservableProperty] private string _port;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasPassword))] private string _password = "";
    [ObservableProperty] private string _manualAddress = "";
    [ObservableProperty] private bool _showPassword;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isSearching;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(IsIdle))] private bool _isSyncing;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasError))] private string _error = "";
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResult))] private string _result = "";

    public bool HasPassword => Password.Length > 0;
    public bool IsIdle => !IsSearching && !IsSyncing;
    public bool HasError => Error.Length > 0;
    public bool HasResult => Result.Length > 0;
    public bool IsListening => LanSyncService.IsListening;
    public bool IsNotListening => !IsListening;
    public string StatusText => IsListening ? Loc.F("LanSync.Listening", LanSyncService.Port) : Loc.T("LanSync.NotListening");
    public bool CanSync => !AppHost.Workspace.IsLocked && AppHost.Workspace.Shared is null;
    public string? BlockedText => AppHost.Workspace.Shared is not null ? Loc.T("LanSync.Refused.shared") : AppHost.Workspace.IsLocked ? Loc.T("LanSync.Refused.locked") : null;
    public bool IsBlocked => BlockedText is not null;
    public string SourceName => AppHost.Workspace.Info?.Name ?? "";

    public ObservableCollection<LanPeerRow> Peers { get; } = [];
    public bool HasPeers => Peers.Count > 0;
    public ObservableCollection<LanEventRow> Events { get; } = [];
    public bool HasEvents => Events.Count > 0;

    private void OnServiceChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Relocalize()
    {
        Title = Loc.T("LanSync.Title");
        Refresh();
    }

    public override void Refresh()
    {
        OnPropertyChanged(nameof(IsListening));
        OnPropertyChanged(nameof(IsNotListening));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(CanSync));
        OnPropertyChanged(nameof(BlockedText));
        OnPropertyChanged(nameof(IsBlocked));
        OnPropertyChanged(nameof(SourceName));
        RebuildPeers();

        Events.Clear();
        foreach (var e in LanSyncService.Events.Take(100))
        {
            var (icon, text, warn) = e.Kind switch
            {
                "listen" => ("radio", Loc.F("LanSync.EvListen", e.Detail), false),
                "stop" => ("square", Loc.T("LanSync.EvStop"), false),
                "synced" => ("check", Loc.F("LanSync.EvSynced", e.Peer, e.Detail), false),
                "refused" => ("ban", Loc.F("LanSync.EvRefused", e.Peer, e.Detail), true),
                "auth-failed" => ("shield-alert", Loc.F("LanSync.EvAuthFailed", e.Peer), true),
                "blocked" => ("shield-x", Loc.F("LanSync.EvBlocked", e.Peer), true),
                "stopped" => ("shield-x", Loc.T("LanSync.EvTooMany"), true),
                _ => ("info", $"{e.Peer} {e.Detail}".Trim(), true)
            };
            Events.Add(new LanEventRow(e.Utc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), icon, text, warn));
        }

        OnPropertyChanged(nameof(HasEvents));
    }

    private void RebuildPeers()
    {
        Peers.Clear();
        var own = AppHost.Settings.DeviceId;
        var known = AppHost.Settings.LanSync.Peers;
        foreach (var a in _found.Where(a => a.DeviceId != own))
        {
            var k = known.FirstOrDefault(p => p.DeviceId == a.DeviceId);
            Peers.Add(new LanPeerRow
            {
                DeviceId = a.DeviceId, Name = a.Name, Address = a.Address, Port = a.Port, IsOnline = true, IsKnown = k is not null,
                Detail = $"{a.Address}:{a.Port}" + (k?.LastSyncUtc is { } t ? " · " + Loc.F("LanSync.LastSync", t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "")
            });
        }

        foreach (var k in known.Where(k => Peers.All(p => p.DeviceId != k.DeviceId)).OrderByDescending(k => k.LastSyncUtc))
        {
            Peers.Add(new LanPeerRow
            {
                DeviceId = k.DeviceId, Name = k.Name, Address = k.Address, Port = k.Port, IsKnown = true,
                Detail = $"{k.Address}:{k.Port}" + (k.LastSyncUtc is { } t ? " · " + Loc.F("LanSync.LastSync", t.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)) : "")
            });
        }

        OnPropertyChanged(nameof(HasPeers));
    }

    private bool CheckPassword()
    {
        Error = "";
        if (Password.Length >= LanSyncService.MinPasswordLength) return true;
        Error = Loc.F("LanSync.PasswordTooShort", LanSyncService.MinPasswordLength);
        return false;
    }

    [RelayCommand]
    private void NewPassword()
    {
        Password = Views.PasswordGenerator.Create(20, true, true, true, false, true);
        ShowPassword = true;
    }

    [RelayCommand]
    private async Task Copy(string? text)
    {
        if (string.IsNullOrEmpty(text) || Owner?.Window?.Clipboard is not { } cb) return;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text);
        Owner?.ShowToast(Loc.T("Toast.Copied"));
    }

    [RelayCommand]
    private void StartListening()
    {
        if (!CheckPassword()) return;
        if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Error = Loc.T("FtpServer.BadPort");
            return;
        }

        SaveDevice(port);
        try
        {
            LanSyncService.Start(Password, port);
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Error = Loc.F("LanSync.StartFailed", ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private void StopListening()
    {
        LanSyncService.Stop();
        Refresh();
    }

    private void SaveDevice(int? port = null)
    {
        var s = AppHost.Settings;
        s.LanSync.DeviceName = string.IsNullOrWhiteSpace(DeviceName) || DeviceName.Trim() == Environment.MachineName ? null : DeviceName.Trim();
        if (port is { } p) s.LanSync.Port = p;
        s.Save();
    }

    [RelayCommand]
    private async Task Search()
    {
        Error = "";
        IsSearching = true;
        try
        {
            _found = await LanSyncService.DiscoverAsync(CancellationToken.None);
            RebuildPeers();
            if (_found.All(a => a.DeviceId == AppHost.Settings.DeviceId)) Result = Loc.T("LanSync.NoneFound");
            else Result = "";
        }
        catch (System.Net.Sockets.SocketException ex)
        {
            Error = ex.Message;
        }
        finally
        {
            IsSearching = false;
        }
    }

    [RelayCommand]
    private Task SyncPeer(LanPeerRow? peer) => peer is null ? Task.CompletedTask : SyncAsync(peer.Address, peer.Port);

    [RelayCommand]
    private Task SyncManual()
    {
        var text = ManualAddress.Trim();
        if (text.Length == 0)
        {
            Error = Loc.T("LanSync.NeedAddress");
            return Task.CompletedTask;
        }

        var port = LanSyncChannel.DefaultPort;
        // host, host:port, [v6]:port
        if (text.StartsWith('[') && text.IndexOf(']', StringComparison.Ordinal) is var end and > 0)
        {
            if (end + 2 < text.Length && text[end + 1] == ':' && int.TryParse(text[(end + 2)..], out var p6)) port = p6;
            text = text[1..end];
        }
        else if (text.Count(c => c == ':') == 1 && int.TryParse(text[(text.IndexOf(':', StringComparison.Ordinal) + 1)..], out var p))
        {
            port = p;
            text = text[..text.IndexOf(':', StringComparison.Ordinal)];
        }

        return SyncAsync(text, port);
    }

    [RelayCommand]
    private void ForgetPeer(LanPeerRow? peer)
    {
        if (peer is null) return;
        LanSyncService.ForgetPeer(peer.DeviceId);
        Refresh();
    }

    [RelayCommand]
    private void Cancel() => _cts?.Cancel();

    private async Task SyncAsync(string address, int port)
    {
        if (!CheckPassword() || IsSyncing) return;
        if (IsBlocked)
        {
            Error = BlockedText ?? "";
            return;
        }

        SaveDevice();
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        Result = "";
        IsSyncing = true;
        try
        {
            var o = await Task.Run(() => LanSyncService.SyncWithAsync(address, port, Password, _cts.Token));
            Result = Loc.F("LanSync.Done", o.PeerName, o.Added, o.Updated, o.Deleted);
            Owner?.ShowToast(Result);
        }
        catch (OperationCanceledException)
        {
        }
        catch (LanSyncAuthException)
        {
            Error = Loc.T("LanSync.WrongPassword");
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException)
        {
            Error = Loc.F("LanSync.NoConnection", $"{address}:{port}", ex.Message);
        }
        catch (Exception ex) when (ex is InvalidOperationException or InvalidDataException or System.Text.Json.JsonException or System.Security.Cryptography.CryptographicException)
        {
            Error = ex.Message;
        }
        finally
        {
            IsSyncing = false;
            Refresh();
        }
    }

    public override void OnClosed()
    {
        _cts?.Cancel();
        LanSyncService.Changed -= OnServiceChanged;
        Loc.LanguageChanged -= Relocalize;
    }
}
