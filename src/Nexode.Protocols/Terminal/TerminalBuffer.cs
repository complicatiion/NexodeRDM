namespace Nexode.Protocols.Terminal;

/// <summary>Text attributes of one cell.</summary>
[Flags]
public enum CellFlags : ushort
{
    None = 0,
    Bold = 1,
    Dim = 2,
    Italic = 4,
    Underline = 8,
    Blink = 16,
    Inverse = 32,
    Hidden = 64,
    Strike = 128,
    /// <summary>Right half of a wide character (no own glyph).</summary>
    WideTail = 256
}

/// <summary>
/// Color of a cell: <see cref="Default"/>, 0–255 = palette index, or 0x1RRGGBB = true color.
/// </summary>
public static class TermColor
{
    public const int Default = -1;
    public const int TrueColorFlag = 0x1000000;

    public static int Rgb(int r, int g, int b) => TrueColorFlag | (r & 255) << 16 | (g & 255) << 8 | (b & 255);
    public static bool IsTrueColor(int c) => c >= TrueColorFlag;
}

public readonly record struct CellAttr(int Fg, int Bg, CellFlags Flags)
{
    public static readonly CellAttr Default = new(TermColor.Default, TermColor.Default, CellFlags.None);
}

/// <summary>One screen cell: Unicode code point (0 = empty) and attributes.</summary>
public struct Cell
{
    public int Code;
    public CellAttr Attr;

    public Cell(int code, CellAttr attr)
    {
        Code = code;
        Attr = attr;
    }

    public static Cell Blank(CellAttr attr) => new(0, attr with { Flags = attr.Flags & CellFlags.Inverse });
}

/// <summary>A row of cells; <see cref="Wrapped"/> marks soft line breaks (for copy and reflow).</summary>
public sealed class TerminalLine
{
    public TerminalLine(int columns, CellAttr fill)
    {
        Cells = new Cell[columns];
        Clear(fill);
    }

    public Cell[] Cells { get; private set; }
    public bool Wrapped { get; set; }

    public void Clear(CellAttr fill)
    {
        var blank = Cell.Blank(fill);
        Array.Fill(Cells, blank);
        Wrapped = false;
    }

    public void Resize(int columns, CellAttr fill)
    {
        if (columns == Cells.Length) return;
        var old = Cells;
        Cells = new Cell[columns];
        var n = Math.Min(columns, old.Length);
        Array.Copy(old, Cells, n);
        if (columns > n) Array.Fill(Cells, Cell.Blank(fill), n, columns - n);
    }

    public string Text(int from = 0, int to = int.MaxValue)
    {
        var sb = new System.Text.StringBuilder();
        to = Math.Min(to, Cells.Length);
        for (var i = Math.Max(0, from); i < to; i++)
        {
            var c = Cells[i];
            if ((c.Attr.Flags & CellFlags.WideTail) != 0) continue;
            sb.Append(c.Code == 0 ? " " : char.ConvertFromUtf32(c.Code));
        }

        return sb.ToString();
    }
}

/// <summary>Screen (main or alternate) with optional scrollback.</summary>
public sealed class TerminalScreen
{
    private readonly List<TerminalLine> _scrollback = [];

    public TerminalScreen(int columns, int rows, int scrollbackLimit)
    {
        Columns = columns;
        Rows = rows;
        ScrollbackLimit = scrollbackLimit;
        Lines = new TerminalLine[rows];
        for (var i = 0; i < rows; i++) Lines[i] = new TerminalLine(columns, CellAttr.Default);
    }

    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public int ScrollbackLimit { get; }
    public TerminalLine[] Lines { get; private set; }
    public IReadOnlyList<TerminalLine> Scrollback => _scrollback;

    public void PushScrollback(TerminalLine line)
    {
        if (ScrollbackLimit <= 0) return;
        _scrollback.Add(line);
        if (_scrollback.Count > ScrollbackLimit) _scrollback.RemoveRange(0, _scrollback.Count - ScrollbackLimit);
    }

    public void ClearScrollback() => _scrollback.Clear();

    /// <summary>Scrolls lines [top, bottom] up by n; lines leaving the top of the full screen go to the scrollback.</summary>
    public void ScrollUp(int top, int bottom, int n, CellAttr fill)
    {
        n = Math.Min(n, bottom - top + 1);
        for (var k = 0; k < n; k++)
        {
            var first = Lines[top];
            if (top == 0) PushScrollback(first);
            for (var i = top; i < bottom; i++) Lines[i] = Lines[i + 1];
            Lines[bottom] = top == 0 ? new TerminalLine(Columns, fill) : Recycle(first, fill);
        }
    }

    public void ScrollDown(int top, int bottom, int n, CellAttr fill)
    {
        n = Math.Min(n, bottom - top + 1);
        for (var k = 0; k < n; k++)
        {
            var last = Lines[bottom];
            for (var i = bottom; i > top; i--) Lines[i] = Lines[i - 1];
            Lines[top] = Recycle(last, fill);
        }
    }

    private TerminalLine Recycle(TerminalLine line, CellAttr fill)
    {
        line.Resize(Columns, fill);
        line.Clear(fill);
        return line;
    }

    /// <summary>Resizes; when rows shrink, lines above the cursor move into the scrollback. Returns the row shift.</summary>
    public int Resize(int columns, int rows, int cursorRow)
    {
        var shift = 0;
        var lines = Lines.ToList();
        foreach (var l in lines) l.Resize(columns, CellAttr.Default);
        foreach (var l in _scrollback) l.Resize(columns, CellAttr.Default);

        if (rows < lines.Count)
        {
            // Drop empty lines below the cursor first, then push the top into the scrollback.
            while (lines.Count > rows && lines.Count - 1 > cursorRow && IsBlank(lines[^1])) lines.RemoveAt(lines.Count - 1);
            while (lines.Count > rows)
            {
                PushScrollback(lines[0]);
                lines.RemoveAt(0);
                shift--;
            }
        }
        else
        {
            // Pull lines back from the scrollback when growing.
            while (lines.Count < rows && _scrollback.Count > 0 && cursorRow + shift < rows - 1)
            {
                lines.Insert(0, _scrollback[^1]);
                _scrollback.RemoveAt(_scrollback.Count - 1);
                shift++;
            }

            while (lines.Count < rows) lines.Add(new TerminalLine(columns, CellAttr.Default));
        }

        Columns = columns;
        Rows = rows;
        Lines = [.. lines];
        return shift;
    }

    private static bool IsBlank(TerminalLine l) => l.Cells.All(c => c.Code == 0 && c.Attr.Bg == TermColor.Default);
}
