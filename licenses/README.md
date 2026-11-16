# Licenses

## Nexode RDM

Nexode RDM is released under the **MIT License** – see [LICENSE.md](LICENSE.md).
Copyright © 2026 Sven Klocke (*sksdesign | complicatiion*).

| Document | Content |
| --- | --- |
| [LICENSE.md](LICENSE.md) | MIT License of Nexode RDM |
| [COPYRIGHT.md](COPYRIGHT.md) | Copyright and trademark notes |
| [LEGAL-NOTICE.md](LEGAL-NOTICE.md) | Disclaimer, limitation of liability, notes on credentials |
| [PRIVACY.md](PRIVACY.md) | Privacy notes (local-first, no telemetry) |

## Third-party components

Components shipped with Nexode keep their own copyrights and licenses. Full license texts are in [third-party/](third-party/).

| Component | Version | License | Shipped as | File |
| --- | --- | --- | --- | --- |
| .NET Runtime | 10.0 | MIT | self-contained runtime | [third-party/DOTNET.md](third-party/DOTNET.md) |
| Avalonia UI (incl. Fluent theme, color picker, WebView, Inter font package, MicroCom, Tmds.DBus.Protocol) | 12.1.2 / 12.1.0 | MIT | library | [third-party/AVALONIA.md](third-party/AVALONIA.md) |
| ANGLE (via Avalonia, Windows only) | 2.1.27548 | BSD-3-Clause | native library | [third-party/ANGLE.md](third-party/ANGLE.md) |
| SkiaSharp / HarfBuzzSharp (via Avalonia) | 3.119.4 / 8.3.1.3 | MIT (+ Skia BSD-3-Clause, HarfBuzz MIT-style) | native libraries | [third-party/SKIASHARP.md](third-party/SKIASHARP.md) |
| CommunityToolkit.Mvvm | 8.4.0 | MIT | library | [third-party/COMMUNITYTOOLKIT-MVVM.md](third-party/COMMUNITYTOOLKIT-MVVM.md) |
| Microsoft.Data.Sqlite | 10.0.0 | MIT | library | [third-party/MICROSOFT-DATA-SQLITE.md](third-party/MICROSOFT-DATA-SQLITE.md) |
| SQLite / SQLitePCLRaw | 3.x / 2.1.11 | Public Domain / Apache-2.0 | native library + wrapper | [third-party/SQLITE.md](third-party/SQLITE.md) |
| BouncyCastle.Cryptography | 2.7.0 | MIT | library (Argon2id, mRemoteNG import, Ed25519 keys; also used by SSH.NET) | [third-party/BOUNCYCASTLE.md](third-party/BOUNCYCASTLE.md) |
| SSH.NET | 2026.0.0 | MIT | library (SSH, SFTP) | [third-party/SSHNET.md](third-party/SSHNET.md) |
| Microsoft.Extensions.Logging.Abstractions | 8.0.3 | MIT | library (dependency of SSH.NET) | [third-party/DOTNET.md](third-party/DOTNET.md) |
| Lucide icons | 0.460.0 | ISC | vector icons in the UI | [third-party/LUCIDE.md](third-party/LUCIDE.md) |
| Inter (font) | 4.x | SIL OFL 1.1 | embedded font | [third-party/INTER-FONT.md](third-party/INTER-FONT.md) |

System web views used by the built-in browser (WebView2, WKWebView, WebKitGTK) are not shipped – see [third-party/WEBVIEW-RUNTIMES.md](third-party/WEBVIEW-RUNTIMES.md).

Shared license texts: [MIT](third-party/LICENSE-MIT.md) · [Apache License 2.0](third-party/LICENSE-APACHE-2.0.md) · [SIL Open Font License 1.1](third-party/LICENSE-OFL-1.1.md)

Build-time only (not shipped): xUnit (Apache-2.0), Microsoft.NET.Test.Sdk (MIT), Inno Setup (own license, installer only).
