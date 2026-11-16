using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace Nexode.App.Controls;

/// <summary>Stroke icon on a 24×24 grid (Lucide style). Set <see cref="Icon"/> (resource key without "ic-") or <see cref="Data"/>.</summary>
public sealed class LineIcon : Control
{
    public static readonly StyledProperty<string?> IconProperty = AvaloniaProperty.Register<LineIcon, string?>(nameof(Icon));
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<LineIcon, Geometry?>(nameof(Data));
    public static readonly StyledProperty<IBrush?> ForegroundProperty = TextElement.ForegroundProperty.AddOwner<LineIcon>();
    public static readonly StyledProperty<IBrush?> TintProperty = AvaloniaProperty.Register<LineIcon, IBrush?>(nameof(Tint));
    public static readonly StyledProperty<double> StrokeThicknessProperty = AvaloniaProperty.Register<LineIcon, double>(nameof(StrokeThickness), 2d);
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<LineIcon, double>(nameof(Size), 16d);
    public static readonly StyledProperty<bool> FilledProperty = AvaloniaProperty.Register<LineIcon, bool>(nameof(Filled));

    static LineIcon()
    {
        AffectsRender<LineIcon>(IconProperty, DataProperty, ForegroundProperty, TintProperty, StrokeThicknessProperty, FilledProperty);
        AffectsMeasure<LineIcon>(SizeProperty);
    }

    public string? Icon { get => GetValue(IconProperty); set => SetValue(IconProperty, value); }
    public Geometry? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    public IBrush? Foreground { get => GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    /// <summary>Overrides <see cref="Foreground"/> when set (folder colors).</summary>
    public IBrush? Tint { get => GetValue(TintProperty); set => SetValue(TintProperty, value); }
    public double StrokeThickness { get => GetValue(StrokeThicknessProperty); set => SetValue(StrokeThicknessProperty, value); }
    public double Size { get => GetValue(SizeProperty); set => SetValue(SizeProperty, value); }
    /// <summary>Fills the shape as well (filled heart for favorites).</summary>
    public bool Filled { get => GetValue(FilledProperty); set => SetValue(FilledProperty, value); }

    protected override Size MeasureOverride(Size availableSize) => new(Size, Size);

    public override void Render(DrawingContext context)
    {
        var geometry = Data ?? Resolve(Icon);
        var brush = Tint ?? Foreground;
        if (geometry is null || brush is null) return;

        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (side <= 0) return;
        var scale = side / 24d;
        var offset = new Point((Bounds.Width - side) / 2, (Bounds.Height - side) / 2);
        using (context.PushTransform(Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(offset.X, offset.Y)))
        {
            context.DrawGeometry(Filled ? brush : null, new Pen(brush, StrokeThickness, lineCap: PenLineCap.Round, lineJoin: PenLineJoin.Round), geometry);
        }
    }

    public static Geometry? Resolve(string? key)
    {
        if (string.IsNullOrEmpty(key) || Application.Current is not { } app) return null;
        return app.TryGetResource("ic-" + key, null, out var value) && value is Geometry g ? g
            : app.TryGetResource("ic-circle-help", null, out var fallback) ? fallback as Geometry : null;
    }
}
