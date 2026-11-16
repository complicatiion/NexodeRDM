using Avalonia.Controls;
using Nexode.App.Sessions;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Views.Sessions;

public partial class RdpSessionView : UserControl
{
    public RdpSessionView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is RdpTabViewModel vm && OperatingSystem.IsWindows()) HostSlot.Content = new RdpHost(vm);
            else HostSlot.Content = null;
        };
    }
}
