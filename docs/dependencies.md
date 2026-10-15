# Dependency and license register

Every dependency must appear here with its license and the reason it is needed before it is added to `Directory.Packages.props` or `third_party/`. "Checked" means the fact was looked up on the date shown; it is not legal advice.

## In use (Milestones 2 to 6)

Milestone 6 added no packages. YouTube playback uses WebView2 through the `WebView2` control that the Windows App SDK already includes (the WebView2 Runtime is part of Windows 11), the YouTube Data API and Google sign-in are reached with .NET's own `HttpClient` and `TcpListener`, and secrets go to the same Credential Manager store as the TMDB key. The new `Aperture.YouTube` project is Aperture's own code.

Milestones 3, 4 and 5 added no packages and no native libraries. Milestone 5 reads sound, dark pictures and chapters for intro and credits detection with the libmpv already in use (its `ao=pcm` output and FFmpeg's `blackdetect` filter), and computes the audio fingerprint in Aperture's own code instead of adding Chromaprint. The engine test project now also references `Aperture.Media`, which is Aperture's own code. Library scanning reuses libmpv headless instead of adding FFprobe, the TMDB key is stored with Windows Credential Manager through four `advapi32` functions called directly (`CredReadW`, `CredWriteW`, `CredDeleteW`, `CredFree`), and TMDB is reached with .NET's own `HttpClient`.

| Dependency | Version | License | Why | Notes |
|---|---|---|---|---|
| .NET SDK / runtime | 10.0 (SDK 10.0.1xx) | MIT | Language and runtime | .NET 10 is the current LTS line; .NET 11 was at RC 2 on NuGet (checked 2026-10-15) |
| Microsoft.WindowsAppSDK | 2.5.1 | Microsoft Software License (requires acceptance) | WinUI 3 and windowing APIs | Latest stable on NuGet, published 2026-09-16 (checked). It is a metapackage that also pulls AI, ML, Search and Widgets components; switching to only the WinUI/Foundation component packages to shrink the self-contained app is to be evaluated in M5 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Software License | Windows SDK build tooling required by WinUI projects | Build-time only |
| Microsoft.Data.Sqlite | 10.0.13 | MIT (bundles SQLite via SQLitePCLRaw: SQLite public domain, SQLitePCLRaw Apache-2.0) | Settings and playback history (`Aperture.Data`) | Moved forward from M4 because the home screen's Continue Watching row needs real stored positions. Latest stable 10.0.x on NuGet (checked 2026-10-15) |
| **libmpv** (`libmpv-2.dll`, client API 2.x) | Build `20261015` of [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake/releases/tag/20261015), mpv git `492bae0768`; x64 and ARM64 | **Treated as GPLv2+.** mpv is GPL unless built with `-Dgpl=false`, and these builds are not advertised as LGPL. Not verified from the build's own configuration here | Playback engine (decode, demux, subtitles, scaling, audio) | Not committed. `tools/Get-Libmpv.ps1` downloads the pinned archive, checks its SHA-256 (in `third_party/libmpv.json`) and extracts the DLL at build time. The GPL vs LGPL release decision below is still open. On Linux CI the engine tests use Ubuntu's `libmpv2` package instead |
| Vortice.Direct3D11, Vortice.DXGI | 3.8.3 | MIT | Typed Direct3D 11 and DXGI bindings for the video presenter (device, textures, composition swap chain) | Pulls in SharpGen.Runtime and SharpGen.Runtime.COM 2.4.2-beta (MIT) transitively; the beta tag is the version Vortice 3.8.x depends on. Hand-writing the COM vtables was the alternative and is more code to get wrong |
| Avalonia.Angle.Windows.Natives | 2.1.27548.20260419 | BSD-3-Clause (ANGLE, Google) | ANGLE: OpenGL ES on Direct3D 11, so libmpv's GL renderer can draw into Aperture's D3D11 textures (architecture.md §4.2) | Ships `av_libglesv2.dll` for x64 and ARM64; exports carry an `EGL_`/`GL_` prefix. Used only as a native binary; no Avalonia UI code is referenced |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 2.9.3, 3.1.4, 17.14.1 | Apache-2.0 / Apache-2.0 / MIT | Unit tests | Test-only, not shipped. xunit v3 (4.x) is available; staying on v2 until there is a reason to move |

## Planned, and open release decisions

| Dependency | License | Why | Milestone | Open questions |
|---|---|---|---|---|
| **libmpv** (`libmpv-2.dll`) | GPLv2+ by default; LGPLv2.1+ only when built without GPL-only files (`-Dgpl=false`). Verified in mpv's `Copyright` file on master, 2026-10-15 | Playback engine | M2 (in use) | The mpv project does not ship Windows binaries. Third-party builds (for example the `mpv-winbuild-cmake` builds) must be checked for which license they were built under and which FFmpeg configuration they include. **Decision needed from adi before release:** either accept GPL obligations for the whole app (Aperture's source would have to be distributed under GPL-compatible terms) or produce/obtain an LGPL build and dynamically link it, shipping license texts and offering corresponding source. Development can proceed with either; release cannot. |
| FFmpeg (inside libmpv) | LGPL-2.1+ or GPL depending on build flags | Demuxing and decoding | M2 (in use, inside the pinned build) | Same as above. Also: shipping H.264/HEVC/AAC decoders can carry patent-licensing implications in some jurisdictions; needs a decision before public distribution |
| libass, and other libmpv deps (HarfBuzz, FreeType, FriBidi, libplacebo…) | ISC / MIT / FreeType / LGPL (per library; to be enumerated from the chosen build) | Subtitles, shaping, rendering | M2 (inside the pinned build) | Enumerate from the exact build into `THIRD-PARTY-NOTICES.md` |
| FFprobe | Same as FFmpeg build | Library scanning metadata | Not needed | Milestone 4 reads length, picture size and bitrate with a headless libmpv instance instead |
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
| FFmpeg (Ubuntu package, CI only) | LGPL/GPL per build | Generates synthetic test clips (`tools/visual-review/make-test-media.sh`, `tools/test-media/make-engine-test-media.sh`). Nothing it produces is committed or shipped |
| libmpv2 (Ubuntu package, CI only) | GPLv2+ | Runs the engine tests on Linux |
| 7-Zip (preinstalled on Windows runners) or `tar` | LGPL / BSD | Extracts the pinned libmpv archive in `Get-Libmpv.ps1` |

## Rules

- No dependency is added without a row here and a sentence on why it is necessary.
- Native binaries are never committed. `third_party/README.md` records source URL, version, SHA-256 and license for each, and a script fetches them.
- `THIRD-PARTY-NOTICES.md` is generated before the first release from this register.

## Removed

| Dependency | Removed in | Why |
|---|---|---|
| Windows Media Foundation (`Windows.Media.Playback.MediaPlayer`) | M2 | Interim engine for Milestone 1 only; replaced by libmpv |
