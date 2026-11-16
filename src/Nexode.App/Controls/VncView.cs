using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Nexode.Protocols.Vnc;

namespace Nexode.App.Controls;

/// <summary>Shows the framebuffer of an <see cref="RfbClient"/> and forwards mouse and keyboard as RFB events.</summary>
public sealed class VncView : Control
{
    public static readonly StyledProperty<bool> ScaleToFitProperty = AvaloniaProperty.Register<VncView, bool>(nameof(ScaleToFit), true);

    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(33) };
    private RfbClient? _client;
    private WriteableBitmap? _bitmap;
    private volatile bool _dirty = true;
    private int _buttons;
    private readonly HashSet<uint> _pressed = [];

    public VncView()
    {
        Focusable = true;
        ClipToBounds = true;
        _frame.Tick += (_, _) =>
        {
            if (!_dirty) return;
            _dirty = false;
            CopyFrame();
            InvalidateVisual();
        };
        AffectsRender<VncView>(ScaleToFitProperty);
    }

    public bool ScaleToFit { get => GetValue(ScaleToFitProperty); set => SetValue(ScaleToFitProperty, value); }

    public RfbClient? Client
    {
        get => _client;
        set
        {
            if (_client is not null) _client.FrameUpdated -= OnFrame;
            _client = value;
            if (_client is not null) _client.FrameUpdated += OnFrame;
            _bitmap?.Dispose();
            _bitmap = null;
            _dirty = true;
            InvalidateMeasure();
        }
    }

    private void OnFrame() => _dirty = true;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _frame.Start();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frame.Stop();
        ReleaseKeys();
    }

    protected override Size MeasureOverride(Size availableSize) =>
        ScaleToFit || _client is null ? new Size(0, 0) : new Size(_client.Width, _client.Height);

    private void CopyFrame()
    {
        if (_client is not { Width: > 0, Height: > 0 } c) return;
        if (_bitmap is null || _bitmap.PixelSize.Width != c.Width || _bitmap.PixelSize.Height != c.Height)
        {
            _bitmap?.Dispose();
            _bitmap = new WriteableBitmap(new PixelSize(c.Width, c.Height), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Opaque);
            InvalidateMeasure();
        }

        using var fb = _bitmap.Lock();
        lock (c.FrameLock)
        {
            if (c.Frame.Length != c.Width * c.Height) return;
            // uint[] and int[] share the runtime layout, so the frame can be copied row by row without allocations.
            var pixels = (int[])(object)c.Frame;
            for (var y = 0; y < c.Height; y++)
            {
                Marshal.Copy(pixels, y * c.Width, fb.Address + y * fb.RowBytes, c.Width);
            }
        }
    }

    private Rect ImageRect()
    {
        if (_client is not { Width: > 0, Height: > 0 } c) return default;
        if (!ScaleToFit) return new Rect(0, 0, c.Width, c.Height);
        var scale = Math.Min(Bounds.Width / c.Width, Bounds.Height / c.Height);
        var w = c.Width * scale;
        var h = c.Height * scale;
        return new Rect((Bounds.Width - w) / 2, (Bounds.Height - h) / 2, w, h);
    }

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(Brushes.Black, new Rect(Bounds.Size));
        if (_bitmap is null) return;
        var target = ImageRect();
        using (context.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.HighQuality }))
        {
            context.DrawImage(_bitmap, new Rect(_bitmap.Size), target);
        }
    }

    // ------------------------------------------------------------------ input

    private (int X, int Y) Map(Point p)
    {
        var r = ImageRect();
        if (_client is not { } c || r.Width <= 0) return (0, 0);
        return ((int)((p.X - r.X) * c.Width / r.Width), (int)((p.Y - r.Y) * c.Height / r.Height));
    }

    private static int Buttons(PointerPointProperties p) =>
        (p.IsLeftButtonPressed ? 1 : 0) | (p.IsMiddleButtonPressed ? 2 : 0) | (p.IsRightButtonPressed ? 4 : 0);

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var p = e.GetCurrentPoint(this);
        var (x, y) = Map(p.Position);
        _buttons = Buttons(p.Properties);
        _client?.SendPointer(x, y, _buttons);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        var p = e.GetCurrentPoint(this);
        _buttons = Buttons(p.Properties);
        var (x, y) = Map(p.Position);
        _client?.SendPointer(x, y, _buttons);
        e.Handled = true;
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        var p = e.GetCurrentPoint(this);
        _buttons = Buttons(p.Properties);
        var (x, y) = Map(p.Position);
        _client?.SendPointer(x, y, _buttons);
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        var (x, y) = Map(e.GetPosition(this));
        var mask = e.Delta.Y > 0 ? 8 : 16;
        _client?.SendPointer(x, y, _buttons | mask);
        _client?.SendPointer(x, y, _buttons);
        e.Handled = true;
    }

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (KeySym(e) is { } sym)
        {
            _pressed.Add(sym);
            _client?.SendKey(sym, true);
        }

        e.Handled = true;
    }

    protected override void OnKeyUp(KeyEventArgs e)
    {
        base.OnKeyUp(e);
        if (KeySym(e) is { } sym)
        {
            _pressed.Remove(sym);
            _client?.SendKey(sym, false);
        }

        e.Handled = true;
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        ReleaseKeys();
    }

    private void ReleaseKeys()
    {
        foreach (var k in _pressed) _client?.SendKey(k, false);
        _pressed.Clear();
    }

    private static uint? KeySym(KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Back: return KeySyms.BackSpace;
            case Key.Tab: return KeySyms.Tab;
            case Key.Enter: return KeySyms.Return;
            case Key.Escape: return KeySyms.Escape;
            case Key.Delete: return KeySyms.Delete;
            case Key.Home: return KeySyms.Home;
            case Key.End: return KeySyms.End;
            case Key.PageUp: return KeySyms.PageUp;
            case Key.PageDown: return KeySyms.PageDown;
            case Key.Left: return KeySyms.Left;
            case Key.Right: return KeySyms.Right;
            case Key.Up: return KeySyms.Up;
            case Key.Down: return KeySyms.Down;
            case Key.Insert: return KeySyms.Insert;
            case >= Key.F1 and <= Key.F12: return KeySyms.F1 + (uint)(e.Key - Key.F1);
            case Key.LeftShift or Key.RightShift: return KeySyms.ShiftL;
            case Key.LeftCtrl or Key.RightCtrl: return KeySyms.ControlL;
            case Key.LeftAlt or Key.RightAlt: return KeySyms.AltL;
            case Key.LWin or Key.RWin: return KeySyms.SuperL;
            case Key.CapsLock: return KeySyms.CapsLock;
            case Key.Apps: return KeySyms.Menu;
            case Key.Space: return 0x20;
        }

        // Printable: use the produced character (layout-aware).
        if (e.KeySymbol is { Length: > 0 } s && !char.IsControl(s[0])) return KeySyms.FromChar(char.ConvertToUtf32(s, 0));
        return e.Key switch
        {
            >= Key.A and <= Key.Z => (uint)('a' + (e.Key - Key.A)),
            >= Key.D0 and <= Key.D9 => (uint)('0' + (e.Key - Key.D0)),
            _ => null
        };
    }
}
