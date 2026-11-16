using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Views.Sessions;

public partial class VncSessionView : UserControl
{
    private VncTabViewModel? _vm;

    public VncSessionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (_vm is not null) _vm.ClientChanged -= Bind;
            _vm = DataContext as VncTabViewModel;
            if (_vm is null) return;
            _vm.ClientChanged += Bind;
            Bind();
            if (_vm.State == SessionState.Idle) Dispatcher.UIThread.Post(() => _ = _vm.StartAsync(), DispatcherPriority.Background);
        };
        DetachedFromVisualTree += (_, _) => Screen.Client = null;
        AttachedToVisualTree += (_, _) => Bind();
    }

    private void Bind()
    {
        Screen.Client = _vm?.Client;
        if (_vm?.Client is not null) Screen.Focus();
    }

    private async void SendClipboard(object? sender, RoutedEventArgs e)
    {
        if (_vm?.Client is { } c && TopLevel.GetTopLevel(this)?.Clipboard is { } cb && await cb.TryGetTextAsync() is { } text) c.SendClipboard(text);
    }
}
