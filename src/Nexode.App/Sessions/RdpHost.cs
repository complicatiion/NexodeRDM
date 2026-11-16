using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Nexode.App.ViewModels.Sessions;

namespace Nexode.App.Sessions;

/// <summary>Native host for the RDP ActiveX window; on destroy the window is parked, not destroyed (session keeps running).</summary>
[SupportedOSPlatform("windows")]
public sealed class RdpHost(RdpTabViewModel session) : NativeControlHost
{
    protected override IPlatformHandle CreateNativeControlCore(IPlatformHandle parent)
    {
        try
        {
            var control = session.EnsureControl(parent.Handle);
            return new PlatformHandle(control.Hwnd, "HWND");
        }
        catch (InvalidOperationException ex)
        {
            session.ReportHostError(ex.Message);
            return base.CreateNativeControlCore(parent);
        }
    }

    protected override void DestroyNativeControlCore(IPlatformHandle control)
    {
        if (session.Control is { } c && c.Hwnd == control.Handle)
        {
            c.Reparent(0);
            return;
        }

        base.DestroyNativeControlCore(control);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        var scale = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1;
        session.OnHostResized((int)(e.NewSize.Width * scale), (int)(e.NewSize.Height * scale));
    }

    protected override void OnGotFocus(Avalonia.Input.FocusChangedEventArgs e)
    {
        base.OnGotFocus(e);
        session.Control?.Focus();
    }
}
