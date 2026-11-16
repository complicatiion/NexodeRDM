using Avalonia.Controls;

namespace Nexode.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => AboutPage.DataContext = (DataContext as ViewModels.SettingsTabViewModel)?.About;
    }
}
