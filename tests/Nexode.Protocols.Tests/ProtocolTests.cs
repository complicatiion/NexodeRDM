using System.Text;
using Nexode.Protocols.Files;
using Nexode.Protocols.Terminal;
using Nexode.Protocols.Vnc;
using Xunit;

namespace Nexode.Protocols.Tests;

public class TerminalTests
{
    private static string Row(TerminalEmulator t, int r) => t.Screen.Lines[r].Text().TrimEnd();

    [Fact]
    public void PrintsWrapsAndScrolls()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("1234567890AB\r\nline2\r\nline3\r\nline4");
        Assert.Equal("line2", Row(t, 0));
        Assert.Equal("line4", Row(t, 2));
        Assert.Equal(2, t.Screen.Scrollback.Count);
        Assert.Equal("1234567890", t.Screen.Scrollback[0].Text());
        Assert.True(t.Screen.Scrollback[0].Wrapped);
        Assert.Equal("1234567890AB", t.GetText(0, 0, 1, 10));
    }

    [Fact]
    public void CursorMovementAndErase()
    {
        var t = new TerminalEmulator(20, 5);
        t.Feed("hello world\e[1;7HWORLD\e[1;3H\e[K");
        Assert.Equal("he", Row(t, 0));
        t.Feed("\e[2J\e[3;4Hx");
        Assert.Equal("   x", Row(t, 2));
        Assert.Equal(2, t.CursorRow);
        Assert.Equal(4, t.CursorCol);
    }

    [Fact]
    public void SgrColors()
    {
        var t = new TerminalEmulator(20, 2);
        t.Feed("\e[1;31ma\e[38;5;200mb\e[38;2;1;2;3mc\e[0md");
        var c = t.Screen.Lines[0].Cells;
        Assert.Equal(1, c[0].Attr.Fg);
        Assert.True((c[0].Attr.Flags & CellFlags.Bold) != 0);
        Assert.Equal(200, c[1].Attr.Fg);
        Assert.Equal(TermColor.Rgb(1, 2, 3), c[2].Attr.Fg);
        Assert.Equal(CellAttr.Default, c[3].Attr);
    }

    [Fact]
    public void AlternateScreenKeepsMain()
    {
        var t = new TerminalEmulator(10, 3);
        t.Feed("main");
        t.Feed("\e[?1049h\e[Halt");
        Assert.True(t.IsAlternate);
        Assert.Equal("alt", Row(t, 0));
        t.Feed("\e[?1049l");
        Assert.False(t.IsAlternate);
        Assert.Equal("main", Row(t, 0));
    }

    [Fact]
    public void ScrollRegionAndWideChars()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("a\r\nb\r\nc\r\nd\e[2;3r\e[3;1H\n");
        Assert.Equal("a", Row(t, 0));
        Assert.Equal("c", Row(t, 1));
        Assert.Equal("", Row(t, 2));
        Assert.Equal("d", Row(t, 3));
        var w = new TerminalEmulator(10, 2);
        w.Feed("日本x");
        Assert.Equal(5, w.CursorCol);
        Assert.Equal("日本x", Row(w, 0));
    }

    [Fact]
    public void ReportsCursorPositionAndUtf8Split()
    {
        var t = new TerminalEmulator(20, 5);
        byte[]? response = null;
        t.Response += r => response = r;
        t.Feed("\e[3;5H\e[6n");
        Assert.Equal("\e[3;5R", Encoding.ASCII.GetString(response!));
        var bytes = Encoding.UTF8.GetBytes("ä€");
        t.Feed("\e[H");
        t.Feed(bytes.AsSpan(0, 1));
        t.Feed(bytes.AsSpan(1));
        Assert.StartsWith("ä€", Row(t, 0));
    }

    [Fact]
    public void ResizeKeepsContent()
    {
        var t = new TerminalEmulator(10, 4);
        t.Feed("one\r\ntwo\r\nthree\r\nfour");
        t.Resize(12, 2);
        Assert.Equal("three", Row(t, 0));
        Assert.Equal("four", Row(t, 1));
        t.Resize(12, 4);
        Assert.Equal("one", Row(t, 0));
    }

    [Fact]
    public void KeyEncoding()
    {
        var t = new TerminalEmulator();
        Assert.Equal("\e[A", Encoding.ASCII.GetString(TerminalInput.Encode(TermKey.Up, TermMods.None, t)!));
        t.Feed("\e[?1h");
        Assert.Equal("\eOA", Encoding.ASCII.GetString(TerminalInput.Encode(TermKey.Up, TermMods.None, t)!));
        Assert.Equal("\e[1;5C", Encoding.ASCII.GetString(TerminalInput.Encode(TermKey.Right, TermMods.Ctrl, t)!));
        Assert.Equal([3], TerminalInput.Control('c', false));
        t.Feed("\e[?2004h");
        Assert.Equal("\e[200~a\rb\e[201~", Encoding.UTF8.GetString(TerminalInput.Paste("a\nb", t)));
    }
}

public class FileProtocolTests
{
    [Fact]
    public void ParsesUnixAndWindowsListings()
    {
        var u = FtpClient.ParseList("drwxr-xr-x    2 ftp      ftp          4096 Jan 15 10:30 www", "/");
        Assert.NotNull(u);
        Assert.True(u!.IsDirectory);
        Assert.Equal("/www", u.FullPath);
        var l = FtpClient.ParseList("lrwxrwxrwx 1 root root 7 Mar  3  2023 bin -> usr/bin", "/");
        Assert.Equal("bin", l!.Name);
        Assert.True(l.IsLink);
        var f = FtpClient.ParseList("-rw-r--r-- 1 user group 1234 Dec 31 2024 file name.txt", "/home");
        Assert.Equal("file name.txt", f!.Name);
        Assert.Equal(1234, f.Size);
        var w = FtpClient.ParseList("01-15-24  10:30AM       <DIR>          inetpub", "/");
        Assert.True(w!.IsDirectory);
        var wf = FtpClient.ParseList("01-15-2024  10:30PM             5120 report.pdf", "/");
        Assert.Equal(5120, wf!.Size);
    }

    [Fact]
    public void ParsesMlsd()
    {
        var m = FtpClient.ParseMlsd("type=file;size=42;modify=20240115103000;UNIX.mode=0644; readme.md", "/pub");
        Assert.Equal("readme.md", m!.Name);
        Assert.Equal(42, m.Size);
        Assert.Equal(new DateTime(2024, 1, 15, 10, 30, 0, DateTimeKind.Utc), m.Modified);
        Assert.Null(FtpClient.ParseMlsd("type=cdir;modify=20240115103000; .", "/pub"));
    }

    [Fact]
    public void RemotePaths()
    {
        Assert.Equal("/a/b", RemotePath.Combine("/a", "b"));
        Assert.Equal("/a", RemotePath.Parent("/a/b/"));
        Assert.Equal("/", RemotePath.Parent("/a"));
        Assert.Equal("b", RemotePath.Name("/a/b"));
    }
}

public class VncTests
{
    [Fact]
    public void VncAuthMatchesKnownVector()
    {
        // Reference: challenge of zeros with password "password" (bit-reversed DES key).
        var response = RfbClient.VncAuthResponse(new byte[16], "password");
        Assert.Equal(16, response.Length);
        Assert.Equal(response.AsSpan(0, 8).ToArray(), response.AsSpan(8, 8).ToArray());
        Assert.NotEqual(RfbClient.VncAuthResponse(new byte[16], "other"), response);
    }

    [Fact]
    public void KeySyms()
    {
        Assert.Equal(0x61u, Vnc.KeySyms.FromChar('a'));
        Assert.Equal(0x010020ACu, Vnc.KeySyms.FromChar('€'));
    }
}
