using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Nexode.App.Services;
using Nexode.App.ViewModels;

namespace Nexode.App.Views;

public partial class StartView : UserControl
{
    private StartTabViewModel? _vm;

    public StartView()
    {
        InitializeComponent();
        AttachedToVisualTree += (_, _) => Dispatcher.UIThread.Post(() => QuickBox.Focus(), DispatcherPriority.Background);
        // The view model is set locally by the tab template and stays when the page leaves the window:
        // listen only while the page is shown, so a page that is gone does not stay subscribed.
        DataContextChanged += (_, _) =>
        {
            Listen(false);
            _vm = DataContext as StartTabViewModel;
            Listen(this.IsAttachedToVisualTree());
            ApplyOrder();
        };
        AttachedToVisualTree += (_, _) => Listen(true);
        DetachedFromVisualTree += (_, _) => Listen(false);
    }

    private bool _listening;

    private void Listen(bool on)
    {
        if (_vm is null || on == _listening) return;
        if (on) _vm.LayoutChanged += ApplyOrder;
        else _vm.LayoutChanged -= ApplyOrder;
        _listening = on;
    }

    /// <summary>Puts the sections into the order of the home page settings (greeting stays on top).</summary>
    private void ApplyOrder()
    {
        var map = new Dictionary<string, Control>
        {
            ["search"] = SecSearch, ["profiles"] = SecProfiles, ["favorites"] = SecFavorites, ["status"] = SecStatus, ["dashboard"] = SecDashboard
        };
        var order = AppHost.Settings.HomeOrder();
        var pairDone = false;
        var wanted = new List<Control>();
        foreach (var id in order)
        {
            if (id is "recent" or "new")
            {
                if (pairDone) continue;
                pairDone = true;
                wanted.Add(SecRecentNew);
                continue;
            }

            if (map.TryGetValue(id, out var c)) wanted.Add(c);
        }

        // Recent and New share a row: the first in the order goes left, a hidden one leaves the row to the other.
        var recentFirst = order.ToList().IndexOf("recent") <= order.ToList().IndexOf("new");
        var both = _vm is { ShowRecent: true, ShowNew: true };
        Grid.SetColumn(SecRecent, both ? (recentFirst ? 0 : 2) : 0);
        Grid.SetColumn(SecNew, both ? (recentFirst ? 2 : 0) : 0);
        Grid.SetColumnSpan(SecRecent, both ? 1 : 3);
        Grid.SetColumnSpan(SecNew, both ? 1 : 3);

        var current = Sections.Children.Skip(1).ToList();
        if (current.SequenceEqual(wanted)) return;
        foreach (var c in wanted) Sections.Children.Remove(c);
        foreach (var c in wanted) Sections.Children.Add(c);
    }
}
