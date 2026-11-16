using System.Security.Cryptography;
using System.Text;
using Avalonia.Controls;
using Avalonia.Platform;
using Nexode.Core.Model;

namespace Nexode.App.Services;

public enum WebMode
{
    /// <summary>Embedded in a tab (WebView2 on Windows, WKWebView on macOS, WebKitGTK on Linux/X11).</summary>
    Tab,
    /// <summary>Own native window (when the system web view cannot be embedded).</summary>
    Dialog,
    /// <summary>Default browser of the system.</summary>
    External
}

/// <summary>
/// Built-in browser for web panels: decides how a panel opens, where cookies are kept (per data source) and how the
/// system web view is configured. Nothing is downloaded – the web view of the operating system is used.
/// </summary>
public static class WebPanels
{
    private static DetailedWebViewAdapterInfo? _info;

    public static WebViewAdapterType PlatformAdapter =>
        OperatingSystem.IsWindows() ? WebViewAdapterType.WebView2
        : OperatingSystem.IsMacOS() ? WebViewAdapterType.WkWebView
        : WebViewAdapterType.WebKitGtk;

    /// <summary>Availability of the system web view (cached; null when it cannot be determined).</summary>
    public static DetailedWebViewAdapterInfo? Info
    {
        get
        {
            if (_info is not null) return _info;
            try
            {
                _info = WebViewAdapterInfo.GetAdapterInfo(PlatformAdapter);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                AppLog.Write("webview info: " + ex.Message);
            }

            return _info;
        }
    }

    /// <summary>Set by tests: forces a mode regardless of the system.</summary>
    public static WebMode? Override { get; set; }

    /// <summary>Why the built-in browser is not available (null = available).</summary>
    public static string? UnavailableReason => Info is { IsSupported: true, IsInstalled: true } ? null : Info?.UnavailableReason ?? "No web view available.";

    public static WebMode Mode(Entry entry, bool forceExternal = false)
    {
        if (forceExternal || AppHost.Settings.OpenWebExternal || entry.Setting(SettingKeys.WebExternal) == "1") return WebMode.External;
        if (Override is { } o) return o;
        if (Info is not { IsSupported: true, IsInstalled: true } info) return WebMode.External;
        if ((info.SupportedScenarios & (WebViewEmbeddingScenario.NativeControlHost | WebViewEmbeddingScenario.OffscreenRenderer)) != 0) return WebMode.Tab;
        return (info.SupportedScenarios & WebViewEmbeddingScenario.NativeDialog) != 0 ? WebMode.Dialog : WebMode.External;
    }

    public static bool CanDialog => Info is { IsSupported: true, IsInstalled: true } i && (i.SupportedScenarios & WebViewEmbeddingScenario.NativeDialog) != 0;

    /// <summary>Profile folder: cookies and logins are kept per data source (separate folder without certificate checks).</summary>
    public static string ProfileFolder(bool insecure)
    {
        var source = AppHost.Workspace?.Info?.Id is { Length: > 0 } id ? Safe(id) : "default";
        var dir = Path.Combine(AppPaths.DataDirectory, "web", source + (insecure ? "-insecure" : ""));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Throw-away profile (jump host proxy: browser arguments differ per session).</summary>
    public static string TemporaryProfile()
    {
        var dir = Path.Combine(AppPaths.DataDirectory, "web", "tmp", Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    public static void DeleteTemporaryProfiles()
    {
        try
        {
            var dir = Path.Combine(AppPaths.DataDirectory, "web", "tmp");
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Removes all cookies and site data of the built-in browser for the current data source.</summary>
    public static bool ClearData()
    {
        var ok = true;
        foreach (var insecure in new[] { false, true })
        {
            try
            {
                var dir = ProfileFolder(insecure);
                Directory.Delete(dir, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                ok = false; // in use by an open panel
            }
        }

        return ok;
    }

    /// <summary>Stable GUID for the WKWebView data store of a profile folder.</summary>
    public static Guid StoreId(string folder) => new(MD5.HashData(Encoding.UTF8.GetBytes(folder.ToUpperInvariant())));

    public static void Configure(WebViewEnvironmentRequestedEventArgs e, string profile, bool ignoreCertificates, int? socksPort)
    {
        switch (e)
        {
            case WindowsWebView2EnvironmentRequestedEventArgs w:
            {
                w.UserDataFolder = profile;
                var args = new List<string>();
                if (ignoreCertificates) args.Add("--ignore-certificate-errors");
                if (socksPort is { } port) args.Add($"--proxy-server=socks5://127.0.0.1:{port}");
                if (args.Count > 0) w.AdditionalBrowserArguments = string.Join(' ', args);
                if (Localization.Loc.Language is { Length: > 0 } lang) w.Language = lang;
                break;
            }
            case AppleWKWebViewEnvironmentRequestedEventArgs a:
                a.DataStoreIdentifier = StoreId(profile);
                break;
            case GtkWebViewEnvironmentRequestedEventArgs g:
                g.BaseDataDirectory = Path.Combine(profile, "data");
                g.BaseCacheDirectory = Path.Combine(profile, "cache");
                break;
        }
    }

    /// <summary>Certificate errors can only be ignored by WebView2 (Windows).</summary>
    public static bool CanIgnoreCertificates => OperatingSystem.IsWindows();

    /// <summary>A SOCKS proxy can only be set for WebView2; other systems use a forwarded port.</summary>
    public static bool CanUseProxy => OperatingSystem.IsWindows();

    private static string Safe(string s) => string.Concat(s.Where(char.IsLetterOrDigit));

    /// <summary>Script that fills a login form (user name and password) without submitting it.</summary>
    public static string FillScript(string? user, string? password, string? userSelector, string? passSelector)
    {
        static string J(string? s) => System.Text.Json.JsonSerializer.Serialize(s ?? "", WebJson.Default.String);
        return $$"""
            (function () {
              var u = {{J(user)}}, p = {{J(password)}}, us = {{J(userSelector)}}, ps = {{J(passSelector)}};
              function set(el, v) {
                if (!el || !v || el.value) return false;
                var proto = Object.getPrototypeOf(el), d = Object.getOwnPropertyDescriptor(proto, 'value');
                if (d && d.set) d.set.call(el, v); else el.value = v;
                el.dispatchEvent(new Event('input', { bubbles: true }));
                el.dispatchEvent(new Event('change', { bubbles: true }));
                return true;
              }
              var pass = ps ? document.querySelector(ps) : document.querySelector('input[type=password]');
              var user = us ? document.querySelector(us) : null;
              if (!user && pass) {
                var inputs = Array.prototype.slice.call(document.querySelectorAll('input'));
                var i = inputs.indexOf(pass);
                for (var k = i - 1; k >= 0; k--) {
                  var t = (inputs[k].type || 'text').toLowerCase();
                  if (t === 'text' || t === 'email' || t === 'tel') { user = inputs[k]; break; }
                }
              }
              var a = set(user, u), b = set(pass, p);
              return (a || b) ? 'filled' : (pass ? 'present' : 'none');
            })();
            """;
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class WebJson : System.Text.Json.Serialization.JsonSerializerContext;
