using System.Globalization;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Core.Model;
using Nexode.Protocols;

namespace Nexode.App.ViewModels.Sessions;

public enum WebCommand
{
    Back,
    Forward,
    Reload,
    Stop,
    Navigate,
    Zoom,
    Focus,
    CloseWindow
}

/// <summary>
/// Web panel in the built-in browser (system web view). "Connecting" resolves the URL, credentials for automatic
/// login and the jump host; the view then hosts the web view (in the tab or as its own window).
/// </summary>
public sealed partial class WebTabViewModel : SessionTabViewModel
{
    public WebTabViewModel(Guid entryId, WebMode mode = WebMode.Tab, IReadOnlyDictionary<string, string>? overrides = null) : base(entryId, overrides)
    {
        Mode = mode == WebMode.External ? WebMode.Tab : mode;
    }

    public override TabViewModel Recreate() => new WebTabViewModel(EntryId, Mode, Overrides);

    /// <summary>Raised on the UI thread for the view (navigation buttons, address bar).</summary>
    public event Action<WebCommand, Uri?>? CommandRequested;

    /// <summary>Raised when a new web view has to be created (connected, mode changed).</summary>
    public event Action? HostChanged;

    [ObservableProperty]
    private WebMode _mode;

    public bool IsDialog => Mode == WebMode.Dialog;
    partial void OnModeChanged(WebMode value)
    {
        OnPropertyChanged(nameof(IsDialog));
        HostChanged?.Invoke();
    }

    /// <summary>Address the panel starts with (after jump host rewriting).</summary>
    public Uri? StartUri { get; private set; }

    /// <summary>Current page (kept when the tab moves to another window).</summary>
    public Uri? CurrentUri { get; set; }

    [ObservableProperty]
    private string _address = "";

    [ObservableProperty]
    private bool _canGoBack;

    [ObservableProperty]
    private bool _canGoForward;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private int _zoom = 100;

    [ObservableProperty]
    private string? _pageError;

    [ObservableProperty]
    private bool _dialogOpen;

    public string ZoomText => Zoom.ToString(CultureInfo.InvariantCulture) + " %";
    partial void OnZoomChanged(int value)
    {
        OnPropertyChanged(nameof(ZoomText));
        CommandRequested?.Invoke(WebCommand.Zoom, null);
    }

    public bool HasPageError => PageError is not null;
    partial void OnPageErrorChanged(string? value) => OnPropertyChanged(nameof(HasPageError));

    /// <summary>Certificate checks are off for this panel (badge in the toolbar).</summary>
    public bool IgnoreCertificates { get; private set; }

    /// <summary>The web view shows the panel through a jump host.</summary>
    public string? TunnelText { get; private set; }
    public bool HasTunnel => TunnelText is not null;

    /// <summary>SOCKS port of the jump host (WebView2) – needs its own browser profile.</summary>
    public int? ProxyPort { get; private set; }

    public string ProfileFolder { get; private set; } = "";
    private string? _authHeader;
    private string? _authHost;
    private (string? User, string? Password, string? UserSel, string? PassSel)? _fill;
    private int _fillCount;

    protected override Task ConnectCoreAsync(ConnectParams p, ISessionPrompts prompts, CancellationToken ct)
    {
        var url = p.Setting(SettingKeys.WebUrl) ?? throw new InvalidOperationException(Loc.T("Toast.InvalidUrl"));
        var uri = new Uri(url);
        _authHost = uri.Authority;
        IgnoreCertificates = p.Flag(SettingKeys.WebIgnoreCert) && WebPanels.CanIgnoreCertificates;
        ProxyPort = null;
        TunnelText = null;
        if (Tunnel is { } tunnel)
        {
            if (WebPanels.CanUseProxy)
            {
                ProxyPort = tunnel.StartSocks();
            }
            else
            {
                // WebKit has no proxy setting: forward the port and open the local end.
                var local = tunnel.ForwardLocal(uri.Host, uri.Port);
                uri = new UriBuilder(uri) { Host = "127.0.0.1", Port = local }.Uri;
                _authHost = uri.Authority;
            }

            TunnelText = Loc.F("Tunnel.ViaName", tunnel.JumpHost);
        }

        ProfileFolder = ProxyPort is not null ? WebPanels.TemporaryProfile() : WebPanels.ProfileFolder(IgnoreCertificates);
        _authHeader = p.Flag(SettingKeys.WebBasicAuth) && !string.IsNullOrEmpty(p.Username)
            ? "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes((string.IsNullOrEmpty(p.Domain) ? p.Username : p.Domain + "\\" + p.Username) + ":" + p.Password))
            : null;
        _fill = p.Flag(SettingKeys.WebFormFill) ? (p.Username, p.Password, p.Setting(SettingKeys.WebUserSelector), p.Setting(SettingKeys.WebPassSelector)) : null;
        _fillCount = 0;
        StartUri = uri;
        Dispatcher.UIThread.Post(() =>
        {
            OnPropertyChanged(nameof(TunnelText));
            OnPropertyChanged(nameof(HasTunnel));
            OnPropertyChanged(nameof(IgnoreCertificates));
            Address = (CurrentUri ?? uri).AbsoluteUri;
            HostChanged?.Invoke();
        });
        return Task.CompletedTask;
    }

    protected override void DisconnectCore()
    {
        StartUri = null;
        Dispatcher.UIThread.Post(() =>
        {
            CommandRequested?.Invoke(WebCommand.CloseWindow, null);
            HostChanged?.Invoke();
        });
    }

    /// <summary>Authorization header for a request (only for the panel's own host).</summary>
    public string? AuthorizationFor(Uri? request) =>
        _authHeader is not null && request is not null && string.Equals(request.Authority, _authHost, StringComparison.OrdinalIgnoreCase) ? _authHeader : null;

    /// <summary>Login form script for the page just loaded (own host only, a few times per session).</summary>
    public string? FillScriptFor(Uri? page)
    {
        if (_fill is not { } f || page is null || _fillCount >= 5) return null;
        if (!string.Equals(page.Authority, _authHost, StringComparison.OrdinalIgnoreCase)) return null;
        _fillCount++;
        return WebPanels.FillScript(f.User, f.Password, f.UserSel, f.PassSel);
    }

    public void OnNavigated(Uri? uri, bool success, bool canBack, bool canForward)
    {
        IsLoading = false;
        CanGoBack = canBack;
        CanGoForward = canForward;
        if (uri is not null)
        {
            CurrentUri = uri;
            Address = uri.AbsoluteUri;
        }

        PageError = success ? null : Loc.T("Web.LoadFailed");
    }

    public void OnNavigating(Uri? uri)
    {
        IsLoading = true;
        PageError = null;
        if (uri is not null) Address = uri.AbsoluteUri;
    }

    [RelayCommand]
    private void Back() => CommandRequested?.Invoke(WebCommand.Back, null);

    [RelayCommand]
    private void Forward() => CommandRequested?.Invoke(WebCommand.Forward, null);

    [RelayCommand]
    private void ReloadPage() => CommandRequested?.Invoke(IsLoading ? WebCommand.Stop : WebCommand.Reload, null);

    [RelayCommand]
    private void Go()
    {
        var text = Address.Trim();
        if (text.Length == 0) return;
        if (!text.Contains("://", StringComparison.Ordinal)) text = "https://" + text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https") CommandRequested?.Invoke(WebCommand.Navigate, uri);
    }

    [RelayCommand]
    private void Home()
    {
        if (StartUri is { } u) CommandRequested?.Invoke(WebCommand.Navigate, u);
    }

    [RelayCommand]
    private void ZoomIn() => Zoom = Math.Min(300, Zoom + 10);

    [RelayCommand]
    private void ZoomOut() => Zoom = Math.Max(30, Zoom - 10);

    [RelayCommand]
    private void ZoomReset() => Zoom = 100;

    [RelayCommand]
    private void OpenExternal()
    {
        var target = CurrentUri ?? StartUri;
        // Through a jump host the local address only works inside Nexode: use the configured one.
        if (HasTunnel && Entry is { } e) target = Uri.TryCreate(AppHost.Workspace.Resolver.Resolve(e).Url, UriKind.Absolute, out var u) ? u : target;
        if (target is not null && Platform.Shell.OpenWebUrl(target.AbsoluteUri)) Owner?.ShowToast(Loc.F("Toast.OpenedBrowser", Title));
    }

    [RelayCommand]
    private void ShowWindow() => CommandRequested?.Invoke(WebCommand.Focus, null);

    /// <summary>The tab's web view could not be created: continue in its own window or the default browser.</summary>
    public void Fallback(string reason)
    {
        AppLog.Write("web panel fallback: " + reason);
        if (Mode == WebMode.Tab && WebPanels.CanDialog)
        {
            Mode = WebMode.Dialog;
            return;
        }

        OpenExternal();
        Error = Loc.T("Web.Unavailable") + (string.IsNullOrEmpty(reason) ? "" : " (" + reason + ")");
        State = SessionState.Failed;
    }

    public override void OnClosed()
    {
        base.OnClosed();
        CommandRequested?.Invoke(WebCommand.CloseWindow, null);
    }
}
