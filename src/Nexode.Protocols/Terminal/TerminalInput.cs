using System.Text;

namespace Nexode.Protocols.Terminal;

/// <summary>Special keys the terminal translates into escape sequences (UI-toolkit independent).</summary>
public enum TermKey
{
    None, Enter, Backspace, Tab, Escape, Up, Down, Left, Right, Home, End, PageUp, PageDown, Insert, Delete,
    F1, F2, F3, F4, F5, F6, F7, F8, F9, F10, F11, F12
}

[Flags]
public enum TermMods
{
    None = 0,
    Shift = 1,
    Alt = 2,
    Ctrl = 4
}

/// <summary>Encodes keyboard input like xterm.</summary>
public static class TerminalInput
{
    public static byte[]? Encode(TermKey key, TermMods mods, TerminalEmulator term)
    {
        var mod = 1 + ((mods & TermMods.Shift) != 0 ? 1 : 0) + ((mods & TermMods.Alt) != 0 ? 2 : 0) + ((mods & TermMods.Ctrl) != 0 ? 4 : 0);
        string? s = key switch
        {
            TermKey.Enter => term.LineFeedNewLine ? "\r\n" : "\r",
            TermKey.Backspace => (mods & TermMods.Ctrl) != 0 ? "\b" : "\x7f",
            TermKey.Tab => (mods & TermMods.Shift) != 0 ? "\e[Z" : "\t",
            TermKey.Escape => "\e",
            TermKey.Up => Cursor('A', mod, term),
            TermKey.Down => Cursor('B', mod, term),
            TermKey.Right => Cursor('C', mod, term),
            TermKey.Left => Cursor('D', mod, term),
            TermKey.Home => Cursor('H', mod, term),
            TermKey.End => Cursor('F', mod, term),
            TermKey.Insert => Tilde(2, mod),
            TermKey.Delete => Tilde(3, mod),
            TermKey.PageUp => Tilde(5, mod),
            TermKey.PageDown => Tilde(6, mod),
            TermKey.F1 => Ss3('P', mod),
            TermKey.F2 => Ss3('Q', mod),
            TermKey.F3 => Ss3('R', mod),
            TermKey.F4 => Ss3('S', mod),
            TermKey.F5 => Tilde(15, mod),
            TermKey.F6 => Tilde(17, mod),
            TermKey.F7 => Tilde(18, mod),
            TermKey.F8 => Tilde(19, mod),
            TermKey.F9 => Tilde(20, mod),
            TermKey.F10 => Tilde(21, mod),
            TermKey.F11 => Tilde(23, mod),
            TermKey.F12 => Tilde(24, mod),
            _ => null
        };
        if (s is null) return null;
        if ((mods & TermMods.Alt) != 0 && key is TermKey.Enter or TermKey.Backspace or TermKey.Escape) s = "\e" + s;
        return Encoding.ASCII.GetBytes(s);
    }

    private static string Cursor(char c, int mod, TerminalEmulator term) =>
        mod > 1 ? $"\e[1;{mod}{c}" : term.ApplicationCursorKeys ? $"\eO{c}" : $"\e[{c}";

    private static string Tilde(int n, int mod) => mod > 1 ? $"\e[{n};{mod}~" : $"\e[{n}~";

    private static string Ss3(char c, int mod) => mod > 1 ? $"\e[1;{mod}{c}" : $"\eO{c}";

    /// <summary>Ctrl+letter and friends as control bytes; null when the combination produces no control code.</summary>
    public static byte[]? Control(char c, bool alt)
    {
        byte? b = c switch
        {
            >= 'a' and <= 'z' => (byte)(c - 'a' + 1),
            >= 'A' and <= 'Z' => (byte)(c - 'A' + 1),
            '@' or ' ' or '2' => 0,
            '[' or '3' => 27,
            '\\' or '4' => 28,
            ']' or '5' => 29,
            '^' or '6' => 30,
            '_' or '-' or '7' => 31,
            '8' or '?' => 127,
            _ => null
        };
        if (b is null) return null;
        return alt ? [0x1B, b.Value] : [b.Value];
    }

    /// <summary>Text typed by the user (Alt prefixes ESC).</summary>
    public static byte[] Text(string text, bool alt)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        return alt ? [0x1B, .. bytes] : bytes;
    }

    /// <summary>Pasted text: line breaks become CR; wrapped in bracketed-paste markers when the app asked for it.</summary>
    public static byte[] Paste(string text, TerminalEmulator term)
    {
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (term.BracketedPaste) text = "\e[200~" + text.Replace("\e[201~", "") + "\e[201~";
        return Encoding.UTF8.GetBytes(text);
    }

    /// <summary>Mouse report (button 0–2, 64/65 = wheel); null when the app did not enable mouse tracking.</summary>
    public static byte[]? Mouse(TerminalEmulator term, int button, bool press, int col, int row, TermMods mods, bool motion = false)
    {
        if (term.MouseMode == 0) return null;
        if (motion && term.MouseMode is not (1002 or 1003)) return null;
        var cb = button + ((mods & TermMods.Shift) != 0 ? 4 : 0) + ((mods & TermMods.Alt) != 0 ? 8 : 0) + ((mods & TermMods.Ctrl) != 0 ? 16 : 0) + (motion ? 32 : 0);
        if (term.MouseSgr) return Encoding.ASCII.GetBytes($"\e[<{cb};{col + 1};{row + 1}{(press ? 'M' : 'm')}");
        if (!press) cb = 3;
        if (col > 222 || row > 222) return null;
        return [0x1B, (byte)'[', (byte)'M', (byte)(32 + cb), (byte)(33 + col), (byte)(33 + row)];
    }
}
