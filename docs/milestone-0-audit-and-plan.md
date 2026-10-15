# Milestone 0: repository audit and product plan

Date: 2026-10-15. Author: Claude, for adi.

Conventions: **Verified** means checked during this milestone, with how. **Unverified** means a reasoned expectation that a later milestone must prove. Nothing below marked unverified should be read as established.

---

## 1. Repository and dependency audit

| Item | Finding | How checked |
|---|---|---|
| Repository | `KhanAdeeb1/Aperture-Video-Player` existed but was **empty**: no commits, no branches, no files | `git log` on the fresh clone ("does not have any commits yet"); GitHub branch list returned `[]` |
| Existing code, README, architecture notes, previous reports | None | Same |
| Dependencies, build scripts, tests | None | Same |
| Toolchain available in the audit environment | Linux (Ubuntu 24.04) cloud container. .NET SDK 10.0.112 installed from Ubuntu packages. No Windows, so WinUI 3 cannot be compiled or run here | `dotnet --info`; attempted WinUI build fails at `XamlCompiler.exe: Exec format error` |
| Current upstream versions | .NET 10 is the current LTS line, .NET 11 at RC 2; Windows App SDK 2.5.1 (published 2026-09-16) is the latest stable | NuGet flat-container and registration API, 2026-10-15 |
| libmpv render API | Supports only `opengl` and `sw` render API types | `include/mpv/render.h` on mpv master, 2026-10-15 |
| libmpv client API version | 2.5 | `MPV_CLIENT_API_VERSION` in `include/mpv/client.h` on mpv master |
| mpv license | GPLv2+ by default; LGPLv2.1+ when built without GPL-only files | mpv `Copyright` file on master |

Because no code existed, Milestone 0 created only the project skeleton and documentation (see §4 and the milestone report).

## 2. Inventory of existing working functionality

Before this milestone: **none**. The repository had no content to preserve.

After this milestone, what actually works:

| Functionality | Status |
|---|---|
| `Aperture.Core.AppDataLocation`: resolves `%LOCALAPPDATA%\Aperture`, honours an absolute `APERTURE_DATA_DIR` override, rejects relative overrides, creates folders | Working; 7 unit tests pass on Linux (run here) |
| `Aperture.App`: empty WinUI 3 window with charcoal theme and design tokens | Builds in Windows CI for x64 and ARM64; the x64 build launches and opens its window in a CI smoke test. Not visually reviewed (no screenshot yet) |
| CI workflow | Runs core tests on Linux and Windows, builds the app for x64 and ARM64 |

## 3. Proposed architecture

See [architecture.md](architecture.md) for the full proposal. Summary:

- **Layers.** A WinUI 3 shell (`Aperture.App`) depends on a platform-neutral `Aperture.Core` of models and contracts. Playback (`Aperture.Playback.Mpv`), persistence (`Aperture.Data`) and media inspection (`Aperture.Media`) implement those contracts. A media server is a later, separate process.
- **Playback engine: libmpv**, evaluated against LibVLC, `Windows.Media.Playback` and a custom FFmpeg pipeline (architecture.md §4.1). libmpv wins on codec coverage, subtitle rendering and image quality.
- **Native integration: decided by measurement in M1.** libmpv cannot render straight into Direct3D 11, so either an OpenGL→D3D11 interop bridge into a `SwapChainPanel` (controls are plain XAML over video) or mpv's own window in a child HWND (fastest, but controls need a separate overlay window) must be chosen. Both are prototyped and measured; the result comes back to adi as a decision.
- **Bindings:** a small hand-written P/Invoke layer over the libmpv client API, rather than an unmaintained third-party wrapper. Dedicated event thread, observed properties, async commands, fixed disposal order.
- **Persistence:** SQLite via `Microsoft.Data.Sqlite`, versioned SQL migrations with `PRAGMA user_version`, backup before migrating.
- **Design system:** tokens in `Theme/Tokens.xaml` from day one; accent colour chosen in M2 against real screenshots.

## 4. Proposed folder and module structure

See [architecture.md §3](architecture.md#3-folder-structure). The skeleton committed in this milestone contains:

```
Aperture.sln · Directory.Build.props · Directory.Packages.props · global.json
.github/workflows/ci.yml
docs/ (this plan, architecture, dependencies, build-and-test, reports)
src/Aperture.App/        App.xaml, MainWindow.xaml, Theme/Tokens.xaml, app.manifest
src/Aperture.Core/       Storage/AppDataLocation.cs
tests/Aperture.Core.Tests/
```

`Aperture.Playback.Mpv`, `Aperture.Data` and `Aperture.Media` are created in the milestones that first need them, so no empty projects sit in the solution.

## 5. Dependency and licensing assessment

See [dependencies.md](dependencies.md). Headlines:

1. **libmpv licensing is the one item that needs a decision from adi before any public release.** Default mpv builds are GPLv2+. Shipping a GPL build means Aperture as distributed must meet GPL terms. An LGPL build (`-Dgpl=false`, with an LGPL FFmpeg configuration) allows a proprietary app if libmpv is dynamically linked, license texts are shipped and the corresponding source is offered. Which third-party Windows build to use, or whether to build our own, is checked in M1. Development is not blocked either way.
2. **Codec patents.** Distributing FFmpeg decoders for H.264/HEVC/AAC can have patent-licensing implications depending on jurisdiction and distribution model. Flagged for a decision before public distribution; this is not legal advice.
3. **Windows App SDK** is under a Microsoft license that permits redistribution with apps. The 2.5.1 metapackage includes components Aperture does not use (AI, ML, Search, Widgets); trimming is evaluated in M5.
4. Everything else planned (Microsoft.Data.Sqlite, CommunityToolkit.Mvvm, Microsoft.Extensions.*) is MIT.

## 6. Risk register

Likelihood and impact are H/M/L estimates.

| # | Area | Risk | L | I | Mitigation | Proven by |
|---|---|---|---|---|---|---|
| R1 | Native playback | libmpv's render API has no D3D11 target, so presenting video inside WinUI needs an interop bridge or a child HWND (verified API limitation; the cost of each workaround is unverified) | H | H | Prototype both paths in M1 and measure; LibVLC kept as fallback engine | M1 |
| R2 | Native playback | `WGL_NV_DX_interop2` support or stability varies by GPU vendor and driver | M | H | Test on at least one NVIDIA, AMD and Intel machine; keep the child-HWND path as fallback | M1 |
| R3 | Native playback | Crashes or leaks from incorrect disposal of `mpv_handle` / render context, or callbacks after free | M | H | One owner per handle, fixed teardown order, `IAsyncDisposable`, open/close soak test | M1, M5 |
| R4 | Native playback | Event-thread to UI-thread marshalling causes stutter or races | M | M | Immutable state snapshots, `DispatcherQueue`, no blocking calls on the UI thread | M1–M2 |
| R5 | UI rendering | Overlay controls over video (airspace) if the child-HWND path is chosen | H if path B | H | Owned transparent overlay window synced to move/resize/DPI; or choose path A | M1 |
| R6 | UI rendering | Swap chain resize, DPI change on moving between monitors, and fullscreen transitions flicker or show stale frames | M | M | Handle `CompositionScaleChanged` and size changes explicitly; test on mixed-DPI setups | M2 |
| R7 | UI rendering | HDR content washed out or not passed through | M | M | Rely on libmpv tone mapping to SDR at first; HDR passthrough is a post-release goal and only feasible on path B (unverified) | M3+ |
| R8 | UI rendering | WinUI 3 startup time and memory footprint feel heavy for a player | M | M | Measure cold start in M2; trim Windows App SDK components in M5 | M2, M5 |
| R9 | Deployment | libmpv and FFmpeg license obligations (GPL vs LGPL) | H | H | Decision from adi; ship license texts and source offer; `THIRD-PARTY-NOTICES.md` | Before release |
| R10 | Deployment | Unpackaged self-contained app is large; MSIX brings install/associations but adds signing and Store/sideload complexity | M | M | Ship unpackaged first; evaluate MSIX in M5 | M5 |
| R11 | Deployment | Unsigned binaries trigger SmartScreen warnings | H | M | Obtain a code-signing certificate before public release | M5 |
| R12 | Deployment | No official Windows libmpv binaries; third-party builds could change, disappear or include unexpected components | M | M | Pin exact build with SHA-256 in `third_party/README.md`; mirror to repo releases; consider building our own | M1 |
| R13 | Codec support | Hardware decoding (d3d11va) availability differs by GPU; AV1/HEVC 10-bit may fall back to slow software decoding | M | M | `hwdec=auto-safe` with automatic software fallback; show decoder in a diagnostics panel; test matrix | M3 |
| R14 | Codec support | Patent licensing for bundled H.264/HEVC/AAC decoders | M | H | Decision before public distribution | Before release |
| R15 | Codec support | Subtitle edge cases (ASS styling, PGS, VobSub, embedded fonts, RTL) | M | M | libass via libmpv; test files for each format | M3 |
| R16 | Windows compatibility | ARM64 libmpv builds may be unavailable or less tested | M | M | CI already builds the app for ARM64; find or build an ARM64 libmpv in M1, otherwise ship x64 only (runs under emulation, unverified performance) | M1 |
| R17 | Windows compatibility | Windows 10 is out of mainstream support; app targets Windows 11 (min build 22000) | L | L | Document Windows 11 as the supported OS | Done |
| R18 | Windows compatibility | Keyboard focus is captured by the video surface, breaking shortcuts; screen readers cannot describe custom controls | M | M | Window-level accelerator handling; AutomationProperties on every custom control; Narrator pass each milestone | M2 |
| R19 | Toolchain | Windows App SDK 2.x and .NET 10 are newer than much published guidance; template details (RID handling, self-contained flags) may differ | M | M | CI builds every push on Windows; adjust from real build output | M0 (CI) |

## 7. Phased implementation plan

Each milestone ends with the 7-part report and a green CI build. A milestone that cannot meet its criteria is reported as blocked rather than skipped.

| Milestone | Goal | Key acceptance criteria |
|---|---|---|
| **M0** Audit and skeleton (this) | Plan, skeleton, docs, CI | This document; skeleton builds in CI |
| **M1** Playback engine spike and decision | Video plays inside the Aperture window | libmpv P/Invoke layer with tests; both render paths prototyped and measured; a file opened from the command line or a file picker plays with sound; pause/seek work; clean shutdown with no leaks over 50 open/close cycles; libmpv build pinned with license recorded. **Ends with a render-path recommendation for adi to approve.** |
| **M2** Player experience | The core screen is designed, not assembled | Design review answered; accent colour and full tokens; auto-hiding transport bar (play/pause, scrubber with time, volume, fullscreen); keyboard shortcuts (Space, ←/→, ↑/↓, F, Esc, M); drag-and-drop; custom title bar; fullscreen and multi-monitor; screenshots at 100% and 150% scaling reviewed |
| **M3** Tracks, subtitles and playback controls | Real-world files are fully usable | Audio/subtitle track menus; external subtitle loading; playback speed; chapters; decoder/diagnostics info; codec test matrix passes |
| **M4** Memory | Aperture remembers | `Aperture.Data` with migrations and tests; settings (volume, last window state, subtitle prefs); resume position per file; recent files; structured rolling logs |
| **M5** First playable release (v0.1) | Something adi can hand to someone | Licensing decision applied; `THIRD-PARTY-NOTICES.md`; packaging decided and automated in CI; crash/error reporting to log; soak tests; definition of done (§8) met |
| M6 | Library scanning | Folder watching, metadata, artwork, never modifying media files |
| M7 | Library experience | Browse/continue-watching screens with real artwork |
| M8 | Bookmarks, playlists, collections | |
| M9+ | Media server process and remote playback | Separate process, authenticated, local network first |

## 8. Definition of done for the first playable release (v0.1)

**Playback**
- Opens files via file picker, drag-and-drop, command line and "Open with".
- Plays a documented test matrix without crashing: MP4 (H.264/AAC), MKV (HEVC 10-bit, multiple audio and ASS/SRT/PGS subtitle tracks), WebM (VP9/Opus), AV1, AVI legacy, MOV, and at least one 4K60 and one HDR10 sample (tone-mapped).
- Hardware decoding where available with automatic, invisible software fallback.
- Unsupported, missing or corrupt files show a clear, calm error; the app keeps running.

**Controls and window**
- Play/pause, seek (scrubber, keyboard, mouse wheel), volume and mute, fullscreen, audio and subtitle track selection, external subtitles, playback speed.
- Controls appear on mouse movement or keyboard input and hide after inactivity during playback; the video is never permanently obstructed.
- Correct behaviour at 100%, 125%, 150% and 200% scaling, across monitors with different DPI, in Snap layouts, and fullscreen.
- Every control reachable by keyboard and named for Narrator.

**Memory**
- Resumes each file from its last position (with a way to start over); remembers volume, window size/position and subtitle preference across restarts.
- Database migrations run safely on upgrade.

**Quality**
- All automated tests pass in CI on the release commit; no warnings (warnings are errors).
- 2-hour continuous playback and 50 open/close cycles without crash or unbounded memory growth.
- Visual review of every screen from real screenshots against the visual quality standard; no placeholder controls.
- Logs contain no media file paths at default verbosity.

**Release**
- libmpv/FFmpeg licensing decision recorded and obligations met; `THIRD-PARTY-NOTICES.md` shipped.
- Installable build produced by CI from a tagged commit, with install and uninstall verified on a clean Windows 11 machine.
- README, build instructions and dependency register up to date.

## Decisions needed from adi

1. **libmpv/FFmpeg license route (GPL app vs LGPL build).** Needed before the first public release, not before M1.
2. **Render path** (architecture.md §4.2). Brought back with measurements at the end of M1.

## Assumptions

- Windows 11 is the only supported OS; Windows 10 is not targeted.
- x64 is the primary architecture; ARM64 is supported if a usable libmpv build exists (R16).
- The app is local-first and offline; no telemetry is planned.
- Unpackaged, self-contained distribution for v0.1 unless M5 finds MSIX clearly better.
