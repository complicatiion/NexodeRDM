using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace Nexode.App.Sessions;

/// <summary>
/// The Microsoft RDP client control (mstscax.dll, part of Windows) hosted in a plain Win32 child window via ATL
/// (atl.dll, also part of Windows) and driven by late-bound COM – no WinForms or interop assemblies needed.
/// The window can be re-parented, so a running session survives moving its tab to another window.
/// </summary>
[SupportedOSPlatform("windows")]
internal sealed partial class RdpActiveX : IDisposable
{
    // Newest first; the first class that loads wins.
    private static readonly string[] ClassIds =
    [
        "{8B918B82-7985-4C24-89DF-C33AD2BBFBCD}", // MsRdpClient9NotSafeForScripting
        "{A3BC03A0-041D-42E3-AD22-882B7865C9C5}", // MsRdpClient8NotSafeForScripting
        "{54D38BF7-B1EF-4479-9674-1BD6EA465258}", // MsRdpClient7NotSafeForScripting
        "{D2EA46A7-C2BF-426B-AF24-E19C44456399}", // MsRdpClient6NotSafeForScripting
        "MsTscAx.MsTscAx"
    ];

    private static readonly Guid EventsIid = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6"); // IMsTscAxEvents
    private static bool _atlReady;
    private static nint _parking;

    private object? _control;

    public nint Hwnd { get; private set; }
    public string? ClassId { get; private set; }
    public bool IsCreated => _control is not null;

    /// <summary>0 = disconnected, 1 = connected, 2 = connecting.</summary>
    public int ConnectedState
    {
        get
        {
            try
            {
                return Convert.ToInt32(Get(_control, "Connected"), System.Globalization.CultureInfo.InvariantCulture);
            }
            catch (Exception ex) when (ex is COMException or TargetInvocationException or InvalidCastException or MissingMethodException)
            {
                return 0;
            }
        }
    }

    public event Action? Connected;
    public event Action<int>? Disconnected;

    /// <summary>Creates the control as a child of <paramref name="parent"/>; throws when no RDP client class is available.</summary>
    public void Create(nint parent, int width, int height)
    {
        if (!_atlReady)
        {
            if (!AtlAxWinInit()) throw new InvalidOperationException("ATL could not be initialised (atl.dll).");
            _atlReady = true;
        }

        foreach (var clsid in ClassIds)
        {
            var hwnd = CreateWindowExW(0, "AtlAxWin", clsid, WsChild | WsVisible | WsClipChildren | WsClipSiblings, 0, 0, Math.Max(1, width), Math.Max(1, height),
                parent, 0, GetModuleHandleW(null), 0);
            if (hwnd == 0) continue;
            if (AtlAxGetControl(hwnd, out var unk) == 0 && unk != 0)
            {
                try
                {
                    _control = Marshal.GetObjectForIUnknown(unk);
                }
                finally
                {
                    Marshal.Release(unk);
                }

                Hwnd = hwnd;
                ClassId = clsid;
                HookEvents();
                return;
            }

            DestroyWindow(hwnd);
        }

        throw new InvalidOperationException("The Microsoft Remote Desktop client (mstscax.dll) is not available.");
    }

    private void HookEvents()
    {
        try
        {
            ComEventsHelper.Combine(_control!, EventsIid, 2, new Action(() => Connected?.Invoke()));
            ComEventsHelper.Combine(_control!, EventsIid, 4, new Action<int>(reason => Disconnected?.Invoke(reason)));
        }
        catch (Exception ex) when (ex is COMException or InvalidCastException or ArgumentException or NotSupportedException)
        {
            // No events: the session polls the connection state instead.
        }
    }

    // ------------------------------------------------------------------ configuration

    public sealed record Options(string Server, int Port, string? User, string? Domain, string? Password, int Width, int Height, int ColorDepth,
        bool SmartSizing, bool Admin, bool Clipboard, bool Drives, bool Printers, int Audio, string? Gateway,
        bool SmartCards = false, bool GatewayBypass = false, bool GatewaySameCreds = true, string? HyperVId = null);

    public void Configure(Options o)
    {
        var c = _control ?? throw new InvalidOperationException();
        Set(c, "Server", o.Server);
        if (!string.IsNullOrEmpty(o.User)) Set(c, "UserName", o.User);
        if (!string.IsNullOrEmpty(o.Domain)) Set(c, "Domain", o.Domain);
        TrySet(c, "DesktopWidth", o.Width);
        TrySet(c, "DesktopHeight", o.Height);
        TrySet(c, "ColorDepth", o.ColorDepth);

        var adv = TryGet(c, "AdvancedSettings9") ?? TryGet(c, "AdvancedSettings8") ?? TryGet(c, "AdvancedSettings7") ?? TryGet(c, "AdvancedSettings2");
        if (adv is not null)
        {
            TrySet(adv, "RDPPort", o.Port);
            TrySet(adv, "EnableCredSspSupport", true);
            TrySet(adv, "SmartSizing", o.SmartSizing);
            TrySet(adv, "AuthenticationLevel", 2u);
            TrySet(adv, "RedirectClipboard", o.Clipboard);
            TrySet(adv, "RedirectDrives", o.Drives);
            TrySet(adv, "RedirectPrinters", o.Printers);
            TrySet(adv, "RedirectSmartCards", o.SmartCards);
            TrySet(adv, "AudioRedirectionMode", (uint)o.Audio);
            TrySet(adv, "ConnectToAdministerServer", o.Admin);
            TrySet(adv, "ConnectToServerConsole", o.Admin);
            TrySet(adv, "EnableAutoReconnect", true);
            TrySet(adv, "keepAliveInterval", 60000);
            TrySet(adv, "BitmapPersistence", 1);
            TrySet(adv, "GrabFocusOnConnect", true);
            if (!string.IsNullOrEmpty(o.HyperVId))
            {
                // Hyper-V console (VMConnect): the host's VM bus port with the VM id as pre-connection blob.
                TrySet(adv, "PCB", o.HyperVId);
                TrySet(adv, "NegotiateSecurityLayer", false);
                TrySet(adv, "AuthenticationServiceClass", "Microsoft Virtual Console Service");
                TrySet(adv, "ConnectToAdministerServer", false);
            }
        }

        if (!string.IsNullOrEmpty(o.Gateway) && TryGet(c, "TransportSettings2") is { } ts)
        {
            TrySet(ts, "GatewayHostname", o.Gateway);
            TrySet(ts, "GatewayUsageMethod", o.GatewayBypass ? 2u : 1u);
            TrySet(ts, "GatewayProfileUsageMethod", 1u);
            TrySet(ts, "GatewayCredsSource", 4u);
            TrySet(ts, "GatewayCredSharing", o.GatewaySameCreds ? 1u : 0u);
        }

        if (!string.IsNullOrEmpty(o.Password))
        {
            try
            {
                ((IMsTscNonScriptable)c).SetClearTextPassword(o.Password);
            }
            catch (Exception ex) when (ex is InvalidCastException or COMException)
            {
                // Safe-for-scripting fallback class: the server will ask for the password.
            }
        }
    }

    public void Connect() => Invoke(_control, "Connect");

    public void Disconnect()
    {
        try
        {
            if (ConnectedState != 0) Invoke(_control, "Disconnect");
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException)
        {
        }
    }

    /// <summary>Text of a disconnect reason (empty for a normal logoff).</summary>
    public string DescribeDisconnect(int reason)
    {
        if (reason is 0 or 1 or 2 or 3) return "";
        try
        {
            var extended = Convert.ToUInt32(TryGet(_control!, "ExtendedDisconnectReason") ?? 0, System.Globalization.CultureInfo.InvariantCulture);
            return Invoke(_control, "GetErrorDescription", (uint)reason, extended) as string ?? $"Disconnected (code {reason}).";
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException)
        {
            return $"Disconnected (code {reason}).";
        }
    }

    /// <summary>Dynamic resolution (RDP 8.1+): the remote desktop follows the tab size.</summary>
    public void UpdateResolution(int width, int height)
    {
        if (_control is null || ConnectedState != 1) return;
        try
        {
            Invoke(_control, "UpdateSessionDisplaySettings", (uint)width, (uint)height, (uint)width, (uint)height, 0u, 100u, 100u);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException)
        {
        }
    }

    // ------------------------------------------------------------------ window parking

    /// <summary>Moves the session window into a hidden parent (tab not visible) or into a new host window.</summary>
    public void Reparent(nint parent)
    {
        if (Hwnd == 0) return;
        if (parent == 0)
        {
            if (_parking == 0) _parking = CreateWindowExW(0, "Static", "NexodeRdpParking", 0, 0, 0, 1, 1, HwndMessage, 0, GetModuleHandleW(null), 0);
            parent = _parking;
        }

        SetParent(Hwnd, parent);
        ShowWindow(Hwnd, parent == _parking ? 0 : 5);
    }

    public void Resize(int width, int height)
    {
        if (Hwnd != 0) MoveWindow(Hwnd, 0, 0, Math.Max(1, width), Math.Max(1, height), true);
    }

    public void Focus()
    {
        if (Hwnd != 0) SetFocus(Hwnd);
    }

    public void Dispose()
    {
        Disconnect();
        if (_control is not null)
        {
            try { Marshal.FinalReleaseComObject(_control); } catch (ArgumentException) { }
            _control = null;
        }

        if (Hwnd != 0)
        {
            DestroyWindow(Hwnd);
            Hwnd = 0;
        }
    }

    // ------------------------------------------------------------------ late binding helpers

    private static object? Get(object? target, string name) =>
        target?.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null, System.Globalization.CultureInfo.InvariantCulture);

    private static object? TryGet(object target, string name)
    {
        try
        {
            return Get(target, name);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException)
        {
            return null;
        }
    }

    private static void Set(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value], System.Globalization.CultureInfo.InvariantCulture);

    private static void TrySet(object target, string name, object value)
    {
        try
        {
            Set(target, name, value);
        }
        catch (Exception ex) when (ex is COMException or TargetInvocationException or MissingMethodException or ArgumentException)
        {
        }
    }

    private static object? Invoke(object? target, string name, params object[] args) =>
        target?.GetType().InvokeMember(name, BindingFlags.InvokeMethod, null, target, args, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Non-scriptable interface of the RDP control: only the password setter is used.</summary>
    [ComImport]
    [Guid("C1E6743A-41C1-4A74-832A-0DD06C1C7A0E")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMsTscNonScriptable
    {
        void SetClearTextPassword([MarshalAs(UnmanagedType.BStr)] string password);
    }

    // ------------------------------------------------------------------ Win32

    private const int WsChild = 0x40000000, WsVisible = 0x10000000, WsClipChildren = 0x02000000, WsClipSiblings = 0x04000000;
    private static readonly nint HwndMessage = -3;

    [LibraryImport("atl.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AtlAxWinInit();

    [LibraryImport("atl.dll")]
    private static partial int AtlAxGetControl(nint hwnd, out nint unknown);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint CreateWindowExW(int exStyle, string className, string windowName, int style, int x, int y, int width, int height,
        nint parent, nint menu, nint instance, nint param);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint SetParent(nint child, nint parent);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int cmd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MoveWindow(nint hwnd, int x, int y, int width, int height, [MarshalAs(UnmanagedType.Bool)] bool repaint);

    [LibraryImport("user32.dll")]
    private static partial nint SetFocus(nint hwnd);

    [LibraryImport("kernel32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint GetModuleHandleW(string? name);
}
