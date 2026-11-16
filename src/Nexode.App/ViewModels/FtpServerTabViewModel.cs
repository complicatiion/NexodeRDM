using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Protocols.Server;

namespace Nexode.App.ViewModels;

public sealed record FtpEventRow(string Time, string Client, string Icon, string Text, bool IsWarning);

public sealed record FtpAddressRow(string Url);

/// <summary>Local FTP server: share one folder in the LAN (optional feature).</summary>
public sealed partial class FtpServerTabViewModel : TabViewModel
{
    private FtpServerSettings S => AppHost.Settings.FtpServer;

    public FtpServerTabViewModel()
    {
        Icon = "server-cog";
        Title = Loc.T("FtpServer.Title");
        _folder = S.Folder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        _port = S.Port.ToString(CultureInfo.InvariantCulture);
        _user = S.User;
        _anonymous = S.Anonymous;
        _readOnly = S.ReadOnly;
        _tls = S.Tls;
        _passive = $"{S.PassiveFrom}-{S.PassiveTo}";
        _password = Views.PasswordGenerator.Create(14, true, true, true, false, true);
        FtpServerService.Changed += OnServerChanged;
        Loc.LanguageChanged += Relocalize;
        Refresh();
    }

    public override string Key => "ftpserver";
    public override TabViewModel Recreate() => new FtpServerTabViewModel();

    [ObservableProperty] private string _folder;
    [ObservableProperty] private string _port;
    [ObservableProperty] private string _user;
    [ObservableProperty] private string _password;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(UsesLogin))] private bool _anonymous;
    [ObservableProperty] private bool _readOnly;
    [ObservableProperty] private bool _tls;
    [ObservableProperty] private string _passive;
    [ObservableProperty] private string _error = "";

    public bool UsesLogin => !Anonymous;
    public bool IsRunning => FtpServerService.IsRunning;
    public bool IsStopped => !IsRunning;
    public string StatusText => IsRunning ? Loc.F("FtpServer.Running", FtpServerService.Clients) : Loc.T("FtpServer.Stopped");
    public string? Fingerprint => FtpServerService.Fingerprint;
    public bool HasFingerprint => IsRunning && Fingerprint is not null;
    public bool HasError => Error.Length > 0;
    public ObservableCollection<FtpAddressRow> Addresses { get; } = [];
    public ObservableCollection<FtpEventRow> Events { get; } = [];
    public bool HasEvents => Events.Count > 0;

    partial void OnErrorChanged(string value) => OnPropertyChanged(nameof(HasError));

    private void OnServerChanged() => Dispatcher.UIThread.Post(Refresh);

    private void Relocalize()
    {
        Title = Loc.T("FtpServer.Title");
        Refresh();
    }

    public override void Refresh()
    {
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsStopped));
        OnPropertyChanged(nameof(StatusText));
        OnPropertyChanged(nameof(Fingerprint));
        OnPropertyChanged(nameof(HasFingerprint));
        Addresses.Clear();
        if (IsRunning)
        {
            var scheme = FtpServerService.Tls ? "ftpes" : "ftp";
            foreach (var ip in LocalFtpServer.LocalAddresses()) Addresses.Add(new FtpAddressRow($"{scheme}://{ip}:{FtpServerService.Port}"));
        }

        Events.Clear();
        foreach (var e in FtpServerService.Events.Take(200))
        {
            var (icon, text, warn) = e.Kind switch
            {
                "start" => ("play", Loc.F("FtpServer.EvStart", e.Detail), false),
                "stop" => ("square", Loc.T("FtpServer.EvStop"), false),
                "connect" => ("plug", Loc.T("FtpServer.EvConnect"), false),
                "disconnect" => ("unplug", Loc.T("FtpServer.EvDisconnect"), false),
                "login" => ("log-in", Loc.F("FtpServer.EvLogin", e.Detail), false),
                "login-failed" => ("shield-alert", Loc.F("FtpServer.EvLoginFailed", e.Detail), true),
                "download" => ("download", e.Detail, false),
                "upload" => ("upload", e.Detail, false),
                "delete" => ("trash-2", e.Detail, true),
                _ => ("info", e.Kind + " " + e.Detail, false)
            };
            Events.Add(new FtpEventRow(e.Utc.ToLocalTime().ToString("T", CultureInfo.CurrentCulture), e.Client, icon, text, warn));
        }

        OnPropertyChanged(nameof(HasEvents));
    }

    [RelayCommand]
    private async Task Browse()
    {
        if (Owner?.Window is not { } w) return;
        var start = Directory.Exists(Folder) ? await w.StorageProvider.TryGetFolderFromPathAsync(Folder) : null;
        var picked = await w.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = Loc.T("FtpServer.Folder"), SuggestedStartLocation = start });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is { } path) Folder = path;
    }

    [RelayCommand]
    private void NewPassword() => Password = Views.PasswordGenerator.Create(14, true, true, true, false, true);

    [RelayCommand]
    private async Task Copy(string? text)
    {
        if (string.IsNullOrEmpty(text) || Owner?.Window?.Clipboard is not { } cb) return;
        await Avalonia.Input.Platform.ClipboardExtensions.SetTextAsync(cb, text);
        Owner?.ShowToast(Loc.T("FtpServer.Copied"));
    }

    [RelayCommand]
    private void Start()
    {
        Error = "";
        if (!Directory.Exists(Folder))
        {
            Error = Loc.T("FtpServer.NoFolder");
            return;
        }

        if (!int.TryParse(Port, NumberStyles.None, CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
        {
            Error = Loc.T("FtpServer.BadPort");
            return;
        }

        var range = Passive.Split('-', StringSplitOptions.TrimEntries);
        var from = range.Length == 2 && int.TryParse(range[0], out var f) ? f : 0;
        var to = range.Length == 2 && int.TryParse(range[1], out var t) ? t : 0;
        if (from is < 0 or > 65535 || to is < 0 or > 65535 || to < from)
        {
            Error = Loc.T("FtpServer.BadPassive");
            return;
        }

        if (!Anonymous && (string.IsNullOrWhiteSpace(User) || Password.Length < 6))
        {
            Error = Loc.T("FtpServer.NeedLogin");
            return;
        }

        var s = S;
        s.Folder = Folder;
        s.Port = port;
        s.User = User.Trim();
        s.Anonymous = Anonymous;
        s.ReadOnly = ReadOnly;
        s.Tls = Tls;
        s.PassiveFrom = from;
        s.PassiveTo = to;
        AppHost.Settings.Save();
        try
        {
            FtpServerService.Start(s, Anonymous ? null : Password);
        }
        catch (Exception ex) when (ex is System.Net.Sockets.SocketException or IOException or UnauthorizedAccessException or InvalidOperationException or System.Security.Cryptography.CryptographicException)
        {
            Error = Loc.F("FtpServer.StartFailed", ex.Message);
        }

        Refresh();
    }

    [RelayCommand]
    private void Stop()
    {
        FtpServerService.Stop();
        Refresh();
    }

    public override void OnClosed()
    {
        FtpServerService.Changed -= OnServerChanged;
        Loc.LanguageChanged -= Relocalize;
    }
}
