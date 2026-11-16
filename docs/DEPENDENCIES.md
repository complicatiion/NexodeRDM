# Dependencies

| Package | Version | License | Project | Purpose |
| --- | --- | --- | --- | --- |
| Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter | 12.1.2 | MIT | Nexode.App | UI framework (pulls in SkiaSharp 3.119.4 – also used directly to decode VNC JPEG rectangles –, HarfBuzzSharp 8.3.1.3, MicroCom.Runtime 0.11.6, Tmds.DBus.Protocol 0.94.1, Avalonia.Angle.Windows.Natives – BSD-3-Clause) |
| Avalonia.Controls.ColorPicker | 12.1.0 | MIT | Nexode.App | custom accent color |
| Avalonia.Controls.WebView | 12.1.0 | MIT | Nexode.App | built-in browser for web panels (hosts the system web view: WebView2, WKWebView, WebKitGTK – not shipped) |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | Nexode.App | MVVM source generators |
| Microsoft.Data.Sqlite | 10.0.0 | MIT | Nexode.Storage | SQLite access (bundles SQLitePCLRaw + e_sqlite3) |
| BouncyCastle.Cryptography | 2.7.0 | MIT | Nexode.Core, Nexode.Protocols | Argon2id, mRemoteNG import (AES-GCM), Ed25519 key generation, J-PAKE for LAN sync |
| SSH.NET | 2026.0.0 | MIT | Nexode.Protocols | SSH shell and SFTP (pulls in Microsoft.Extensions.Logging.Abstractions 8.0.3) |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 2.9.3 / 3.0.2 / 17.12.0 | Apache-2.0 / MIT | tests | unit tests |

Versions are managed centrally in `Directory.Packages.props`. AES-256-GCM, DPAPI and Credential Manager (P/Invoke), TLS (`SslStream`), zlib, DES (VNC authentication), named pipes and JSON come from .NET itself. The RDP client (`mstscax.dll`) and ATL (`atl.dll`) are part of Windows and loaded at run time.

Own implementations instead of packages: terminal emulator, FTP/FTPS client, VNC (RFB) client, Telnet, serial port access (Win32 comm API / stty, instead of System.IO.Ports), Wake-on-LAN, WebDAV client, S3 client with Signature V4, live status probes and the GitHub update check – fewer dependencies and full control over TLS and host key checks. SCP and SSH tunnels use SSH.NET. Version 0.0.4 added only Avalonia.Controls.WebView (MIT); Avalonia.Diagnostics was removed. Version 0.0.5 adds no package: the SSH agent client, the Windows notification icon and folder sync are own code. Avalonia build telemetry is switched off (`AvaloniaTelemetryOptOut`).

The release packager (`build/Packager`) uses only .NET (`System.Formats.Tar`, `System.IO.Compression`); Inno Setup 6 (Windows setup) and `rcodesign` (macOS signing outside a Mac) are optional build tools and not part of any package.

Planned for later versions (not referenced yet): Lextm.SharpSnmpLib (SNMP).
