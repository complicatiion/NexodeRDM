using CommunityToolkit.Mvvm.Input;
using Nexode.App.Localization;
using Nexode.App.Services;
using Nexode.Platform;

namespace Nexode.App.ViewModels;

public sealed record LicenseItem(string Name, string License, string Path);

public sealed partial class AboutTabViewModel : TabViewModel
{
    public AboutTabViewModel()
    {
        Icon = "info";
        Refresh();
    }

    public override string Key => "about";
    public override TabViewModel Recreate() => new AboutTabViewModel();

    public string Product => AppHost.ProductName;
    public string Version => Loc.F("About.Version", AppHost.Version);
    public string Runtime => $".NET {Environment.Version} · {System.Runtime.InteropServices.RuntimeInformation.OSDescription} · {System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}";
    public string DataPath => AppPaths.DataDirectory;
    public bool IsPortable => AppPaths.IsPortable;

    public IReadOnlyList<LicenseItem> Licenses { get; } =
    [
        new("Nexode RDM", "MIT", "LICENSE.md"),
        new(".NET", "MIT", "third-party/DOTNET.md"),
        new("Avalonia UI (incl. WebView)", "MIT", "third-party/AVALONIA.md"),
        new("ANGLE (Windows)", "BSD-3-Clause", "third-party/ANGLE.md"),
        new("SkiaSharp / HarfBuzzSharp", "MIT", "third-party/SKIASHARP.md"),
        new("CommunityToolkit.Mvvm", "MIT", "third-party/COMMUNITYTOOLKIT-MVVM.md"),
        new("Microsoft.Data.Sqlite", "MIT", "third-party/MICROSOFT-DATA-SQLITE.md"),
        new("SQLite / SQLitePCLRaw", "Public Domain / Apache-2.0", "third-party/SQLITE.md"),
        new("BouncyCastle", "MIT", "third-party/BOUNCYCASTLE.md"),
        new("SSH.NET", "MIT", "third-party/SSHNET.md"),
        new("Lucide icons", "ISC", "third-party/LUCIDE.md"),
        new("Inter (font)", "SIL OFL 1.1", "third-party/INTER-FONT.md"),
        new("System web views (not shipped)", "WebView2 / WebKit", "third-party/WEBVIEW-RUNTIMES.md")
    ];

    public override void Refresh()
    {
        Title = Loc.T("Tab.About");
        OnPropertyChanged(nameof(Version));
    }

    [RelayCommand]
    private void OpenLicense(LicenseItem item)
    {
        var path = System.IO.Path.Combine(AppPaths.LicensesDirectory, item.Path.Replace('/', System.IO.Path.DirectorySeparatorChar));
        if (!File.Exists(path) || !Shell.Open(path)) Owner?.ShowToast(Loc.T("Toast.LicenseMissing"), true);
    }

    [RelayCommand]
    private static void OpenRepository() => Shell.OpenWebUrl(AppHost.RepositoryUrl);

    [RelayCommand]
    private static void OpenWebsite() => Shell.OpenWebUrl(AppHost.WebsiteUrl);

    [RelayCommand]
    private static void OpenDataFolder() => Shell.Open(AppPaths.DataDirectory);
}
