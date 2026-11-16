using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;

namespace Nexode.App.Controls;

/// <summary>Icon left of a text: content of buttons and menu items.</summary>
public sealed class IconLabel : StackPanel
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<IconLabel, string?>(nameof(Icon));
    public static readonly StyledProperty<string?> TextProperty = AvaloniaProperty.Register<IconLabel, string?>(nameof(Text));
    public static readonly StyledProperty<double> IconSizeProperty = AvaloniaProperty.Register<IconLabel, double>(nameof(IconSize), 16d);

    private readonly LineIcon _icon = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _text = new() { VerticalAlignment = VerticalAlignment.Center };

    public IconLabel()
    {
        Orientation = Orientation.Horizontal;
        Spacing = 8;
        _icon.IsVisible = false;
        _text.IsVisible = false;
        Children.Add(_icon);
        Children.Add(_text);
    }

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public string? Text { get => GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public double IconSize { get => GetValue(IconSizeProperty); set => SetValue(IconSizeProperty, value); }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == IconProperty)
        {
            _icon.Icon = Icon;
            _icon.IsVisible = !string.IsNullOrEmpty(Icon);
        }
        else if (change.Property == TextProperty)
        {
            _text.Text = Text;
            _text.IsVisible = !string.IsNullOrEmpty(Text);
        }
        else if (change.Property == IconSizeProperty)
        {
            _icon.Size = IconSize;
        }
    }
}
