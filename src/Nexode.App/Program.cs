using Avalonia;
using Nexode.App.Services;
using Nexode.Platform;

namespace Nexode.App;

internal static class Program
{
    internal static string[] StartArgs { get; private set; } = [];

    [STAThread]
    public static int Main(string[] args)
    {
        AppReset.WaitForPrevious(args);
        args = [.. args.Where(a => !a.StartsWith("--wait-pid=", StringComparison.Ordinal))];
        StartArgs = args;

        // One process per user: a second start hands its arguments (files, --new-window) over and exits.
        var instance = new SingleInstance("Nexode");
        if (!args.Contains("--new-instance") && instance.TryForward(args))
        {
            instance.Dispose();
            return 0;
        }

        AppHost.Instance = instance;
        try
        {
            return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnLastWindowClose);
        }
        finally
        {
            instance.Dispose();
        }
    }

    // Also used by the Avalonia designer.
    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
