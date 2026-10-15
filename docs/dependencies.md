# Dependency and license register

Every dependency must appear here with its license and the reason it is needed before it is added to `Directory.Packages.props` or `third_party/`. "Checked" means the fact was looked up on the date shown; it is not legal advice.

## Licence decision (Milestone 10)

**Aperture is released under GPL-3.0-or-later** ([LICENSE](../LICENSE)), chosen by adi on 2026-10-15, so it can ship the pinned GPL build of libmpv as it is. The libraries inside that build were read from the DLL itself and are listed, with their licences, in [THIRD-PARTY-NOTICES.md](../THIRD-PARTY-NOTICES.md); the notices, the GPL and the other licence texts (`third_party/licenses/`) are installed next to `Aperture.exe`. The build contains no non-free parts (no `fdk-aac`). Corresponding source for libmpv and its libraries is pointed to (the pinned shinchiro release and each project's source at the versions the build names), not mirrored in this repository; mirroring it is listed as unresolved in the [Milestone 10 report](reports/milestone-10.md#5-known-limitations).

Milestone 10 added no packages. The installer is made with Inno Setup 6 (build tool only, below).

## In use (Milestones 2 to 10)

Milestone 9 added no packages. Seek previews use a second instance of the libmpv already shipped; clearer dialogue and even volume use FFmpeg filters inside that libmpv (`dialoguenhance`, `dynaudnorm`); the clipboard report uses Windows' own `DataTransfer` API. Local transcription was not built: it would need a speech model (for example whisper.cpp and a model file of 75 MB or more) and is waiting for a decision.

Milestone 8 added no packages. Playing on a TV uses SSDP and SOAP over .NET's own `UdpClient`, `HttpClient` and `System.Xml`; the TV is reached with the DLNA/UPnP AV standards, which need no licence or key to control a renderer. Conversion for TVs uses the same user-installed FFmpeg as the media server. CI's visual review starts a stand-in TV written in Python with only its standard library (Python is preinstalled on GitHub's Windows runners; nothing is shipped).

Milestone 7 added no packages. The media server uses ASP.NET Core (Kestrel), which is part of .NET itself (the `Microsoft.AspNetCore.App` shared framework, MIT) and is published with the server; its certificates are made with .NET's own cryptography. FFmpeg is **not** shipped: the server uses an FFmpeg the user installs, only to repackage or convert files a device can't play directly, so FFmpeg's licence terms stay with the copy the user chose. Aperture runs it as a separate program and never links to it.

Milestone 6 added no packages. YouTube playback uses WebView2 through the `WebView2` control that the Windows App SDK already includes (the WebView2 Runtime is part of Windows 11), the YouTube Data API and Google sign-in are reached with .NET's own `HttpClient` and `TcpListener`, and secrets go to the same Credential Manager store as the TMDB key. The new `Aperture.YouTube` project is Aperture's own code.

Milestones 3, 4 and 5 added no packages and no native libraries. Milestone 5 reads sound, dark pictures and chapters for intro and credits detection with the libmpv already in use (its `ao=pcm` sound output and its encoding mode for a tiny grey copy of the picture), and computes the audio fingerprint in Aperture's own code instead of adding Chromaprint. The engine test project now also references `Aperture.Media`, which is Aperture's own code. Library scanning reuses libmpv headless instead of adding FFprobe, the TMDB key is stored with Windows Credential Manager through four `advapi32` functions called directly (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`), and TMDB is reached with .NET's own `HttpClient`.

| Dependency | Version | License | Why | Notes |
|---|---|---|---|---|
| .NET SDK / runtime | 10.0 (SDK 10.0.1xx) | MIT | Language and runtime | .NET 10 is the current LTS line; .NET 11 was at RC 2 on NuGet (checked 2026-10-15) |
| Microsoft.WindowsAppSDK | 2.5.1 | Microsoft Software License (requires acceptance) | WinUI 3 and windowing APIs | Latest stable on NuGet, published 2026-09-16 (checked). It is a metapackage that also pulls AI, ML, Search and Widgets components; switching to only the WinUI/Foundation component packages to shrink the self-contained app is to be evaluated in M5 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Software License | Windows SDK build tooling required by WinUI projects | Build-time only |
| Microsoft.Data.Sqlite | 10.0.13 | MIT (bundles SQLite via SQLitePCLRaw: SQLite public domain, SQLitePCLRaw Apache-2.0) | Settings and playback history (`Aperture.Data`) | Moved forward from M4 because the home screen's Continue Watching row needs real stored positions. Latest stable 10.0.x on NuGet (checked 2026-10-15) |
| **libmpv** (`libmpv-2.dll`, client API 2.x) | Build `20261015` of [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261015), mpv git `492bae0768`; x64 and ARM64 | **GPLv2+** (with FFmpeg built `--enable-gpl`, including x264 and x265); compatible with Aperture's GPL-3.0-or-later | Playback engine (decode, demux, subtitles, scaling, audio) | Not committed. `tools/Get-Libmpv.ps1` downloads the pinned archive, checks its SHA-256 (in `third_party/libmpv.json`) and extracts the DLL at build time. Shipped in the release under the GPL decision above. On Linux CI the engine tests use Ubuntu's `libmpv2` package instead |
| Vortice.Direct3D11, Vortice.DXGI | 3.8.3 | MIT | Typed Direct3D 11 and DXGI bindings for the video presenter (device, textures, composition swap chain) | Pulls in SharpGen.Runtime and SharpGen.Runtime.COM 2.4.2-beta (MIT) transitively; the beta tag is the version Vortice 3.8.x depends on. Hand-writing the COM vtables was the alternative and is more code to get wrong |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD-3-Clause (ANGLE, Google) | ANGLE: OpenGL ES on Direct3D 11, so libmpv's GL renderer can draw into Aperture's D3D11 textures (architecture.md §4.2) | Ships `av_libglesv2.dll` for x64 and ARM64; exports carry an `EGL_`/`GL_` prefix. Used only as a native binary; no Avalonia UI code is referenced |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 2.9.3, 3.1.4, 17.14.1 | Apache-2.0 / Apache-2.0 / MIT | Unit tests | Test-only, not shipped. xunit v3 (4.x) is available; staying on v2 until there is a reason to move |

## Earlier planning notes (kept for history)

The libmpv licence question in the first row was decided in Milestone 10 (GPL, above). The patent question for H.264, HEVC and AAC decoders remains open and is listed in the Milestone 10 report.

| Dependency | License | Why | Milestone | Open questions |
|---|---|---|---|---|
| **libmpv** (`libmpv-2.dll`) | GPLv2+ by default; LGPLv2.1+ only when built without GPL-only files (`-Dgpl=false`). Verified in mpv's `Copyright` file on master, 2026-10-15 | Playback engine | M2 (in use) | The mpv project does not ship Windows binaries. Third-party builds (for example the `mpv-winbuild-cmake` builds) must be checked for which license they were built under and which FFmpeg configuration they include. **Decision needed from adi before release:** either accept GPL obligations for the whole app (Aperture's source would have to be distributed under GPL-compatible terms) or produce/obtain an LGPL build and dynamically link it, shipping license texts and offering corresponding source. Development can proceed with either; release cannot. |
| FFmpeg (inside libmpv) | LGPL-2.1+ or GPL depending on build flags | Demuxing and decoding | M2 (in use, inside the pinned build) | Same as above. Also: shipping H.264/HEVC/AAC decoders can carry patent-licensing implications in some jurisdictions; needs a decision before public distribution |
| libass, and other libmpv deps (HarfBuzz, FreeType, FriBidi, libplacebo…) | ISC / MIT / FreeType / LGPL (per library; to be enumerated from the chosen build) | Subtitles, shaping, rendering | M2 (inside the pinned build) | Enumerate from the exact build into `THIRD-PARTY-NOTICES.md` |
| FFprobe | Same as FFmpeg build | Library scanning metadata | Not needed | Milestone 4 reads length, picture size and bitrate with a headless libmpv instance instead. The media server (M7) uses the user's own FFprobe, next to their FFmpeg, to choose how to stream a file |
| FFmpeg, installed by the user (optional) | LGPL-2.1+ or GPL, per the build the user installs | Media server: repackaging and converting for devices that can't play the original | M7 | Not distributed with Aperture, so no redistribution obligation arises from it. Settings links to where to get it |
| CommunityToolkit.Mvvm | MIT | Source-generated observable properties/commands, removes MVVM boilerplate | M2 | Only if ViewModels grow enough to justify it |
| Microsoft.Extensions.DependencyInjection / Logging | MIT | Composition root and structured logging abstractions | M1–M2 | Logging sink: a small rolling file logger of our own vs. Serilog (Apache-2.0), decided in M4 |

## Online services (optional, off by default)

| Service | Terms | Why | Notes |
|---|---|---|---|
| [TMDB API](https://developer.themoviedb.org/docs) v3 | TMDB API terms of use; non-commercial use is free with attribution | Optional titles, years, episode names and posters for films and shows in the library | Used only when the user turns on "Look up details online" and pastes their own API key (v3 key or v4 read access token). Aperture ships no key. The key is kept in Windows Credential Manager, never in the database, settings or logs. Only the cleaned-up title, year, season and episode are sent, never file paths. Settings shows the required notice: "This product uses the TMDB API but is not endorsed or certified by TMDB." Commercial distribution would need TMDB's commercial terms |

| [YouTube Data API v3](https://developers.google.com/youtube/v3) and [IFrame Player API](https://developers.google.com/youtube/iframe_api_reference) | [YouTube API Services Terms](https://developers.google.com/youtube/terms/api-services-terms-of-service) and [Developer Policies](https://developers.google.com/youtube/terms/developer-policies), checked 2026-10-15 | The YouTube section: search, popular videos, details, liked videos and playlists, and embedded playback | The player and oEmbed need no key. Search needs the user's own API key; liked videos and playlists need the user's own Google OAuth client and read-only consent. Aperture ships no key or client. Shows the YouTube Terms and Google Privacy Policy before first use. Details copied from the API are deleted after 30 days. See [youtube.md](youtube.md) |
| [Google OAuth 2.0 for installed apps](https://developers.google.com/identity/protocols/oauth2/native-app) | Google APIs Terms of Service | Signing in for liked videos and playlists | Loopback redirect with PKCE; refresh token in Credential Manager; revoked on sign out |

## Build and CI tools (not shipped)

| Tool | License | Why |
|---|---|---|
| FFmpeg (Ubuntu package and Chocolatey package, CI only) | LGPL/GPL per build | Generates synthetic test clips (`tools/visual-review/make-test-media.sh`, `tools/test-media/make-engine-test-media.sh`) and runs the media server's conversion tests. Nothing it produces is committed or shipped |
| libmpv2 (Ubuntu package, CI only) | GPLv2+ | Runs the engine tests on Linux |
| Python 3 (preinstalled on Windows runners, CI only) | PSF | Runs `tools/visual-review/fake-tv.py`, the stand-in DLNA TV the visual review casts to (standard library only) |
| gmrender-resurrect (`gmediarender`, Ubuntu package; used by hand, not in CI) | GPLv2+ | A real open-source DLNA renderer that casting was checked against; see build-and-test.md |
| 7-Zip (preinstalled on Windows runners) or `tar` | LGPL / BSD | Extracts the pinned libmpv archive in `Get-Libmpv.ps1` |
| [Inno Setup](https://jrsoftware.org/isinfo.php) 6 (Chocolatey `innosetup` in CI, if the runner lacks it) | Inno Setup License (free, including commercial use) | Makes the per-user installer from `installer/Aperture.iss`. Its setup and uninstall program is part of the installer; its licence is in THIRD-PARTY-NOTICES.md |
| Pillow (Python, used once by hand) | MIT-CMU | `tools/brand/make-icon.py` draws `Assets/Aperture.ico`; the icon is committed, so Pillow isn't needed to build |

## Rules

- No dependency is added without a row here and a sentence on why it is necessary.
- Native binaries are never committed. `third_party/README.md` records source URL, version, SHA-256 and license for each, and a script fetches them.
- `THIRD-PARTY-NOTICES.md` is kept in step with this register and ships with every release.

## Removed

| Dependency | Removed in | Why |
|---|---|---|
| Windows Media Foundation (`Windows.Media.Playback.MediaPlayer`) | M2 | Interim engine for Milestone 1 only; replaced by libmpv |
