using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Nexode.App.Controls;
using Nexode.App.Localization;
using Nexode.App.Services;

namespace Nexode.App.Views;

/// <summary>Icon picker: searchable, grouped. Returns the icon key, "" for automatic, null on cancel.</summary>
public sealed class IconPickerWindow : Window
{
    public static readonly IReadOnlyList<(string Group, string[] Icons)> Groups =
    [
        ("Icons.Protocols", ["monitor", "square-terminal", "terminal", "folder-lock", "folder-up", "screen-share", "globe", "square-chevron-right",
            "database", "key-round", "notebook-pen", "plug", "plug-zap", "link", "cable"]),
        ("Icons.Systems", ["os-windows", "os-linux", "os-macos", "server", "laptop", "monitor-smartphone", "smartphone", "tv", "container", "box", "layers", "cloud"]),
        ("Icons.Devices", ["router", "network", "wifi", "ethernet-port", "brick-wall", "shield", "shield-check", "hard-drive", "hard-drive-download",
            "cpu", "microchip", "memory-stick", "printer", "cctv", "webcam", "phone", "radio-tower", "satellite-dish", "gamepad-2"]),
        ("Icons.General", ["folder", "folder-open", "folder-tree", "house", "building-2", "factory", "warehouse", "users", "user", "user-round-cog", "mail",
            "tag", "tags", "flag", "star", "heart", "rocket", "sparkles", "bot", "code", "braces", "git-branch", "workflow", "archive", "package",
            "file-json", "file-key", "fingerprint", "badge-check", "lock", "power", "clock", "history", "settings", "palette", "list-tree"])
    ];

    private readonly WrapPanel _panel = new() { ItemSpacing = 6, LineSpacing = 6 };
    private readonly string? _current;

    public IconPickerWindow() : this(null)
    {
    }

    public IconPickerWindow(string? current)
    {
        _current = current;
        Title = Loc.T("Icons.Title");
        Width = 560;
        Height = 520;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Nexode/Assets/nexode.ico")));
        this[!BackgroundProperty] = new Avalonia.Markup.Xaml.MarkupExtensions.DynamicResourceExtension("B.BgContent");

        var search = new TextBox { PlaceholderText = Loc.T("Icons.Search"), Classes = { "search" }, Margin = new Thickness(0, 0, 0, 12) };
        search.InnerLeftContent = new LineIcon { Icon = "search", Size = 15, Margin = new Thickness(8, 0, 0, 0) };
        search.TextChanged += (_, _) => Fill(search.Text);

        var auto = new Button { Content = new IconLabel { Icon = "rotate-ccw", Text = Loc.T("Editor.AutoIcon") } };
        auto.Click += (_, _) => Close("");
        var cancel = new Button { Content = new IconLabel { Icon = "x", Text = Loc.T("Cancel") }, IsCancel = true };
        cancel.Click += (_, _) => Close(null);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 12, 0, 0) };
        buttons.Children.Add(auto);
        buttons.Children.Add(cancel);

        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(search, Dock.Top);
        DockPanel.SetDock(buttons, Dock.Bottom);
        root.Children.Add(search);
        root.Children.Add(buttons);
        root.Children.Add(new ScrollViewer { Content = _panel });
        Content = root;
        Fill(null);
        Opened += (_, _) =>
        {
            WindowChrome.Apply(this);
            search.Focus();
        };
    }

    private void Fill(string? filter)
    {
        _panel.Children.Clear();
        foreach (var (group, icons) in Groups)
        {
            var matches = icons.Where(i => string.IsNullOrWhiteSpace(filter) || i.Contains(filter.Trim(), StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count == 0) continue;
            _panel.Children.Add(new TextBlock { Text = Loc.T(group).ToUpperInvariant(), Classes = { "section" }, Width = 500, Margin = new Thickness(0, 8, 0, 2) });
            foreach (var icon in matches)
            {
                var b = new Button
                {
                    Width = 44,
                    Height = 44,
                    Padding = new Thickness(0),
                    HorizontalContentAlignment = HorizontalAlignment.Center,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Content = new LineIcon { Icon = icon, Size = 20 }
                };
                if (icon == _current) b.Classes.Add("primary");
                ToolTip.SetTip(b, icon);
                b.Click += (_, _) => Close(icon);
                _panel.Children.Add(b);
            }
        }
    }
}
