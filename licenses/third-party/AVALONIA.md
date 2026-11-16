# Avalonia UI

| | |
| --- | --- |
| Version | 12.1.2 (Avalonia, Avalonia.Desktop, Avalonia.Themes.Fluent, Avalonia.Fonts.Inter, Avalonia.FreeDesktop.AtSpi) · 12.1.0 (Avalonia.Controls.ColorPicker, Avalonia.Controls.WebView) |
| License | MIT |
| Source | https://github.com/AvaloniaUI/Avalonia |
| Use in Nexode RDM | Cross-platform user interface, Fluent theme, color picker, built-in browser for web panels (Avalonia.Controls.WebView – hosts the web view of the operating system) |

Avalonia ships further MIT-licensed helper libraries (MicroCom.Runtime 0.11.6, Tmds.DBus.Protocol 0.94.1 for Linux) and uses SkiaSharp/HarfBuzzSharp for rendering – see [SKIASHARP.md](SKIASHARP.md). On Windows the ANGLE library (Avalonia.Angle.Windows.Natives, BSD-3-Clause) translates OpenGL to Direct3D – see [ANGLE.md](ANGLE.md).

Avalonia.Controls.WebView contains no browser engine. It uses Microsoft Edge WebView2 on Windows, WKWebView on macOS and WebKitGTK on Linux – all part of (or installed into) the operating system and not redistributed with Nexode RDM. Build telemetry of Avalonia is switched off (`AvaloniaTelemetryOptOut`). The Inter font embedded via Avalonia.Fonts.Inter is licensed under the SIL OFL 1.1 – see [INTER-FONT.md](INTER-FONT.md).

## License text

```
The MIT License (MIT)

Copyright (c) AvaloniaUI OÜ

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```
