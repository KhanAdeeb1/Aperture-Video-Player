# Aperture architecture

Status: proposed in Milestone 0. Items marked **unverified** are design intentions that have not yet been proven by a running build; each names the milestone that proves or replaces it.

## 1. Shape of the system

```
┌──────────────────────────────────────────────────────────────┐
│ Aperture.App  (WinUI 3, Windows only)                         │
│   Views (XAML) · ViewModels · Controls · Theme tokens         │
│   Composition root (DI) · Window / fullscreen / input         │
│   Video surface host (render bridge, see §4)                  │
└───────────────┬──────────────────────────────────────────────┘
                │ depends on interfaces only
┌───────────────▼──────────────────────────────────────────────┐
│ Aperture.Core  (net10.0, no UI, no native code)               │
│   Domain models · Service contracts · App data location       │
└───────▲──────────────────────▲──────────────────────▲────────┘
        │ implements           │ implements           │ implements
┌───────┴────────────┐ ┌───────┴────────────┐ ┌───────┴────────────┐
│ Aperture.Playback  │ │ Aperture.Data      │ │ Aperture.Media     │
│ .Mpv               │ │ SQLite repositories│ │ Probing, scanning, │
│ libmpv P/Invoke,   │ │ + migrations       │ │ artwork (later)    │
│ event loop, engine │ │                    │ │                    │
└────────────────────┘ └────────────────────┘ └────────────────────┘

Later, separate process:  Aperture.Server (media server, remote streaming)
```

Rules that hold the shape together:

- **Dependencies point inward.** `App` references everything; `Playback.Mpv`, `Data` and `Media` reference only `Core`; `Core` references nothing.
- **No XAML type crosses into a service, and no native handle crosses into a ViewModel.** The one deliberate exception is the video surface host, which lives in `App` because it binds a XAML element to a native render target.
- **Platform-neutral where possible.** `Core`, `Data`, and the non-rendering parts of `Playback.Mpv` target plain `net10.0`, so their tests also run on Linux CI. Anything that needs Windows targets `net10.0-windows…`.
- **One composition root.** Services are created in `App` with `Microsoft.Extensions.DependencyInjection` (added when the second service appears, not before).

## 2. Modules

| Module | Target | Responsibility | Created in |
|---|---|---|---|
| `Aperture.App` | `net10.0-windows10.0.22621.0` | WinUI shell, screens, input, window management, video surface host | M0 (empty shell) |
| `Aperture.Core` | `net10.0` | Domain records and rules (`HistoryEntry`, `WatchProgress`, `HomeShelves`, `AppSettings`, `TimeFormat`), contracts (`IPlaybackEngine`, `ISettingsStore`, `IPlaybackHistory`), `AppDataLocation` | M0, extended in M1 |
| `Aperture.Playback.Mpv` | `net10.0` | Hand-written P/Invoke over the libmpv client and render APIs, a dedicated event thread, property observation, command dispatch, track list, diagnostics, failure mapping, disposal | M2 |
| `Aperture.Video` | `net10.0-windows` | The Direct3D 11 video presenter: ANGLE frame source, software fallback, composition swap chain, render thread (§4.2) | M2 |
| `Aperture.Data` | `net10.0` | `Microsoft.Data.Sqlite` connection management, versioned SQL migrations, settings and playback-history repositories | M1 (brought forward from M4) |
| `Aperture.Media` | `net10.0` | Media inspection (libmpv track list during playback; FFprobe for library scanning), folder scanning, artwork | M6 |
| `Aperture.Server` | separate executable | Background media server for remote/cross-device playback | After first release |

Tests mirror the modules: `tests/Aperture.<Module>.Tests`. Tests that need the native libmpv library are integration tests and are reported as skipped, never as passed, when the library is absent.

## 3. Folder structure

```
/
├─ Aperture.sln
├─ Directory.Build.props        shared compiler settings (nullable, analyzers, warnings as errors)
├─ Directory.Packages.props     central package versions
├─ global.json                  .NET SDK pin (10.0.100, roll forward within the feature band)
├─ .github/workflows/ci.yml     Linux + Windows core tests; Windows app build (x64, ARM64)
├─ docs/
│  ├─ architecture.md           this file
│  ├─ build-and-test.md         how to build, run and test
│  ├─ dependencies.md           dependency and license register
│  ├─ milestone-0-audit-and-plan.md
│  └─ reports/                  one report per milestone
├─ src/
│  ├─ Aperture.App/
│  │  ├─ Theme/                 design tokens and control styles
│  │  ├─ Views/                 one folder per screen (from M2)
│  │  ├─ ViewModels/
│  │  ├─ Controls/              Aperture's own components (transport bar, scrubber…)
│  │  └─ Video/                 video surface host / render bridge (M1)
│  ├─ Aperture.Core/
│  │  ├─ Storage/
│  │  ├─ Playback/              engine contracts and state records (M1)
│  │  └─ Library/               (M6)
│  ├─ Aperture.Playback.Mpv/    (M1)
│  │  ├─ Interop/               P/Invoke declarations, marshalling helpers
│  │  └─ MpvPlaybackEngine.cs
│  └─ Aperture.Data/            (M4)
│     └─ Migrations/            numbered .sql files embedded as resources
├─ tests/
│  └─ Aperture.Core.Tests/
└─ third_party/
   ├─ README.md                 where native binaries come from, versions, hashes (M1)
   └─ bin/                      fetched by script; git-ignored
```

## 4. Playback engine and native integration

### 4.1 Engine choice

| Option | Strengths | Weaknesses | Verdict |
|---|---|---|---|
| **libmpv** (recommended) | Broad format/codec coverage through FFmpeg; libass subtitle rendering; high-quality scaling and tone mapping; d3d11va/hardware decoding; stable C client API (`client.h`, API version 2.5 on mpv master as checked 2026-10-15); property/observer model maps cleanly to MVVM | Render API supports only OpenGL and software rendering (verified in `include/mpv/render.h` on mpv master, 2026-10-15), so integration with WinUI's Direct3D composition needs a bridge; GPL/LGPL build choice matters (see dependencies.md); no official Windows binaries from the mpv project | Proposed engine |
| LibVLC / LibVLCSharp | Mature, LGPL, wide codec support, existing .NET bindings | Less control over rendering quality and subtitle styling; WinUI 3 integration is community-maintained (unverified state) | Fallback if libmpv integration had failed (it did not; see §4.2 and §4.5) |
| `Windows.Media.Playback` (MediaPlayerElement) | Native WinUI control, simplest integration, no native redistribution | Codec coverage depends on installed Windows codecs (HEVC needs a Store extension, MKV/ASS subtitle support is limited); little control over rendering | Rejected for the core player |
| Custom FFmpeg pipeline | Full control | Very large effort (A/V sync, subtitles, rendering) | Rejected |

### 4.2 Rendering path (chosen in Milestone 2, awaiting adi's approval)

WinUI 3 gives no per-control HWND, and libmpv's render API offers only OpenGL and software output. Two paths were credible (see the Milestone 0 audit). **Milestone 2 implements Path A over ANGLE**, and this is flagged for adi's approval in the Milestone 2 report.

```
libmpv render API (OpenGL ES 3)                     Aperture.Playback.Mpv / MpvRenderContext
   │  draws into an EGL pbuffer
ANGLE (EGL + GLES on Direct3D 11)                    Aperture.Video / AngleFrameSource
   │  pbuffer wraps a D3D11 texture (EGL_D3D_TEXTURE_ANGLE) on Aperture's own device
   │  CopyResource
DXGI composition swap chain (B8G8R8A8, flip model)   Aperture.Video / VideoPresenter
   │  ISwapChainPanelNative.SetSwapChain
XAML SwapChainPanel, controls drawn above it          Aperture.App / SwapChainPanelBridge, PlayerView
```

Why this path:
- **Controls are plain XAML over the video.** The auto-hiding control bar, menus, info panel and toasts are ordinary XAML with Mica/Acrylic, keyboard focus and UI Automation. Path B (mpv's own child window) would need a second transparent top-level window kept in sync with moves, resizes, Snap, full screen and DPI, which is the least reliable part of either design.
- **No vendor-specific interop.** ANGLE translates GL to D3D11 itself, so there is no dependency on `WGL_NV_DX_interop2` driver support. ANGLE shares the D3D11 device Aperture creates (`EGL_ANGLE_device_d3d`), so the frame never leaves the GPU; the only extra cost is one GPU texture copy per frame.
- **It runs on WARP.** The same code works on the CI runners' software D3D11 device, so CI renders real frames and checks their pixels.
- **A fallback exists.** If ANGLE cannot start (no D3D11 feature level 10, a broken driver), the presenter uses libmpv's software renderer and uploads frames with `UpdateSubresource`. `APERTURE_VIDEO_RENDERER=software` forces it for diagnosis. The playback-info panel shows which renderer is active.

Costs accepted:
- **HDR is lost.** libmpv's GL output on an 8-bit swap chain tone-maps to SDR. An HDR path would need Path B or a future D3D11 render API in libmpv.
- One GPU copy per frame and a second GL→D3D translation layer. Not yet measured at 4K60 on real hardware (CI has no GPU).
- `Avalonia.Angle.Windows.Natives` is another native dependency (BSD-3-Clause; see dependencies.md).

The presenter's render thread owns the D3D11 device, ANGLE display and context, and the swap chain. It waits on libmpv's update callback and redraws only for new frames or size changes. On device loss it rebuilds the pipeline, up to three times, before falling back to an error.

### 4.3 Threading and lifetime

- One `mpv_handle` per player session. A dedicated background thread runs `mpv_wait_event`; events are copied into managed records and state changes are posted to the UI through the `SynchronizationContext` captured when the engine is created. Position and duration are stored atomically instead, so the UI's 4 Hz refresh reads them without a post per frame.
- Property changes (`time-pos`, `pause`, `duration`, `track-list`, `volume`) are observed with `mpv_observe_property`, not polled.
- Commands and property writes are asynchronous (`mpv_command_async`, `mpv_set_property_async`), so the UI thread never blocks on mpv.
- Disposal order is fixed: stop the render thread and free the render context (`VideoPresenter.Dispose`) → stop the event thread with `mpv_wakeup` and join it → `mpv_terminate_destroy`. The event loop exits on *any* event once stopping, because libmpv clears a pending wakeup whenever it returns an event; waiting for the wakeup's own event hung intermittently in testing. If the thread does not stop within 5 seconds the handle is leaked and an error logged, rather than destroying it under a running thread.
- mpv log messages are routed into Aperture's logger at `warn` level by default; the file path is replaced with `<file>` in Aperture's own log lines.

### 4.4 Media metadata

During playback, libmpv already reports duration, chapters and the track list, so the player needs no separate probe. FFprobe is deferred to library scanning (M6), run as a separate process with a timeout so a malformed file cannot hang or crash the app. Whether FFprobe is needed at all, versus a headless libmpv instance, is decided in M6.

### 4.5 Engine integration (Milestone 2)

- `Aperture.Playback.Mpv` binds libmpv's C API by hand with `[LibraryImport]` (no wrapper package). `MpvLibrary` resolves `libmpv-2.dll` on Windows and `libmpv.so.2` on Linux and refuses any client API other than major version 2.
- mpv is configured as an embedded engine: no user `mpv.conf`, scripts, OSC, OSD, key bindings or terminal output, and `ytdl=no`, so it never fetches anything from the network.
- Before loading, the engine checks the file exists, has a video extension, and is readable and non-empty, so the common failures get specific messages without involving mpv. mpv's `end-file` error codes map to `NotFound`, `Unsupported`, `Damaged` or `Failed` (`MpvFailures`).
- `vo=null` and `ao=null` let the same engine run headless in tests. The engine tests in `tests/Aperture.Playback.Mpv.Tests` play real files on Linux and Windows CI.
- The Media Foundation interim engine from Milestone 1 has been removed.

## 5. Persistence

SQLite through `Microsoft.Data.Sqlite`, one database at `AppDataLocation.DatabasePath` (`%LOCALAPPDATA%\Aperture\aperture.db`, overridable with `APERTURE_DATA_DIR`). Schema changes are numbered SQL migrations tracked with `PRAGMA user_version`, applied in a transaction at startup, with a backup copy taken before any migration runs. No ORM: the schema is small and plain SQL keeps it inspectable. Milestone 1 created `settings` (key/value) and `playback_history` (path, title, position, duration, last played); later `library_items`, `bookmarks`.

## 6. Design system foundation

See [design-system.md](design-system.md). Tokens are in `Theme/Tokens.xaml` (with high-contrast counterparts), component styles in `Theme/Styles.xaml`, motion in `Theme/Motion.cs`. Screens reference token keys only. The accent, Lamp `#F2A93B`, was chosen in Milestone 1 and reviewed on real screenshots of the player.

## 7. Packaging (proposed)

Development and the first release run **unpackaged and self-contained** (Windows App SDK runtime bundled), distributed as a signed zip or installer. MSIX packaging is evaluated before release for clean install/uninstall and file associations. This is a proposal, not a decision; it is confirmed in M5.
