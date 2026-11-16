# System web views (not shipped)

The built-in browser for web panels (Avalonia.Controls.WebView, MIT – see [AVALONIA.md](AVALONIA.md)) hosts the web view of the operating system. None of these engines is part of the Nexode RDM packages.

| System | Engine | How it gets there | License |
| --- | --- | --- | --- |
| Windows | Microsoft Edge WebView2 Runtime (Evergreen) | Preinstalled on Windows 11 and current Windows 10. Otherwise the Nexode setup downloads Microsoft's Evergreen bootstrapper and installs the runtime (or uses a bootstrapper placed next to the setup script at build time). | Microsoft software license terms of the WebView2 Runtime; the bootstrapper may be distributed with applications |
| macOS | WKWebView (WebKit) | Part of macOS | Apple / WebKit (LGPL/BSD) |
| Linux | WebKitGTK 4.1 (or 4.0) | `libwebkit2gtk-4.1-0` – a dependency of the `.deb`, installed by the package manager | LGPL-2.1 (WebKitGTK) |

The portable Windows ZIP does not install anything: without the WebView2 runtime web panels open in the default browser.
