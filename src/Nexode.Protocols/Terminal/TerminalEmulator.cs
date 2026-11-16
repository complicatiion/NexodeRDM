using System.Globalization;
using System.Text;

namespace Nexode.Protocols.Terminal;

/// <summary>
/// xterm-compatible terminal state machine (VT100/VT220 + common xterm extensions): UTF-8 input, CSI/OSC/ESC parsing,
/// SGR colors (16/256/true color), scroll regions, alternate screen, bracketed paste, cursor modes.
/// Not thread-safe: feed and read from one thread (the UI thread) or lock <see cref="Sync"/>.
/// </summary>
public sealed class TerminalEmulator
{
    private enum State { Ground, Escape, EscapeIntermediate, Csi, CsiIgnore, Osc, OscEscape, Dcs, DcsEscape, Charset }

    private readonly Decoder _utf8 = new UTF8Encoding(false, false).GetDecoder();
    private readonly char[] _chars = new char[8192];
    private readonly List<int> _params = new(16);
    private readonly StringBuilder _osc = new();
    private State _state;
    private int _param = -1;
    private char _prefix;
    private char _intermediate;
    private char _charsetTarget;
    private int _highSurrogate;
    private bool _paramColon;

    private TerminalScreen _main;
    private TerminalScreen _alt;
    private CellAttr _attr = CellAttr.Default;
    private bool _wrapPending;
    private int _top;
    private int _bottom;
    private bool[] _tabs = [];
    private (int Row, int Col, CellAttr Attr, bool Origin, bool G0Line) _saved;
    private bool _g0Line;
    private bool _g1Line;
    private bool _shiftOut;
    private int _lastPrinted = ' ';

    public TerminalEmulator(int columns = 80, int rows = 24, int scrollback = 5000)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        _main = new TerminalScreen(columns, rows, scrollback);
        _alt = new TerminalScreen(columns, rows, 0);
        Screen = _main;
        _bottom = rows - 1;
        ResetTabs();
    }

    public object Sync { get; } = new();
    public TerminalScreen Screen { get; private set; }
    public bool IsAlternate => Screen == _alt;
    public int Columns => Screen.Columns;
    public int Rows => Screen.Rows;
    public int CursorRow { get; private set; }
    public int CursorCol { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool ApplicationCursorKeys { get; private set; }
    public bool ApplicationKeypad { get; private set; }
    public bool BracketedPaste { get; private set; }
    public bool AutoWrap { get; private set; } = true;
    public bool OriginMode { get; private set; }
    public bool InsertMode { get; private set; }
    public bool LineFeedNewLine { get; private set; }
    public bool ReverseVideo { get; private set; }
    public int MouseMode { get; private set; }
    public bool MouseSgr { get; private set; }
    public string Title { get; private set; } = "";
    /// <summary>Increments on every visible change; renderers compare it to skip redundant frames.</summary>
    public long Version { get; private set; }

    /// <summary>Bytes the terminal must send back to the host (device reports).</summary>
    public event Action<byte[]>? Response;
    public event Action? TitleChanged;
    public event Action? Bell;

    // ------------------------------------------------------------------ input

    public void Feed(ReadOnlySpan<byte> data)
    {
        while (data.Length > 0)
        {
            var take = Math.Min(data.Length, _chars.Length / 2);
            var n = _utf8.GetChars(data[..take], _chars, flush: false);
            for (var i = 0; i < n; i++) Process(_chars[i]);
            data = data[take..];
        }

        Version++;
    }

    public void Feed(string text)
    {
        foreach (var ch in text) Process(ch);
        Version++;
    }

    private void Process(char ch)
    {
        // Surrogate pairs become one code point.
        int code = ch;
        if (char.IsHighSurrogate(ch))
        {
            _highSurrogate = ch;
            return;
        }

        if (char.IsLowSurrogate(ch))
        {
            if (_highSurrogate == 0) return;
            code = char.ConvertToUtf32((char)_highSurrogate, ch);
            _highSurrogate = 0;
        }
        else
        {
            _highSurrogate = 0;
        }

        // C0 controls act in (almost) every state.
        if (code < 0x20 && _state is not (State.Osc or State.OscEscape or State.Dcs or State.DcsEscape))
        {
            if (code == 0x1B)
            {
                _state = State.Escape;
                _intermediate = '\0';
                return;
            }

            if (code is 0x18 or 0x1A)
            {
                _state = State.Ground;
                return;
            }

            Execute(code);
            return;
        }

        switch (_state)
        {
            case State.Ground:
                Print(code);
                break;
            case State.Escape:
                EscapeChar(code);
                break;
            case State.EscapeIntermediate:
                _state = State.Ground;
                break;
            case State.Charset:
                SetCharset(_charsetTarget, (char)code);
                _state = State.Ground;
                break;
            case State.Csi:
                CsiChar(code);
                break;
            case State.CsiIgnore:
                if (code is >= 0x40 and <= 0x7E) _state = State.Ground;
                break;
            case State.Osc:
                if (code == 0x07)
                {
                    FinishOsc();
                }
                else if (code == 0x1B)
                {
                    _state = State.OscEscape;
                }
                else if (_osc.Length < 4096)
                {
                    _osc.Append(char.ConvertFromUtf32(code));
                }

                break;
            case State.OscEscape:
                if (code == '\\') FinishOsc();
                else _state = State.Ground;
                break;
            case State.Dcs:
                if (code == 0x1B) _state = State.DcsEscape;
                break;
            case State.DcsEscape:
                _state = code == '\\' ? State.Ground : State.Dcs;
                break;
        }
    }

    private void EscapeChar(int code)
    {
        _state = State.Ground;
        switch ((char)code)
        {
            case '[':
                _state = State.Csi;
                _params.Clear();
                _param = -1;
                _prefix = '\0';
                _intermediate = '\0';
                _paramColon = false;
                break;
            case ']':
                _state = State.Osc;
                _osc.Clear();
                break;
            case 'P' or 'X' or '^' or '_':
                _state = State.Dcs;
                break;
            case '(' or ')' or '*' or '+':
                _charsetTarget = (char)code;
                _state = State.Charset;
                break;
            case '#' or '%' or ' ':
                _state = State.EscapeIntermediate;
                break;
            case '7':
                SaveCursor();
                break;
            case '8':
                RestoreCursor();
                break;
            case 'D':
                Index();
                break;
            case 'E':
                Index();
                CursorCol = 0;
                break;
            case 'H':
                if (CursorCol < _tabs.Length) _tabs[CursorCol] = true;
                break;
            case 'M':
                ReverseIndex();
                break;
            case 'c':
                Reset();
                break;
            case '=':
                ApplicationKeypad = true;
                break;
            case '>':
                ApplicationKeypad = false;
                break;
        }
    }

    private void CsiChar(int code)
    {
        if (code is >= '0' and <= '9')
        {
            _param = (_param < 0 ? 0 : _param) * 10 + (code - '0');
            if (_param > 99999) _param = 99999;
            return;
        }

        switch (code)
        {
            case ';':
                _params.Add(_param);
                _param = -1;
                return;
            case ':':
                // Sub-parameters (38:2:r:g:b) are treated like ';'.
                _params.Add(_param);
                _param = -1;
                _paramColon = true;
                return;
            case '?' or '>' or '<' or '=':
                _prefix = (char)code;
                return;
            case >= 0x20 and <= 0x2F:
                _intermediate = (char)code;
                return;
            case >= 0x40 and <= 0x7E:
                _params.Add(_param);
                _state = State.Ground;
                Csi((char)code);
                return;
            default:
                _state = State.CsiIgnore;
                return;
        }
    }

    private int P(int index, int fallback = 1) => index < _params.Count && _params[index] > 0 ? _params[index] : fallback;
    private int P0(int index) => index < _params.Count && _params[index] >= 0 ? _params[index] : 0;

    // ------------------------------------------------------------------ printing

    private void Print(int code)
    {
        if (_shiftOut ? _g1Line : _g0Line) code = LineDrawing(code);
        var width = CharWidth.Of(code);
        if (width == 0)
        {
            return; // combining marks are not composed
        }

        if (_wrapPending)
        {
            if (AutoWrap)
            {
                Screen.Lines[CursorRow].Wrapped = true;
                CursorCol = 0;
                Index();
            }

            _wrapPending = false;
        }

        if (width == 2 && CursorCol == Columns - 1)
        {
            // A wide char does not fit: wrap first.
            if (AutoWrap)
            {
                Screen.Lines[CursorRow].Cells[CursorCol] = Cell.Blank(_attr);
                Screen.Lines[CursorRow].Wrapped = true;
                CursorCol = 0;
                Index();
            }
            else
            {
                return;
            }
        }

        var line = Screen.Lines[CursorRow];
        if (InsertMode)
        {
            Array.Copy(line.Cells, CursorCol, line.Cells, CursorCol + width, Columns - CursorCol - width);
        }

        FixWideNeighbours(line, CursorCol, width);
        line.Cells[CursorCol] = new Cell(code, _attr);
        if (width == 2) line.Cells[CursorCol + 1] = new Cell(0, _attr with { Flags = _attr.Flags | CellFlags.WideTail });
        _lastPrinted = code;

        if (CursorCol + width >= Columns)
        {
            CursorCol = Columns - 1;
            _wrapPending = true;
        }
        else
        {
            CursorCol += width;
        }
    }

    private void FixWideNeighbours(TerminalLine line, int col, int width)
    {
        // Overwriting half of a wide char clears the other half.
        if ((line.Cells[col].Attr.Flags & CellFlags.WideTail) != 0 && col > 0) line.Cells[col - 1] = Cell.Blank(_attr);
        var end = col + width;
        if (end < Columns && (line.Cells[end].Attr.Flags & CellFlags.WideTail) != 0) line.Cells[end] = Cell.Blank(_attr);
    }

    private static int LineDrawing(int code) => code switch
    {
        'j' => '┘', 'k' => '┐', 'l' => '┌', 'm' => '└', 'n' => '┼', 'q' => '─', 't' => '├', 'u' => '┤', 'v' => '┴', 'w' => '┬',
        'x' => '│', 'a' => '▒', '`' => '◆', 'f' => '°', 'g' => '±', 'y' => '≤', 'z' => '≥', '~' => '·', 'o' => '⎺', 's' => '⎽',
        _ => code
    };

    private void SetCharset(char target, char set)
    {
        var line = set == '0';
        if (target == '(') _g0Line = line;
        else if (target == ')') _g1Line = line;
    }

    // ------------------------------------------------------------------ controls

    private void Execute(int code)
    {
        switch (code)
        {
            case 0x07:
                Bell?.Invoke();
                break;
            case 0x08:
                _wrapPending = false;
                if (CursorCol > 0) CursorCol--;
                break;
            case 0x09:
                TabForward(1);
                break;
            case 0x0A or 0x0B or 0x0C:
                Index();
                if (LineFeedNewLine) CursorCol = 0;
                break;
            case 0x0D:
                _wrapPending = false;
                CursorCol = 0;
                break;
            case 0x0E:
                _shiftOut = true;
                break;
            case 0x0F:
                _shiftOut = false;
                break;
        }
    }

    private void Index()
    {
        _wrapPending = false;
        if (CursorRow == _bottom) Screen.ScrollUp(_top, _bottom, 1, _attr);
        else if (CursorRow < Rows - 1) CursorRow++;
    }

    private void ReverseIndex()
    {
        _wrapPending = false;
        if (CursorRow == _top) Screen.ScrollDown(_top, _bottom, 1, _attr);
        else if (CursorRow > 0) CursorRow--;
    }

    private void TabForward(int n)
    {
        _wrapPending = false;
        for (var k = 0; k < n; k++)
        {
            var c = CursorCol + 1;
            while (c < Columns - 1 && !_tabs[c]) c++;
            CursorCol = Math.Min(c, Columns - 1);
        }
    }

    private void TabBack(int n)
    {
        for (var k = 0; k < n; k++)
        {
            var c = CursorCol - 1;
            while (c > 0 && !_tabs[c]) c--;
            CursorCol = Math.Max(c, 0);
        }
    }

    private void ResetTabs()
    {
        _tabs = new bool[Columns];
        for (var i = 8; i < Columns; i += 8) _tabs[i] = true;
    }

    // ------------------------------------------------------------------ CSI

    private void Csi(char final)
    {
        if (_prefix == '?')
        {
            if (final is 'h' or 'l') PrivateMode(final == 'h');
            return;
        }

        if (_prefix == '>')
        {
            if (final == 'c') Send("\e[>0;10;0c");
            return;
        }

        if (_intermediate == '!' && final == 'p')
        {
            SoftReset();
            return;
        }

        if (_intermediate != '\0') return; // DECSCUSR (" q") and friends: ignored

        switch (final)
        {
            case '@':
            {
                var line = Screen.Lines[CursorRow];
                var n = Math.Min(P(0), Columns - CursorCol);
                Array.Copy(line.Cells, CursorCol, line.Cells, CursorCol + n, Columns - CursorCol - n);
                Array.Fill(line.Cells, Cell.Blank(_attr), CursorCol, n);
                break;
            }
            case 'A':
                MoveTo(CursorRow - P(0), CursorCol, clampToRegion: true);
                break;
            case 'B' or 'e':
                MoveTo(CursorRow + P(0), CursorCol, clampToRegion: true);
                break;
            case 'C' or 'a':
                MoveTo(CursorRow, CursorCol + P(0));
                break;
            case 'D':
                MoveTo(CursorRow, CursorCol - P(0));
                break;
            case 'E':
                MoveTo(CursorRow + P(0), 0, clampToRegion: true);
                break;
            case 'F':
                MoveTo(CursorRow - P(0), 0, clampToRegion: true);
                break;
            case 'G' or '`':
                MoveTo(CursorRow, P(0) - 1);
                break;
            case 'H' or 'f':
                MoveTo((OriginMode ? _top : 0) + P(0) - 1, P(1) - 1, clampToRegion: OriginMode);
                break;
            case 'I':
                TabForward(P(0));
                break;
            case 'J':
                EraseDisplay(P0(0));
                break;
            case 'K':
                EraseLine(P0(0));
                break;
            case 'L':
                if (CursorRow >= _top && CursorRow <= _bottom) Screen.ScrollDown(CursorRow, _bottom, P(0), _attr);
                CursorCol = 0;
                break;
            case 'M':
                if (CursorRow >= _top && CursorRow <= _bottom) ScrollRegionUp(CursorRow, _bottom, P(0), history: false);
                CursorCol = 0;
                break;
            case 'P':
            {
                var line = Screen.Lines[CursorRow];
                var n = Math.Min(P(0), Columns - CursorCol);
                Array.Copy(line.Cells, CursorCol + n, line.Cells, CursorCol, Columns - CursorCol - n);
                Array.Fill(line.Cells, Cell.Blank(_attr), Columns - n, n);
                break;
            }
            case 'S':
                ScrollRegionUp(_top, _bottom, P(0), history: true);
                break;
            case 'T':
                Screen.ScrollDown(_top, _bottom, P(0), _attr);
                break;
            case 'X':
            {
                var n = Math.Min(P(0), Columns - CursorCol);
                Array.Fill(Screen.Lines[CursorRow].Cells, Cell.Blank(_attr), CursorCol, n);
                break;
            }
            case 'Z':
                TabBack(P(0));
                break;
            case 'b':
                for (var i = 0; i < Math.Min(P(0), 4096); i++) Print(_lastPrinted);
                break;
            case 'c':
                Send("\e[?62;22c");
                break;
            case 'd':
                MoveTo((OriginMode ? _top : 0) + P(0) - 1, CursorCol, clampToRegion: OriginMode);
                break;
            case 'g':
                if (P0(0) == 0 && CursorCol < _tabs.Length) _tabs[CursorCol] = false;
                else if (P0(0) == 3) Array.Fill(_tabs, false);
                break;
            case 'h' or 'l':
                foreach (var p in _params)
                {
                    if (p == 4) InsertMode = final == 'h';
                    else if (p == 20) LineFeedNewLine = final == 'h';
                }

                break;
            case 'm':
                Sgr();
                break;
            case 'n':
                if (P0(0) == 5) Send("\e[0n");
                else if (P0(0) == 6) Send($"\e[{CursorRow + 1 - (OriginMode ? _top : 0)};{CursorCol + 1}R");
                break;
            case 'r':
            {
                var top = P(0) - 1;
                var bottom = P(1, Rows) - 1;
                if (top < bottom && bottom < Rows)
                {
                    _top = top;
                    _bottom = bottom;
                    MoveTo(OriginMode ? _top : 0, 0);
                }

                break;
            }
            case 's':
                SaveCursor();
                break;
            case 'u':
                RestoreCursor();
                break;
            case 't':
                if (P0(0) == 18) Send($"\e[8;{Rows};{Columns}t");
                break;
        }
    }

    private void ScrollRegionUp(int top, int bottom, int n, bool history)
    {
        // Only scrolling from the top of the main screen feeds the scrollback.
        if (history && top == 0 && !IsAlternate)
        {
            Screen.ScrollUp(0, bottom, n, _attr);
            return;
        }

        n = Math.Min(n, bottom - top + 1);
        for (var k = 0; k < n; k++)
        {
            var first = Screen.Lines[top];
            for (var i = top; i < bottom; i++) Screen.Lines[i] = Screen.Lines[i + 1];
            first.Clear(_attr);
            Screen.Lines[bottom] = first;
        }
    }

    private void PrivateMode(bool on)
    {
        foreach (var p in _params)
        {
            switch (p)
            {
                case 1:
                    ApplicationCursorKeys = on;
                    break;
                case 5:
                    ReverseVideo = on;
                    break;
                case 6:
                    OriginMode = on;
                    MoveTo(on ? _top : 0, 0);
                    break;
                case 7:
                    AutoWrap = on;
                    break;
                case 25:
                    CursorVisible = on;
                    break;
                case 9 or 1000 or 1002 or 1003:
                    MouseMode = on ? p : 0;
                    break;
                case 1006:
                    MouseSgr = on;
                    break;
                case 47 or 1047:
                    SwitchScreen(on, clear: p == 1047);
                    break;
                case 1048:
                    if (on) SaveCursor();
                    else RestoreCursor();
                    break;
                case 1049:
                    if (on)
                    {
                        SaveCursor();
                        SwitchScreen(true, clear: true);
                    }
                    else
                    {
                        SwitchScreen(false, clear: false);
                        RestoreCursor();
                    }

                    break;
                case 2004:
                    BracketedPaste = on;
                    break;
            }
        }
    }

    private void SwitchScreen(bool alternate, bool clear)
    {
        var target = alternate ? _alt : _main;
        if (target == Screen) return;
        if (target.Columns != Columns || target.Rows != Rows) target.Resize(Columns, Rows, 0);
        if (alternate && clear)
        {
            foreach (var l in _alt.Lines) l.Clear(CellAttr.Default);
        }

        Screen = target;
        _wrapPending = false;
    }

    private void EraseDisplay(int mode)
    {
        var lines = Screen.Lines;
        switch (mode)
        {
            case 0:
                EraseLine(0);
                for (var r = CursorRow + 1; r < Rows; r++) lines[r].Clear(_attr);
                break;
            case 1:
                EraseLine(1);
                for (var r = 0; r < CursorRow; r++) lines[r].Clear(_attr);
                break;
            case 2:
                foreach (var l in lines) l.Clear(_attr);
                break;
            case 3:
                Screen.ClearScrollback();
                break;
        }
    }

    private void EraseLine(int mode)
    {
        var cells = Screen.Lines[CursorRow].Cells;
        var blank = Cell.Blank(_attr);
        switch (mode)
        {
            case 0:
                Array.Fill(cells, blank, CursorCol, Columns - CursorCol);
                Screen.Lines[CursorRow].Wrapped = false;
                break;
            case 1:
                Array.Fill(cells, blank, 0, Math.Min(CursorCol + 1, Columns));
                break;
            case 2:
                Array.Fill(cells, blank);
                Screen.Lines[CursorRow].Wrapped = false;
                break;
        }
    }

    private void MoveTo(int row, int col, bool clampToRegion = false)
    {
        _wrapPending = false;
        var min = clampToRegion && CursorRow >= _top ? _top : 0;
        var max = clampToRegion && CursorRow <= _bottom ? _bottom : Rows - 1;
        CursorRow = Math.Clamp(row, min, max);
        CursorCol = Math.Clamp(col, 0, Columns - 1);
    }

    private void SaveCursor() => _saved = (CursorRow, CursorCol, _attr, OriginMode, _g0Line);

    private void RestoreCursor()
    {
        CursorRow = Math.Min(_saved.Row, Rows - 1);
        CursorCol = Math.Min(_saved.Col, Columns - 1);
        _attr = _saved.Attr;
        OriginMode = _saved.Origin;
        _g0Line = _saved.G0Line;
        _wrapPending = false;
    }

    // ------------------------------------------------------------------ SGR

    private void Sgr()
    {
        if (_params.Count == 0)
        {
            _attr = CellAttr.Default;
            return;
        }

        for (var i = 0; i < _params.Count; i++)
        {
            var p = Math.Max(0, _params[i]);
            var flags = _attr.Flags;
            switch (p)
            {
                case 0:
                    _attr = CellAttr.Default;
                    continue;
                case 1: flags |= CellFlags.Bold; break;
                case 2: flags |= CellFlags.Dim; break;
                case 3: flags |= CellFlags.Italic; break;
                case 4: flags |= CellFlags.Underline; break;
                case 5 or 6: flags |= CellFlags.Blink; break;
                case 7: flags |= CellFlags.Inverse; break;
                case 8: flags |= CellFlags.Hidden; break;
                case 9: flags |= CellFlags.Strike; break;
                case 21 or 22: flags &= ~(CellFlags.Bold | CellFlags.Dim); break;
                case 23: flags &= ~CellFlags.Italic; break;
                case 24: flags &= ~CellFlags.Underline; break;
                case 25: flags &= ~CellFlags.Blink; break;
                case 27: flags &= ~CellFlags.Inverse; break;
                case 28: flags &= ~CellFlags.Hidden; break;
                case 29: flags &= ~CellFlags.Strike; break;
                case >= 30 and <= 37:
                    _attr = _attr with { Fg = p - 30 };
                    continue;
                case 38:
                    _attr = _attr with { Fg = ExtendedColor(ref i) };
                    continue;
                case 39:
                    _attr = _attr with { Fg = TermColor.Default };
                    continue;
                case >= 40 and <= 47:
                    _attr = _attr with { Bg = p - 40 };
                    continue;
                case 48:
                    _attr = _attr with { Bg = ExtendedColor(ref i) };
                    continue;
                case 49:
                    _attr = _attr with { Bg = TermColor.Default };
                    continue;
                case >= 90 and <= 97:
                    _attr = _attr with { Fg = p - 90 + 8 };
                    continue;
                case >= 100 and <= 107:
                    _attr = _attr with { Bg = p - 100 + 8 };
                    continue;
                default:
                    continue;
            }

            _attr = _attr with { Flags = flags };
        }
    }

    private int ExtendedColor(ref int i)
    {
        if (i + 1 >= _params.Count) return TermColor.Default;
        var mode = _params[i + 1];
        if (mode == 5 && i + 2 < _params.Count)
        {
            i += 2;
            return Math.Clamp(_params[i], 0, 255);
        }

        if (mode == 2)
        {
            // 38;2;r;g;b – with colons an optional color-space id may precede r.
            var offset = _paramColon && i + 5 < _params.Count ? 1 : 0;
            if (i + 4 + offset < _params.Count)
            {
                var r = Math.Max(0, _params[i + 2 + offset]);
                var g = Math.Max(0, _params[i + 3 + offset]);
                var b = Math.Max(0, _params[i + 4 + offset]);
                i += 4 + offset;
                return TermColor.Rgb(r, g, b);
            }
        }

        i = _params.Count;
        return TermColor.Default;
    }

    // ------------------------------------------------------------------ OSC

    private void FinishOsc()
    {
        _state = State.Ground;
        var text = _osc.ToString();
        var semi = text.IndexOf(';');
        if (semi <= 0) return;
        if (!int.TryParse(text.AsSpan(0, semi), NumberStyles.Integer, CultureInfo.InvariantCulture, out var cmd)) return;
        if (cmd is 0 or 2)
        {
            Title = text[(semi + 1)..];
            TitleChanged?.Invoke();
        }
    }

    // ------------------------------------------------------------------ reset / resize

    public void Reset()
    {
        _attr = CellAttr.Default;
        SwitchScreen(false, false);
        foreach (var l in _main.Lines) l.Clear(_attr);
        _main.ClearScrollback();
        _top = 0;
        _bottom = Rows - 1;
        CursorRow = CursorCol = 0;
        CursorVisible = true;
        ApplicationCursorKeys = ApplicationKeypad = BracketedPaste = OriginMode = InsertMode = ReverseVideo = false;
        AutoWrap = true;
        MouseMode = 0;
        _g0Line = _g1Line = _shiftOut = false;
        ResetTabs();
    }

    private void SoftReset()
    {
        _attr = CellAttr.Default;
        _top = 0;
        _bottom = Rows - 1;
        CursorVisible = true;
        OriginMode = InsertMode = ApplicationCursorKeys = false;
        AutoWrap = true;
    }

    public void Resize(int columns, int rows)
    {
        columns = Math.Max(2, columns);
        rows = Math.Max(1, rows);
        if (columns == Columns && rows == Rows) return;
        var shift = Screen.Resize(columns, rows, CursorRow);
        var other = Screen == _main ? _alt : _main;
        other.Resize(columns, rows, other == _main ? CursorRow : 0);
        CursorRow = Math.Clamp(CursorRow + shift, 0, rows - 1);
        CursorCol = Math.Clamp(CursorCol, 0, columns - 1);
        _top = 0;
        _bottom = rows - 1;
        _wrapPending = false;
        ResetTabs();
        Version++;
    }

    private void Send(string s) => Response?.Invoke(Encoding.ASCII.GetBytes(s));

    // ------------------------------------------------------------------ text access

    /// <summary>Line by absolute index: 0 … scrollback count − 1 are history, then the screen rows.</summary>
    public TerminalLine LineAt(int absolute)
    {
        var sb = Screen.Scrollback;
        return absolute < sb.Count ? sb[absolute] : Screen.Lines[Math.Clamp(absolute - sb.Count, 0, Rows - 1)];
    }

    public int TotalLines => Screen.Scrollback.Count + Rows;

    /// <summary>Text between two absolute positions (inclusive start, exclusive end column), joining soft-wrapped lines.</summary>
    public string GetText(int startLine, int startCol, int endLine, int endCol)
    {
        var sb = new StringBuilder();
        for (var l = startLine; l <= endLine; l++)
        {
            var line = LineAt(l);
            var from = l == startLine ? startCol : 0;
            var to = l == endLine ? endCol : Columns;
            sb.Append(line.Text(from, to).TrimEnd());
            if (l < endLine && !line.Wrapped) sb.Append(Environment.NewLine);
        }

        return sb.ToString();
    }
}

/// <summary>Display width of code points (East Asian wide, emoji = 2, combining = 0).</summary>
public static class CharWidth
{
    public static int Of(int c)
    {
        if (c < 0x300) return 1;
        if (c is >= 0x300 and <= 0x36F or >= 0x200B and <= 0x200F or >= 0xFE00 and <= 0xFE0F or >= 0x1AB0 and <= 0x1AFF or >= 0x20D0 and <= 0x20FF) return 0;
        if (c is >= 0x1100 and <= 0x115F or >= 0x2E80 and <= 0x303E or >= 0x3041 and <= 0x33FF or >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF or >= 0xA000 and <= 0xA4CF or >= 0xAC00 and <= 0xD7A3 or >= 0xF900 and <= 0xFAFF
            or >= 0xFE30 and <= 0xFE4F or >= 0xFF00 and <= 0xFF60 or >= 0xFFE0 and <= 0xFFE6 or >= 0x1F300 and <= 0x1F64F
            or >= 0x1F900 and <= 0x1F9FF or >= 0x1F680 and <= 0x1F6FF or >= 0x20000 and <= 0x3FFFD) return 2;
        return 1;
    }
}
