using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Views.Sessions;

public partial class TerminalSessionView : UserControl
{
    private TerminalTabViewModel? _vm;

    public TerminalSessionView()
    {
        InitializeComponent();
        Term.Input += data => _vm?.SendInput(data);
        Term.Typed += data => _vm?.Typed(data);
        Term.GridResized += (c, r) => _vm?.OnResized(c, r);
        DataContextChanged += (_, _) =>
        {
            _vm = DataContext as TerminalTabViewModel;
            if (_vm is null) return;
            if (_vm.State == SessionState.Idle) Dispatcher.UIThread.Post(() => _ = _vm.StartAsync(), DispatcherPriority.Background);
        };
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => Term.Focus(), DispatcherPriority.Background);
    }

    private async void CopyClick(object? sender, RoutedEventArgs e) => await Term.CopyAsync();
    private async void PasteClick(object? sender, RoutedEventArgs e)
    {
        await Term.PasteAsync();
        Term.Focus();
    }

    private void SelectAllClick(object? sender, RoutedEventArgs e) => Term.SelectAll();

    private void ClearClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        lock (_vm.Terminal.Sync) _vm.Terminal.Screen.ClearScrollback();
        Term.ScrollBy(-100000);
        Term.InvalidateVisual();
    }

    private void SmallerClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.FontSize = Math.Max(8, _vm.FontSize - 1);
    }

    private void LargerClick(object? sender, RoutedEventArgs e)
    {
        if (_vm is not null) _vm.FontSize = Math.Min(32, _vm.FontSize + 1);
    }
}
