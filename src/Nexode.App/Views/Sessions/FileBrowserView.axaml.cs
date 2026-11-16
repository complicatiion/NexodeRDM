using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Views.Sessions;

/// <summary>Local and remote pane side by side (thin adjustable divider), transfer queue below.</summary>
public partial class FileBrowserView : UserControl
{
    private FileBrowserTabViewModel? _vm;
    private double _queueHeight = 150;

    public FileBrowserView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            Listen(false);
            foreach (var old in Panes.Children.OfType<FilePaneView>().ToList()) Panes.Children.Remove(old);
            _vm = DataContext as FileBrowserTabViewModel;
            if (_vm is null) return;
            var local = new FilePaneView(_vm, _vm.Local);
            var remote = new FilePaneView(_vm, _vm.Remote);
            Grid.SetColumn(remote, 2);
            Panes.Children.Add(local);
            Panes.Children.Add(remote);
            Listen(this.IsAttachedToVisualTree());
            UpdateQueue();
            if (_vm.State == SessionState.Idle) Dispatcher.UIThread.Post(() => _ = _vm.StartAsync(), DispatcherPriority.Background);
        };
        // The tab template sets the view model locally (it stays when the page leaves): listen only while shown.
        AttachedToVisualTree += (_, _) =>
        {
            Listen(true);
            UpdateQueue();
        };
        DetachedFromVisualTree += (_, _) => Listen(false);
    }

    private bool _listening;

    private void Listen(bool on)
    {
        if (_vm is null || on == _listening) return;
        if (on) _vm.PropertyChanged += VmChanged;
        else _vm.PropertyChanged -= VmChanged;
        _listening = on;
    }

    private void VmChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(FileBrowserTabViewModel.HasTransfers)) UpdateQueue();
    }

    private void UpdateQueue()
    {
        var row = Body.RowDefinitions[2];
        if (_vm?.HasTransfers == true)
        {
            if (row.Height.Value < 1) row.Height = new GridLength(_queueHeight);
        }
        else
        {
            if (row.Height.Value >= 1) _queueHeight = Math.Max(80, row.Height.Value);
            row.Height = new GridLength(0);
        }
    }
}
