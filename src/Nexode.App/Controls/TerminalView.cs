using System.Globalization;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Media;
using Avalonia.Threading;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Protocols.Terminal;

namespace Nexode.App.Controls;

/// <summary>
/// Renders a <see cref="TerminalEmulator"/> and turns keyboard, mouse and clipboard input into bytes for the host.
/// Scrollback with the mouse wheel, selection with copy (on select or Ctrl+Shift+C), paste with Ctrl+Shift+V or right click.
/// </summary>
public sealed class TerminalView : Control
{
    public static readonly StyledProperty<TerminalEmulator?> EmulatorProperty = AvaloniaProperty.Register<TerminalView, TerminalEmulator?>(nameof(Emulator));
    public static readonly StyledProperty<double> FontSizeProperty = AvaloniaProperty.Register<TerminalView, double>(nameof(FontSize), 13);

    private static readonly FontFamily MonoFamily = new("Cascadia Mono, Cascadia Code, Consolas, SF Mono, Menlo, DejaVu Sans Mono, Liberation Mono, Ubuntu Mono, monospace");
    private readonly DispatcherTimer _frame = new() { Interval = TimeSpan.FromMilliseconds(16) };
    private readonly DispatcherTimer _resizeDebounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private Typeface _typeface = new(MonoFamily);
    private Typeface _bold = new(MonoFamily, weight: FontWeight.Bold);
    private double _cellW = 8, _cellH = 16, _baseline;
    private long _drawnVersion = -1;
    private int _scroll; // lines scrolled back into history
    private (int Line, int Col)? _selStart, _selEnd;
    private bool _selecting;
    private TerminalColors _colors = TerminalColors.Dark();

    public TerminalView()
    {
        Focusable = true;
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Ibeam);
        _frame.Tick += (_, _) =>
        {
            if (Emulator is { } t && t.Version != _drawnVersion) InvalidateVisual();
        };
        _resizeDebounce.Tick += (_, _) =>
        {
            _resizeDebounce.Stop();
            ApplySize();
        };
        UpdateMetrics();
    }

    public TerminalEmulator? Emulator { get => GetValue(EmulatorProperty); set => SetValue(EmulatorProperty, value); }
    public double FontSize { get => GetValue(FontSizeProperty); set => SetValue(FontSizeProperty, value); }

    /// <summary>Bytes typed by the user.</summary>
    public event Action<byte[]>? Input;

    /// <summary>Keyboard, text and paste input only (no mouse reports): what "send to all terminals" repeats.</summary>
    public event Action<byte[]>? Typed;

    /// <summary>New size in cells.</summary>
    public event Action<int, int>? GridResized;

    public int Columns { get; private set; } = 80;
    public int Rows { get; private set; } = 24;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _colors = TerminalColors.ForTheme();
        _frame.Start();
        AppHost.Theme.Changed += OnTheme;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _frame.Stop();
        AppHost.Theme.Changed -= OnTheme;
    }

    private void OnTheme()
    {
        _colors = TerminalColors.ForTheme();
        InvalidateVisual();
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == FontSizeProperty)
        {
            UpdateMetrics();
            ApplySize();
        }
        else if (change.Property == EmulatorProperty)
        {
            _scroll = 0;
            ApplySize();
        }
        else if (change.Property == BoundsProperty)
        {
            _resizeDebounce.Stop();
            _resizeDebounce.Start();
        }
    }

    private void UpdateMetrics()
    {
        _typeface = new Typeface(MonoFamily);
        _bold = new Typeface(MonoFamily, weight: FontWeight.Bold);
        var ft = new FormattedText("MMMMMMMMMM", CultureInfo.InvariantCulture, FlowDirection.LeftToRight, _typeface, FontSize, Brushes.White);
        _cellW = Math.Max(4, ft.WidthIncludingTrailingWhitespace / 10);
        _cellH = Math.Ceiling(ft.Height * 1.08);
        _baseline = (_cellH - ft.Height) / 2;
    }

    private void ApplySize()
    {
        var cols = Math.Max(10, (int)((Bounds.Width - 8) / _cellW));
        var rows = Math.Max(3, (int)((Bounds.Height - 6) / _cellH));
        if (Bounds.Width <= 0) return;
        Columns = cols;
        Rows = rows;
        if (Emulator is { } t && (t.Columns != cols || t.Rows != rows))
        {
            lock (t.Sync) t.Resize(cols, rows);
            GridResized?.Invoke(cols, rows);
            InvalidateVisual();
        }
    }

    // ------------------------------------------------------------------ rendering

    public override void Render(DrawingContext context)
    {
        context.FillRectangle(_colors.Background, new Rect(Bounds.Size));
        if (Emulator is not { } t) return;
        lock (t.Sync)
        {
            _drawnVersion = t.Version;
            var history = t.Screen.Scrollback.Count;
            _scroll = Math.Clamp(_scroll, 0, history);
            var first = history - _scroll;
            var (s0, s1) = OrderedSelection();
            for (var r = 0; r < t.Rows; r++)
            {
                var abs = first + r;
                var line = t.LineAt(abs);
                DrawLine(context, line, r, abs, s0, s1, t.ReverseVideo);
            }

            // Cursor (only when not scrolled back).
            if (_scroll == 0 && t.CursorVisible)
            {
                var rect = new Rect(4 + t.CursorCol * _cellW, 3 + t.CursorRow * _cellH, _cellW, _cellH);
                if (IsFocused) context.FillRectangle(_colors.CursorBrush, rect);
                else context.DrawRectangle(null, new Pen(_colors.CursorBrush, 1), rect.Deflate(0.5));
                if (IsFocused)
                {
                    var cell = t.Screen.Lines[t.CursorRow].Cells[t.CursorCol];
                    if (cell.Code > 32) DrawText(context, char.ConvertFromUtf32(cell.Code), rect.X, rect.Y, _colors.Background, (cell.Attr.Flags & CellFlags.Bold) != 0, false);
                }
            }

            if (_scroll > 0)
            {
                var text = new FormattedText(Loc.F("Terminal.Scrolled", _scroll), CultureInfo.CurrentCulture, FlowDirection.LeftToRight, new Typeface("Inter"), 11, _colors.Foreground);
                var box = new Rect(Bounds.Width - text.Width - 22, 6, text.Width + 14, text.Height + 6);
                context.FillRectangle(_colors.Badge, box, 6);
                context.DrawText(text, new Point(box.X + 7, box.Y + 3));
            }
        }
    }

    private void DrawLine(DrawingContext ctx, TerminalLine line, int row, int abs, (int, int) s0, (int, int) s1, bool reverse)
    {
        var y = 3 + row * _cellH;
        var cells = line.Cells;
        var cols = Math.Min(cells.Length, Columns + 2);
        var sb = new StringBuilder();
        var runStart = 0;
        IBrush? runFg = null;
        var runBold = false;
        var runUnderline = false;
        var runStrike = false;

        void Flush(int end)
        {
            if (sb.Length == 0) return;
            var x = 4 + runStart * _cellW;
            DrawText(ctx, sb.ToString(), x, y, runFg!, runBold, false);
            if (runUnderline) ctx.DrawLine(new Pen(runFg, 1), new Point(x, y + _cellH - 2), new Point(4 + end * _cellW, y + _cellH - 2));
            if (runStrike) ctx.DrawLine(new Pen(runFg, 1), new Point(x, y + _cellH / 2), new Point(4 + end * _cellW, y + _cellH / 2));
            sb.Clear();
        }

        for (var c = 0; c < cols; c++)
        {
            var cell = cells[c];
            var a = cell.Attr;
            if ((a.Flags & CellFlags.WideTail) != 0) continue;
            var inverse = ((a.Flags & CellFlags.Inverse) != 0) ^ reverse ^ IsSelected(abs, c, s0, s1);
            var fgIndex = a.Fg;
            if ((a.Flags & CellFlags.Bold) != 0 && fgIndex is >= 0 and < 8) fgIndex += 8;
            var fg = _colors.Brush(fgIndex, foreground: true);
            var bg = a.Bg == -1 ? null : _colors.Brush(a.Bg, foreground: false);
            if (inverse)
            {
                (fg, bg) = (bg ?? _colors.Background, fg);
            }

            if ((a.Flags & CellFlags.Dim) != 0 && fg is ISolidColorBrush sc) fg = new SolidColorBrush(sc.Color, 0.6);
            if ((a.Flags & CellFlags.Hidden) != 0) fg = bg ?? _colors.Background;
            var width = c + 1 < cells.Length && (cells[c + 1].Attr.Flags & CellFlags.WideTail) != 0 ? 2 : 1;
            if (bg is not null) ctx.FillRectangle(bg, new Rect(4 + c * _cellW, y, _cellW * width + 0.5, _cellH));

            var bold = (a.Flags & CellFlags.Bold) != 0;
            var underline = (a.Flags & CellFlags.Underline) != 0;
            var strike = (a.Flags & CellFlags.Strike) != 0;
            if (sb.Length > 0 && (!ReferenceEquals(fg, runFg) && !SameColor(fg, runFg) || bold != runBold || underline != runUnderline || strike != runStrike || width == 2))
            {
                Flush(c);
            }

            if (sb.Length == 0)
            {
                runStart = c;
                runFg = fg;
                runBold = bold;
                runUnderline = underline;
                runStrike = strike;
            }

            if (width == 2)
            {
                // Wide glyphs are drawn on their own so the grid stays aligned.
                DrawText(ctx, char.ConvertFromUtf32(cell.Code), 4 + c * _cellW, y, fg, bold, false);
                continue;
            }

            sb.Append(cell.Code == 0 ? " " : char.ConvertFromUtf32(cell.Code));
        }

        Flush(cols);
    }

    private static bool SameColor(IBrush? a, IBrush? b) => a is ISolidColorBrush x && b is ISolidColorBrush y && x.Color == y.Color && Math.Abs(x.Opacity - y.Opacity) < 0.01;

    private void DrawText(DrawingContext ctx, string text, double x, double y, IBrush fg, bool bold, bool italic)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var ft = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, bold ? _bold : _typeface, FontSize, fg);
        ctx.DrawText(ft, new Point(x, y + _baseline));
        _ = italic;
    }

    // ------------------------------------------------------------------ selection

    private ((int, int), (int, int)) OrderedSelection()
    {
        if (_selStart is not { } a || _selEnd is not { } b) return ((-1, -1), (-1, -1));
        return a.Line < b.Line || a.Line == b.Line && a.Col <= b.Col ? (a, b) : (b, a);
    }

    private static bool IsSelected(int line, int col, (int Line, int Col) s0, (int Line, int Col) s1)
    {
        if (s0.Line < 0) return false;
        if (line < s0.Line || line > s1.Line) return false;
        if (line == s0.Line && col < s0.Col) return false;
        if (line == s1.Line && col >= s1.Col) return false;
        return true;
    }

    private (int Line, int Col) CellAt(Point p)
    {
        var t = Emulator!;
        var row = Math.Clamp((int)((p.Y - 3) / _cellH), 0, t.Rows - 1);
        var col = Math.Clamp((int)Math.Round((p.X - 4) / _cellW), 0, t.Columns);
        return (t.Screen.Scrollback.Count - _scroll + row, col);
    }

    public bool HasSelection => _selStart is { } a && _selEnd is { } b && a != b;

    public string SelectedText()
    {
        if (Emulator is not { } t || !HasSelection) return "";
        var (s0, s1) = OrderedSelection();
        lock (t.Sync) return t.GetText(s0.Item1, s0.Item2, s1.Item1, s1.Item2);
    }

    public async Task CopyAsync()
    {
        var text = SelectedText();
        if (text.Length > 0 && TopLevel.GetTopLevel(this)?.Clipboard is { } cb) await cb.SetTextAsync(text);
    }

    public async Task PasteAsync()
    {
        if (Emulator is not { } t || TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        if (await cb.TryGetTextAsync() is { Length: > 0 } text) Send(TerminalInput.Paste(text, t));
    }

    public void SelectAll()
    {
        if (Emulator is not { } t) return;
        _selStart = (0, 0);
        _selEnd = (t.TotalLines - 1, t.Columns);
        InvalidateVisual();
    }

    public void ClearSelection()
    {
        _selStart = _selEnd = null;
        InvalidateVisual();
    }

    // ------------------------------------------------------------------ input

    private void Send(byte[] data, bool mouse = false)
    {
        if (_scroll != 0)
        {
            _scroll = 0;
            InvalidateVisual();
        }

        Input?.Invoke(data);
        if (!mouse) Typed?.Invoke(data);
    }

    private static TermMods Mods(KeyModifiers m) =>
        (m.HasFlag(KeyModifiers.Shift) ? TermMods.Shift : 0) | (m.HasFlag(KeyModifiers.Alt) ? TermMods.Alt : 0) | (m.HasFlag(KeyModifiers.Control) ? TermMods.Ctrl : 0);

    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Emulator is not { } t) return;
        var ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift);
        var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);

        if (ctrl && shift && e.Key == Key.C)
        {
            await CopyAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && shift && e.Key == Key.V || shift && e.Key == Key.Insert)
        {
            await PasteAsync();
            e.Handled = true;
            return;
        }

        if (ctrl && !shift && e.Key == Key.C && HasSelection)
        {
            await CopyAsync();
            ClearSelection();
            e.Handled = true;
            return;
        }

        if (shift && e.Key is Key.PageUp or Key.PageDown && !t.IsAlternate)
        {
            ScrollBy(e.Key == Key.PageUp ? t.Rows - 1 : -(t.Rows - 1));
            e.Handled = true;
            return;
        }

        if (ctrl && e.Key is Key.OemPlus or Key.Add or Key.OemMinus or Key.Subtract or Key.D0)
        {
            FontSize = e.Key is Key.D0 ? AppHost.Settings.TerminalFontSize : Math.Clamp(FontSize + (e.Key is Key.OemPlus or Key.Add ? 1 : -1), 8, 32);
            e.Handled = true;
            return;
        }

        var key = e.Key switch
        {
            Key.Enter => TermKey.Enter,
            Key.Back => TermKey.Backspace,
            Key.Tab => TermKey.Tab,
            Key.Escape => TermKey.Escape,
            Key.Up => TermKey.Up,
            Key.Down => TermKey.Down,
            Key.Left => TermKey.Left,
            Key.Right => TermKey.Right,
            Key.Home => TermKey.Home,
            Key.End => TermKey.End,
            Key.PageUp => TermKey.PageUp,
            Key.PageDown => TermKey.PageDown,
            Key.Insert => TermKey.Insert,
            Key.Delete => TermKey.Delete,
            >= Key.F1 and <= Key.F12 => TermKey.F1 + (e.Key - Key.F1),
            _ => TermKey.None
        };

        if (key != TermKey.None)
        {
            if (TerminalInput.Encode(key, Mods(e.KeyModifiers), t) is { } bytes) Send(bytes);
            e.Handled = true;
            return;
        }

        // Ctrl+letter and friends: control codes (TextInput does not deliver them).
        if (ctrl && !alt || ctrl && alt && OperatingSystem.IsMacOS())
        {
            var ch = e.KeySymbol is { Length: 1 } sym ? sym[0] : KeyChar(e.Key);
            if (ch != '\0' && TerminalInput.Control(ch, alt) is { } control)
            {
                Send(control);
                e.Handled = true;
            }
        }
    }

    private static char KeyChar(Key key) => key switch
    {
        >= Key.A and <= Key.Z => (char)('a' + (key - Key.A)),
        >= Key.D0 and <= Key.D9 => (char)('0' + (key - Key.D0)),
        Key.OemOpenBrackets => '[',
        Key.OemCloseBrackets => ']',
        Key.OemBackslash or Key.OemPipe => '\\',
        Key.Space => ' ',
        _ => '\0'
    };

    protected override void OnTextInput(TextInputEventArgs e)
    {
        base.OnTextInput(e);
        if (Emulator is null || string.IsNullOrEmpty(e.Text)) return;
        Send(TerminalInput.Text(e.Text, false));
        e.Handled = true;
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        Focus();
        if (Emulator is not { } t) return;
        var point = e.GetCurrentPoint(this);
        if (t.MouseMode != 0 && !e.KeyModifiers.HasFlag(KeyModifiers.Shift) && _scroll == 0)
        {
            var (line, col) = CellAt(point.Position);
            var button = point.Properties.IsLeftButtonPressed ? 0 : point.Properties.IsMiddleButtonPressed ? 1 : 2;
            if (TerminalInput.Mouse(t, button, true, col, line - t.Screen.Scrollback.Count, Mods(e.KeyModifiers)) is { } m) Send(m, mouse: true);
            e.Handled = true;
            return;
        }

        if (point.Properties.IsLeftButtonPressed)
        {
            var cell = CellAt(point.Position);
            if (e.ClickCount == 2)
            {
                SelectWord(cell);
            }
            else if (e.ClickCount >= 3)
            {
                _selStart = (cell.Line, 0);
                _selEnd = (cell.Line, t.Columns);
            }
            else
            {
                _selStart = _selEnd = cell;
                _selecting = true;
            }

            e.Pointer.Capture(this);
            InvalidateVisual();
        }
        else if (point.Properties.IsMiddleButtonPressed)
        {
            _ = PasteAsync();
        }
    }

    private void SelectWord((int Line, int Col) cell)
    {
        var line = Emulator!.LineAt(cell.Line);
        bool Word(int c) => c >= 0 && c < line.Cells.Length && line.Cells[c].Code is var code && code > 32 && !"\"'()[]{}<>,;|`".Contains((char)Math.Min(code, 0xFFFF));
        var s = Math.Min(cell.Col, line.Cells.Length - 1);
        var en = s;
        while (Word(s - 1)) s--;
        while (Word(en)) en++;
        _selStart = (cell.Line, s);
        _selEnd = (cell.Line, en);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_selecting || Emulator is null) return;
        var p = e.GetPosition(this);
        if (p.Y < 0) ScrollBy(1);
        else if (p.Y > Bounds.Height) ScrollBy(-1);
        _selEnd = CellAt(p);
        InvalidateVisual();
    }

    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (Emulator is { MouseMode: not 0 } t && !_selecting && _scroll == 0)
        {
            var (line, col) = CellAt(e.GetPosition(this));
            var button = e.InitialPressMouseButton switch { MouseButton.Middle => 1, MouseButton.Right => 2, _ => 0 };
            if (TerminalInput.Mouse(t, button, false, col, line - t.Screen.Scrollback.Count, Mods(e.KeyModifiers)) is { } m) Send(m, mouse: true);
            return;
        }

        _selecting = false;
        e.Pointer.Capture(null);
        if (HasSelection && AppHost.Settings.TerminalCopyOnSelect) await CopyAsync();
        if (!HasSelection) ClearSelection();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Emulator is not { } t) return;
        var lines = (int)Math.Round(e.Delta.Y * 3);
        if (t.MouseMode != 0 && _scroll == 0)
        {
            var (line, col) = CellAt(e.GetPosition(this));
            for (var i = 0; i < Math.Abs(lines); i++)
            {
                if (TerminalInput.Mouse(t, lines > 0 ? 64 : 65, true, col, line - t.Screen.Scrollback.Count, Mods(e.KeyModifiers)) is { } m) Input?.Invoke(m);
            }
        }
        else if (t.IsAlternate)
        {
            // Full-screen apps without mouse support: arrow keys.
            for (var i = 0; i < Math.Abs(lines); i++)
            {
                if (TerminalInput.Encode(lines > 0 ? TermKey.Up : TermKey.Down, TermMods.None, t) is { } k) Input?.Invoke(k);
            }
        }
        else
        {
            ScrollBy(lines);
        }

        e.Handled = true;
    }

    public void ScrollBy(int lines)
    {
        if (Emulator is not { } t) return;
        _scroll = Math.Clamp(_scroll + lines, 0, t.Screen.Scrollback.Count);
        InvalidateVisual();
    }

    protected override void OnGotFocus(FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        InvalidateVisual();
    }

    protected override void OnLostFocus(FocusChangedEventArgs e)
    {
        base.OnLostFocus(e);
        InvalidateVisual();
    }
}

/// <summary>Terminal palette per theme (xterm 16 colors + 256-color cube + true color).</summary>
public sealed class TerminalColors
{
    private readonly IBrush[] _palette = new IBrush[256];
    private readonly Dictionary<int, IBrush> _trueColor = [];

    public required IBrush Background { get; init; }
    public required IBrush Foreground { get; init; }
    public required IBrush CursorBrush { get; init; }
    public required IBrush Badge { get; init; }

    public static TerminalColors ForTheme()
    {
        var theme = AppHost.Theme;
        if (!theme.IsDark) return Light();
        var accent = ThemeService.ToColor(theme.Palette["AccentText"]);
        return theme.Variant switch
        {
            Nexode.Core.Theming.PaletteVariant.Amoled => Build("#000000", "#E6E6EC", accent, "#1E1E24", Dark16),
            Nexode.Core.Theming.PaletteVariant.Dark => Build("#141414", "#E6E6E6", accent, "#2E2E2E", Dark16),
            Nexode.Core.Theming.PaletteVariant.Shadow => Build("#2A2A2D", "#ECECEE", accent, "#4A4A50", Dark16),
            Nexode.Core.Theming.PaletteVariant.Mica => Build("#1A1A1A", "#F0F0F0", accent, "#323232", Dark16),
            _ => Build("#15131E", "#E4E2EE", accent, "#2B2938", Dark16)
        };
    }

    public static TerminalColors Dark() => Build("#15131E", "#E4E2EE", Color.Parse("#8F7BFF"), "#2B2938", Dark16);

    public static TerminalColors Light() =>
        Build("#FBFBFD", "#24222F", ThemeService.ToColor(AppHost.Theme?.Palette["Accent"] ?? Nexode.Core.Theming.Rgb.FromHex("#573CFA")), "#E9E8EF", Light16);

    private static readonly string[] Dark16 =
    [
        "#2A2737", "#F0506E", "#3FC380", "#F5B342", "#5B8CFF", "#B07BFF", "#3ACBD9", "#C9C6D6",
        "#6D6981", "#FF7A93", "#6FE0A3", "#FFD27A", "#8AAEFF", "#CBA6FF", "#78E3EE", "#FFFFFF"
    ];

    private static readonly string[] Light16 =
    [
        "#24222F", "#C8193C", "#06804A", "#A86400", "#2457D6", "#7A3BD9", "#08808D", "#6E6B80",
        "#5F5C71", "#E0294F", "#0E9C5D", "#C27A00", "#3A6BF0", "#8F52EE", "#0A9AAA", "#2A2737"
    ];

    private static TerminalColors Build(string bg, string fg, Color cursor, string badge, string[] sixteen)
    {
        var t = new TerminalColors
        {
            Background = new SolidColorBrush(Color.Parse(bg)),
            Foreground = new SolidColorBrush(Color.Parse(fg)),
            CursorBrush = new SolidColorBrush(cursor),
            Badge = new SolidColorBrush(Color.Parse(badge))
        };
        for (var i = 0; i < 16; i++) t._palette[i] = new SolidColorBrush(Color.Parse(sixteen[i]));
        int[] levels = [0, 95, 135, 175, 215, 255];
        for (var i = 16; i < 232; i++)
        {
            var n = i - 16;
            t._palette[i] = new SolidColorBrush(Color.FromRgb((byte)levels[n / 36], (byte)levels[n / 6 % 6], (byte)levels[n % 6]));
        }

        for (var i = 232; i < 256; i++)
        {
            var v = (byte)(8 + (i - 232) * 10);
            t._palette[i] = new SolidColorBrush(Color.FromRgb(v, v, v));
        }

        return t;
    }

    public IBrush Brush(int color, bool foreground)
    {
        if (color == TermColor.Default) return foreground ? Foreground : Background;
        if (color is >= 0 and < 256) return _palette[color];
        if (TermColor.IsTrueColor(color))
        {
            if (!_trueColor.TryGetValue(color, out var b))
            {
                if (_trueColor.Count > 4096) _trueColor.Clear();
                b = new SolidColorBrush(Color.FromRgb((byte)(color >> 16), (byte)(color >> 8), (byte)color));
                _trueColor[color] = b;
            }

            return b;
        }

        return foreground ? Foreground : Background;
    }
}
