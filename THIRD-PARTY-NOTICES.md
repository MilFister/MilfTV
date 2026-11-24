# Third-party notices

MilfTV ships with the components below. Their licence texts are in the `licenses` folder, next to this file (in the source repository and in the installed application).

## Native components

| Component | Version | Licence | Source |
|---|---|---|---|
| libmpv (`libmpv-2.dll`): mpv with FFmpeg, libass, libplacebo and the other libraries of the build | mpv 0.42.0 (shinchiro build) | GPL-2.0-or-later, as built (it includes GPL components such as rubberband and libdvdnav) | <https://github.com/mpv-player/mpv>, build scripts <https://github.com/shinchiro/mpv-winbuild-cmake> |
| Vulkan loader (`vulkan-1.dll`) | 1.4.357 | Apache-2.0. Copyright © 2015-2026 The Khronos Group Inc., Valve Corporation, LunarG, Inc. | <https://github.com/KhronosGroup/Vulkan-Loader> |
| ANGLE (OpenGL ES on Direct3D 11, through Avalonia.Angle.Windows.Natives) | 2.1.25547 | BSD-3-Clause, see `licenses/ANGLE-LICENSE.txt` | <https://chromium.googlesource.com/angle/angle> |
| Skia and HarfBuzz (through SkiaSharp and HarfBuzzSharp) | 2.88.9 / 8.3.1.1 | MIT, with the notices in `licenses/SkiaSharp-HarfBuzzSharp-THIRD-PARTY-NOTICES.txt` | <https://github.com/mono/SkiaSharp> |
| SQLite (through SQLitePCLRaw.lib.e_sqlite3) | 3.x | Public domain | <https://sqlite.org> |

The ARM64 version ships the ARM64 build of the same Khronos loader published by the Silk.NET project (.NET Foundation, package `Silk.NET.Vulkan.Loader.Native` 2025.9.12, Apache-2.0), and the ARM64 (aarch64) build of libmpv from the same source and date as the x64 one.

The Vulkan loader is used only when Windows does not already provide one (it normally comes with the graphics driver). libmpv needs it to start, even though MilfTV draws the video through OpenGL.

MilfTV itself is licensed under the GNU General Public License v3.0 (see `LICENSE`), which is compatible with libmpv (GPL-2.0-or-later, texts in `licenses/GPL-2.0.txt` and `licenses/GPL-3.0.txt`) and with the Apache-2.0 Vulkan loader. The source code of libmpv and of the libraries it contains is available from the projects listed above, for the build referenced by its version.

## .NET packages

| Package | Version | Licence | Copyright |
|---|---|---|---|
| Avalonia (Avalonia, Desktop, Win32, Skia, Themes.Fluent, Themes.Simple, Controls.ColorPicker, FreeDesktop, Native, X11, Remote.Protocol) | 11.3.23 | MIT | © 2013-2026 The AvaloniaUI Project |
| Avalonia.Controls.ItemsRepeater | 11.1.5 | MIT | © 2013-2024 The AvaloniaUI Project |
| CommunityToolkit.Mvvm | 8.4.2 | MIT | © .NET Foundation and Contributors |
| HarfBuzzSharp, HarfBuzzSharp.NativeAssets.Win32 | 8.3.1.1 | MIT | © Microsoft Corporation |
| MicroCom.Runtime | 0.11.0 | MIT | © 2021 Nikita Tsukanov |
| Microsoft.Data.Sqlite.Core | 10.0.15 | MIT | © Microsoft Corporation |
| Microsoft.Web.WebView2 | 1.0.4370.26 | BSD-style, see `licenses/WebView2-SDK-LICENSE.txt` | © Microsoft Corporation |
| SkiaSharp, SkiaSharp.NativeAssets.Win32 | 2.88.9 | MIT | © Microsoft Corporation |
| SQLitePCLRaw (bundle_e_sqlite3, core, lib.e_sqlite3, provider.e_sqlite3) | 2.1.13 | Apache-2.0, see `licenses/Apache-2.0.txt` | © 2014-2024 SourceGear, LLC |
| System.Security.Cryptography.ProtectedData | 10.0.15 | MIT | © Microsoft Corporation |
| Tmds.DBus.Protocol | 0.21.3 | MIT | © Tom Deseyn |
| Velopack | 1.2.161 | MIT | © Velopack Ltd. |
| .NET runtime (self-contained) | 10.0 | MIT | © .NET Foundation and Contributors |

### MIT licence

The packages marked MIT above are distributed under the following terms, with the copyright notice of each package as listed.

```
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

## Online services

- Free playlist offered to profiles without a source: [iptv-org](https://github.com/iptv-org/iptv) (links to publicly available streams; MilfTV does not host any content).
- Posters, synopses and trailers: [TMDB](https://www.themoviedb.org), with the user's own API key. This product uses the TMDB API but is not endorsed or certified by TMDB.
