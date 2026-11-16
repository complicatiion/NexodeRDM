using System.Runtime.InteropServices;

namespace Nexode.Platform;

/// <summary>
/// Notification-area icon for Windows (Shell_NotifyIcon on a message-only window). Unlike the cross-platform tray icon it
/// reports right clicks, so the app can show its own themed menu. Must be created on the UI thread (its message loop
/// dispatches the icon's messages).
/// </summary>
public sealed unsafe partial class WinTrayIcon : IDisposable
{
    private const uint WmApp = 0x8000;
    private const uint CallbackMessage = WmApp + 0x31;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonUp = 0x0205;
    private const uint WmContextMenu = 0x007B;
    private const uint NimAdd = 0, NimModify = 1, NimDelete = 2;
    private const uint NifMessage = 1, NifIcon = 2, NifTip = 4, NifShowTip = 0x80;
    private static readonly nint HwndMessage = -3;

    private static readonly WndProc Proc = WindowProc;
    private static readonly Dictionary<nint, WinTrayIcon> Instances = [];
    private static uint _taskbarCreated;
    private static bool _classRegistered;

    private readonly nint _hwnd;
    private nint _icon;
    private string _tip;
    private bool _added;

    /// <summary>Left click (open the app).</summary>
    public event Action? Clicked;
    /// <summary>Right click with the cursor position in physical screen pixels (show the menu).</summary>
    public event Action<int, int>? MenuRequested;

    public WinTrayIcon(byte[] icoFile, string tip)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        const string className = "NexodeTrayWindow";
        var module = GetModuleHandleW(null);
        fixed (char* name = className)
        {
            if (!_classRegistered)
            {
                var wc = new WndClassEx
                {
                    cbSize = (uint)sizeof(WndClassEx),
                    lpfnWndProc = Marshal.GetFunctionPointerForDelegate(Proc),
                    hInstance = module,
                    lpszClassName = (nint)name
                };
                if (RegisterClassExW(ref wc) == 0) throw new InvalidOperationException("RegisterClassEx failed: " + Marshal.GetLastPInvokeError());
                _classRegistered = true;
                _taskbarCreated = RegisterWindowMessageW("TaskbarCreated");
            }

            _hwnd = CreateWindowExW(0, (nint)name, null, 0, 0, 0, 0, 0, HwndMessage, 0, module, 0);
        }

        if (_hwnd == 0) throw new InvalidOperationException("CreateWindowEx failed: " + Marshal.GetLastPInvokeError());
        Instances[_hwnd] = this;
        _tip = tip;
        _icon = LoadIcon(icoFile, GetSystemMetrics(49 /* SM_CXSMICON */));
        Add();
    }

    public void SetTip(string tip)
    {
        _tip = tip;
        if (_added) Notify(NimModify);
    }

    private void Add() => _added = Notify(NimAdd);

    private bool Notify(uint message)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NifMessage | NifIcon | NifTip | NifShowTip,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon
        };
        var tip = _tip.Length > 127 ? _tip[..127] : _tip;
        for (var i = 0; i < tip.Length; i++) data.szTip[i] = tip[i];
        return Shell_NotifyIconW(message, ref data);
    }

    /// <summary>Brings the hidden window to the foreground, so a menu shown next closes when the user clicks elsewhere.</summary>
    public void Foreground() => SetForegroundWindow(_hwnd);

    public void Dispose()
    {
        if (_added)
        {
            Notify(NimDelete);
            _added = false;
        }

        if (_icon != 0)
        {
            DestroyIcon(_icon);
            _icon = 0;
        }

        Instances.Remove(_hwnd);
        DestroyWindow(_hwnd);
    }

    private static nint WindowProc(nint hwnd, uint msg, nint wParam, nint lParam)
    {
        if (Instances.TryGetValue(hwnd, out var icon))
        {
            if (msg == CallbackMessage)
            {
                switch ((uint)(lParam & 0xFFFF))
                {
                    case WmLButtonUp or WmLButtonDblClk:
                        icon.Clicked?.Invoke();
                        break;
                    case WmRButtonUp or WmContextMenu:
                        GetCursorPos(out var pt);
                        icon.MenuRequested?.Invoke(pt.X, pt.Y);
                        break;
                }

                return 0;
            }

            if (msg == _taskbarCreated && _taskbarCreated != 0)
            {
                // Explorer restarted: the icon has to be added again.
                icon.Add();
                return 0;
            }
        }

        return DefWindowProcW(hwnd, msg, wParam, lParam);
    }

    /// <summary>Picks the image closest to <paramref name="size"/> from an .ico file and turns it into an HICON.</summary>
    private static nint LoadIcon(byte[] ico, int size)
    {
        if (ico.Length < 6) return 0;
        var count = BitConverter.ToUInt16(ico, 4);
        int bestOffset = -1, bestLength = 0, bestDiff = int.MaxValue;
        for (var i = 0; i < count; i++)
        {
            var e = 6 + i * 16;
            if (e + 16 > ico.Length) break;
            var w = ico[e] == 0 ? 256 : ico[e];
            var length = BitConverter.ToInt32(ico, e + 8);
            var offset = BitConverter.ToInt32(ico, e + 12);
            var diff = w >= size ? w - size : (size - w) * 4;
            if (diff < bestDiff && offset > 0 && offset + length <= ico.Length)
            {
                bestDiff = diff;
                bestOffset = offset;
                bestLength = length;
            }
        }

        if (bestOffset < 0) return 0;
        fixed (byte* p = &ico[bestOffset])
        {
            return CreateIconFromResourceEx(p, (uint)bestLength, true, 0x00030000, size, size, 0);
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WndProc(nint hwnd, uint msg, nint wParam, nint lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public nint lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public nint hInstance;
        public nint hIcon;
        public nint hCursor;
        public nint hbrBackground;
        public nint lpszMenuName;
        public nint lpszClassName;
        public nint hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    private static partial ushort RegisterClassExW(ref WndClassEx wc);

    [LibraryImport("user32.dll", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint CreateWindowExW(uint exStyle, nint className, string? windowName, uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    private static partial nint DefWindowProcW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int index);

    [LibraryImport("user32.dll")]
    private static partial nint CreateIconFromResourceEx(byte* bits, uint size, [MarshalAs(UnmanagedType.Bool)] bool icon, uint version, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyIcon(nint icon);

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, ref NotifyIconData data);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
