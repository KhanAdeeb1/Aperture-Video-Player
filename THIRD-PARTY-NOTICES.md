# Aperture: licence and third-party notices

Aperture is free software: you can redistribute it and/or modify it under the terms of the **GNU General Public License, version 3 or (at your option) any later version**, as published by the Free Software Foundation. It comes with NO WARRANTY. The full licence is in `LICENSE.txt` (`LICENSE` in the source repository).

Aperture's source code: <https://github.com/KhanAdeeb1/Aperture-Video-Player>. The source for any release is the commit its tag names.

Aperture is GPL because the playback engine it ships, libmpv, is built under the GPL (see below). This was decided by the project owner on 2026-10-15.

The components below are included in the installer and the portable zip. Licence texts that are not printed here are in the `licenses` folder next to `Aperture.exe` (`third_party/licenses` in the source).

This list was checked on 2026-10-15. It is a good-faith record, not legal advice.

## Playback engine: libmpv and the libraries built into it

`libmpv-2.dll`, from build `20261015` of [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261015), mpv `v0.41.0-1175-g492bae076`, FFmpeg `N-127552-gee8cbd7db`.

- **mpv**: GPL-2.0-or-later as built (it is LGPL only when built with `-Dgpl=false`, which this build is not). Copyright the mpv developers. Source: <https://github.com/mpv-player/mpv/tree/492bae0768>.
- **FFmpeg**: built with GPL components (it contains the x264 and x265 encoders), so the build is under the GPL. Copyright the FFmpeg developers. Source: <https://github.com/FFmpeg/FFmpeg/tree/ee8cbd7db>.
- The build script that fixes every other library's version, and so identifies the corresponding source: <https://github.com/shinchiro/mpv-winbuild-cmake/tree/20261015>.

Libraries found inside this DLL (by their embedded names and version strings), with their licences:

| Library | Licence |
|---|---|
| libass, FriBidi, HarfBuzz, FreeType, Fontconfig | ISC; LGPL-2.1+; MIT ("Old MIT"); FreeType Licence (FTL); MIT-style |
| libplacebo, shaderc, SPIRV-Cross, Vulkan loader | LGPL-2.1+; Apache-2.0; Apache-2.0; Apache-2.0 |
| dav1d, libaom, libvpx, SVT-AV1 | BSD-2-Clause; BSD-2-Clause + AOM patent licence; BSD-3-Clause; BSD-3-Clause-Clear + AOM patent licence |
| x264, x265 | GPL-2.0-or-later |
| libjxl, libwebp, libpng, libjpeg-turbo, lcms2, zimg | BSD-3-Clause; BSD-3-Clause; libpng licence; IJG + BSD-3-Clause; MIT; WTFPL |
| libbluray, libdvdnav, libdvdread | LGPL-2.1+; GPL-2.0+; GPL-2.0+ |
| Opus, Vorbis, Speex, LAME (libmp3lame), libopenmpt, libmodplug, libmysofa, Rubber Band, soxr | BSD-3-Clause; BSD-3-Clause; BSD-3-Clause; LGPL-2.0+; BSD-3-Clause; public domain; BSD-3-Clause; GPL-2.0+; LGPL-2.1+ |
| OpenSSL, libssh, libsrt, libzvbi, libarchive, libxml2, zlib, Brotli, uchardet, LuaJIT | Apache-2.0; LGPL-2.1+; MPL-2.0; GPL-2.0+/LGPL-2.0+; BSD-2-Clause; MIT; zlib; MIT; MPL-1.1/GPL/LGPL; MIT |
| AMD AMF and NVIDIA NVENC headers | MIT |

Copyright in each library belongs to its authors. Because the whole combined engine is distributed under the GPL version 3, every GPL-2.0-or-later and LGPL part is used under terms compatible with it (OpenSSL's Apache-2.0 licence is compatible with GPL version 3, not version 2).

**Corresponding source.** Aperture's own source is public (above). For the libmpv build, the sources are the upstream projects at the versions fixed by the build script linked above. If you received Aperture from someone and cannot get any of that source, ask the person or project who gave you the copy; the GPL requires them to provide it.

## .NET and the Windows App SDK

| Component | Licence | Copyright | Source / notices |
|---|---|---|---|
| .NET runtime and ASP.NET Core runtime 10.0 (bundled, self-contained) | MIT | © .NET Foundation and Contributors | <https://github.com/dotnet/runtime>, <https://github.com/dotnet/aspnetcore>; their own third-party notices: <https://github.com/dotnet/runtime/blob/main/THIRD-PARTY-NOTICES.TXT> |
| Windows App SDK 2.5.1 (WinUI 3), bundled | Microsoft Software License Terms for the Windows App SDK (redistributable with applications) | © Microsoft Corporation | <https://www.nuget.org/packages/Microsoft.WindowsAppSDK/2.5.1/License> |
| Microsoft Edge WebView2 (used through Windows; not bundled) | Part of Windows 11 | © Microsoft Corporation | |

## Libraries used by Aperture's own code

| Component | Version | Licence | Copyright |
|---|---|---|---|
| Microsoft.Data.Sqlite | 10.0.13 | MIT | © .NET Foundation and Contributors |
| SQLitePCLRaw (bundle_e_sqlite3) | as resolved by Microsoft.Data.Sqlite | Apache-2.0 | © Eric Sink / SourceGear |
| SQLite (inside `e_sqlite3.dll`) | as bundled | Public domain | |
| Vortice.Direct3D11, Vortice.DXGI | 3.8.3 | MIT | © Amer Koleci and Contributors |
| SharpGen.Runtime, SharpGen.Runtime.COM | 2.4.2-beta | MIT | © SharpGenTools contributors |
| ANGLE (`av_libglesv2.dll`, from Avalonia.Angle.Windows.Natives) | 2.1.27548.20260419 | BSD-3-Clause (ANGLE); package MIT | © The ANGLE Project Authors; © AvaloniaUI OÜ |

### MIT licence (for the components marked MIT above)

> Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:
>
> The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.
>
> THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### BSD 3-Clause licence (ANGLE)

> Copyright 2018 The ANGLE Project Authors. All rights reserved.
>
> Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met: (1) Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer. (2) Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution. (3) Neither the name of TransGaming Inc., Google Inc., 3DLabs Inc. Ltd., nor the names of their contributors may be used to endorse or promote products derived from this software without specific prior written permission.
>
> THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT OWNER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

## Installer

The installer is made with [Inno Setup](https://jrsoftware.org/isinfo.php) 6 (Inno Setup Licence, free for any use, © Jordan Russell and Martijn Laan). Its setup and uninstall programs are included in `Aperture-*-setup.exe` and in the installed folder (`unins000.exe`).

## Online services

Aperture can use TMDB and the YouTube APIs only with your own keys, and only when you turn them on. "This product uses the TMDB API but is not endorsed or certified by TMDB." YouTube use is under the YouTube Terms of Service and the Google Privacy Policy. See `docs/dependencies.md` in the source.
