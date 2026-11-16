using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nexode.App.Services;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Views.Sessions;

/// <summary>
/// Hosts the system web view of a web panel: embedded (NativeWebView) or as its own window (NativeWebDialog).
/// The web view is created when the panel is connected and recreated when the tab moves to another window.
/// </summary>
public partial class WebSessionView : UserControl
{
    private WebTabViewModel? _vm;
    private NativeWebView? _view;
    private NativeWebDialog? _dialog;
    private DispatcherTimer? _watchdog;
    private bool _adapterReady;

    public WebSessionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Detach();
            _vm = DataContext as WebTabViewModel;
            if (_vm is null) return;
            _vm.HostChanged += Rebuild;
            _vm.CommandRequested += OnCommand;
            if (_vm.State == SessionState.Idle) Dispatcher.UIThread.Post(() => _ = _vm.StartAsync(), DispatcherPriority.Background);
            else Rebuild();
        };
        AttachedToVisualTree += (_, _) => Rebuild();
        DetachedFromVisualTree += (_, _) => DestroyView(keepDialog: true);
    }

    private void Detach()
    {
        if (_vm is null) return;
        _vm.HostChanged -= Rebuild;
        _vm.CommandRequested -= OnCommand;
        DestroyView(keepDialog: false);
        _vm = null;
    }

    private Uri? Target => _vm?.CurrentUri ?? _vm?.StartUri;

    /// <summary>Creates the web view for the current state (nothing while connecting or ended).</summary>
    private void Rebuild()
    {
        if (_vm is not { IsConnected: true } vm || Target is null || VisualRoot is null)
        {
            DestroyView(keepDialog: _vm?.IsConnected == true);
            DialogCard.IsVisible = false;
            return;
        }

        if (vm.Mode == WebMode.Dialog)
        {
            DestroyView(keepDialog: true);
            DialogCard.IsVisible = true;
            if (_dialog is null) OpenDialog(vm);
            return;
        }

        DialogCard.IsVisible = false;
        if (_view is not null) return;
        try
        {
            var view = new NativeWebView();
            Wire(view, vm);
            _adapterReady = false;
            view.AdapterCreated += (_, _) =>
            {
                _adapterReady = true;
                ApplyZoom();
            };
            Host.Children.Add(view);
            _view = view;
            view.Source = Target;
            // No web view after a while: the embedding failed (e.g. no X11) – fall back.
            _watchdog?.Stop();
            _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
            _watchdog.Tick += (_, _) =>
            {
                _watchdog?.Stop();
                if (!_adapterReady && _view == view && _vm == vm)
                {
                    DestroyView(keepDialog: false);
                    vm.Fallback("timeout");
                }
            };
            _watchdog.Start();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            DestroyView(keepDialog: false);
            vm.Fallback(ex.Message);
        }
    }

    private void OpenDialog(WebTabViewModel vm)
    {
        try
        {
            var dialog = new NativeWebDialog { Title = vm.Title, CanUserResize = true };
            Wire(dialog, vm);
            dialog.Closing += (_, _) =>
            {
                if (_dialog != dialog) return;
                _dialog = null;
                vm.DialogOpen = false;
                if (vm.IsConnected) vm.DisconnectCommand.Execute(null);
            };
            _dialog = dialog;
            dialog.Show(TopLevel.GetTopLevel(this)!);
            if (TopLevel.GetTopLevel(this) is Window w)
            {
                var scale = w.RenderScaling;
                dialog.Resize((int)(Math.Max(900, w.Bounds.Width * 0.8) * scale), (int)(Math.Max(640, w.Bounds.Height * 0.8) * scale));
            }

            dialog.Navigate(Target!);
            vm.DialogOpen = true;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _dialog = null;
            vm.Mode = WebMode.External;
            vm.Fallback(ex.Message);
        }
    }

    private void Wire(object web, WebTabViewModel vm)
    {
        void Env(object? _, WebViewEnvironmentRequestedEventArgs e) => WebPanels.Configure(e, vm.ProfileFolder, vm.IgnoreCertificates, vm.ProxyPort);

        void Starting(object? _, WebViewNavigationStartingEventArgs e) => vm.OnNavigating(e.Request);

        async void Completed(object? _, WebViewNavigationCompletedEventArgs e)
        {
            var (back, fwd) = web switch
            {
                NativeWebView v => (v.CanGoBack, v.CanGoForward),
                NativeWebDialog d => (d.CanGoBack, d.CanGoForward),
                _ => (false, false)
            };
            vm.OnNavigated(e.Request, e.IsSuccess, back, fwd);
            if (e.IsSuccess && vm.FillScriptFor(e.Request) is { } script)
            {
                try
                {
                    await Invoke(web, script);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    AppLog.Write("form fill: " + ex.GetType().Name);
                }
            }

            ApplyZoom();
        }

        void Resource(object? _, WebResourceRequestedEventArgs e)
        {
            if (vm.AuthorizationFor(e.Request.Uri) is { } auth) e.Request.Headers.TrySet("Authorization", auth);
        }

        void NewWindow(object? _, WebViewNewWindowRequestedEventArgs e)
        {
            // Links with target=_blank stay in the panel.
            if (e.Request is null) return;
            e.Handled = true;
            Navigate(web, e.Request);
        }

        switch (web)
        {
            case NativeWebView v:
                v.EnvironmentRequested += Env;
                v.NavigationStarted += Starting;
                v.NavigationCompleted += Completed;
                v.WebResourceRequested += Resource;
                v.NewWindowRequested += NewWindow;
                break;
            case NativeWebDialog d:
                d.EnvironmentRequested += Env;
                d.NavigationStarted += Starting;
                d.NavigationCompleted += Completed;
                d.WebResourceRequested += Resource;
                d.NewWindowRequested += NewWindow;
                break;
        }
    }

    private static Task<string?> Invoke(object web, string script) => web switch
    {
        NativeWebView v => v.InvokeScript(script)!,
        NativeWebDialog d => d.InvokeScript(script)!,
        _ => Task.FromResult<string?>(null)
    };

    private static void Navigate(object web, Uri uri)
    {
        if (web is NativeWebView v) v.Navigate(uri);
        else if (web is NativeWebDialog d) d.Navigate(uri);
    }

    private object? Web => (object?)_view ?? _dialog;

    private void ApplyZoom()
    {
        if (Web is not { } web || _vm is null) return;
        var factor = (_vm.Zoom / 100.0).ToString("0.##", CultureInfo.InvariantCulture);
        _ = SafeInvoke(web, $"document.documentElement.style.zoom = '{factor}';");
    }

    private static async Task SafeInvoke(object web, string script)
    {
        try
        {
            await Invoke(web, script);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    private void OnCommand(WebCommand command, Uri? uri)
    {
        if (command == WebCommand.CloseWindow)
        {
            DestroyView(keepDialog: false);
            return;
        }

        if (command == WebCommand.Focus)
        {
            if (_dialog is { } dlg) dlg.Focus();
            else if (_vm is { IsConnected: true }) Rebuild();
            return;
        }

        if (Web is not { } web) return;
        switch (command)
        {
            case WebCommand.Back when web is NativeWebView v: v.GoBack(); break;
            case WebCommand.Back when web is NativeWebDialog d: d.GoBack(); break;
            case WebCommand.Forward when web is NativeWebView v: v.GoForward(); break;
            case WebCommand.Forward when web is NativeWebDialog d: d.GoForward(); break;
            case WebCommand.Reload when web is NativeWebView v: v.Refresh(); break;
            case WebCommand.Reload when web is NativeWebDialog d: d.Refresh(); break;
            case WebCommand.Stop when web is NativeWebView v: v.Stop(); break;
            case WebCommand.Stop when web is NativeWebDialog d: d.Stop(); break;
            case WebCommand.Navigate when uri is not null: Navigate(web, uri); break;
            case WebCommand.Zoom: ApplyZoom(); break;
        }
    }

    private void ShowWindowClick(object? sender, RoutedEventArgs e)
    {
        if (_dialog is { } d) d.Focus();
        else if (_vm is { IsConnected: true } vm) OpenDialog(vm);
    }

    private void DestroyView(bool keepDialog)
    {
        _watchdog?.Stop();
        if (_view is { } v)
        {
            _view = null;
            Host.Children.Remove(v);
        }

        if (!keepDialog && _dialog is { } d)
        {
            _dialog = null;
            try
            {
                d.Close();
                d.Dispose();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
            }
        }
    }
}
