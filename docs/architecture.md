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
| `Aperture.Core` | `net10.0` | Domain records (`MediaItem`, `PlaybackPosition`, `TrackInfo`…), contracts (`IPlaybackEngine`, `ISettingsStore`, `IPlaybackHistory`…), `AppDataLocation` | M0 (`AppDataLocation` only) |
| `Aperture.Playback.Mpv` | `net10.0` (+ Windows-only render bridge) | Hand-written P/Invoke over the libmpv client API, a dedicated event thread, property observation, command dispatch, disposal | M1 |
| `Aperture.Data` | `net10.0` | `Microsoft.Data.Sqlite` connection management, versioned SQL migrations, repositories | M4 |
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
| LibVLC / LibVLCSharp | Mature, LGPL, wide codec support, existing .NET bindings | Less control over rendering quality and subtitle styling; WinUI 3 integration is community-maintained (unverified state) | Fallback if libmpv integration fails the M1 spike |
| `Windows.Media.Playback` (MediaPlayerElement) | Native WinUI control, simplest integration, no native redistribution | Codec coverage depends on installed Windows codecs (HEVC needs a Store extension, MKV/ASS subtitle support is limited); little control over rendering | Rejected for the core player |
| Custom FFmpeg pipeline | Full control | Very large effort (A/V sync, subtitles, rendering) | Rejected |

### 4.2 Rendering path (decision deferred to the M1 spike)

WinUI 3 does not expose a per-control HWND, and libmpv cannot render directly into Direct3D 11 through its render API. Two paths are credible; neither is proven yet.

**Path A: render API → OpenGL → Direct3D 11 → `SwapChainPanel`** (preferred if it performs)
libmpv renders into an OpenGL framebuffer on a hidden window; the framebuffer is shared with a D3D11 texture through `WGL_NV_DX_interop2` (or an EGL/ANGLE equivalent), and that texture is presented in a XAML `SwapChainPanel`. Controls are ordinary XAML layered over the video, which is exactly what "content before interface" needs.
Unverified: driver support for the interop extension across NVIDIA/AMD/Intel, frame pacing, cost of the extra copy, HDR output (likely lost on this path), and behaviour across DPI/monitor changes.

**Path B: mpv's own window (`wid`) in a child HWND**
libmpv renders with its native `gpu-next`/d3d11 output into a child window of the WinUI window. Best performance and HDR potential, least code in the render loop.
Cost: the "airspace" problem. XAML cannot draw over a child HWND, so the auto-hiding controls must live in a separate owned, transparent top-level window kept in sync with moves, resizes, fullscreen and DPI changes. Unverified: how cleanly this behaves with WinUI 3 window management and Snap layouts.

**M1 decision gate.** Both paths are prototyped against the same test clips and measured for: dropped frames at 4K60, CPU/GPU load, resize and fullscreen smoothness, overlay quality, multi-monitor DPI moves, and code size. The result and a recommendation come back to adi before the player UI is built on top of it, because this is the largest architectural decision in the project.

### 4.3 Threading and lifetime (applies to both paths)

- One `mpv_handle` per player session. A dedicated background thread runs `mpv_wait_event`; events are translated into immutable state records and marshalled to the UI through `DispatcherQueue`.
- Property changes (`time-pos`, `pause`, `duration`, `track-list`, `volume`) are observed with `mpv_observe_property`, not polled.
- Commands are issued asynchronously (`mpv_command_async`) so the UI thread never blocks on mpv.
- Disposal order is fixed: stop rendering → free the render context → `mpv_terminate_destroy` → join the event thread. `IPlaybackEngine` is `IAsyncDisposable` so this can be awaited.
- mpv log messages are routed into Aperture's logger at `warn` level by default; file paths are not logged at default verbosity (privacy-conscious default).

### 4.4 Media metadata

During playback, libmpv already reports duration, chapters and the track list, so the player needs no separate probe. FFprobe is deferred to library scanning (M6), run as a separate process with a timeout so a malformed file cannot hang or crash the app. Whether FFprobe is needed at all, versus a headless libmpv instance, is decided in M6.

## 5. Persistence

SQLite through `Microsoft.Data.Sqlite`, one database at `AppDataLocation.DatabasePath` (`%LOCALAPPDATA%\Aperture\aperture.db`, overridable with `APERTURE_DATA_DIR`). Schema changes are numbered SQL migrations tracked with `PRAGMA user_version`, applied in a transaction at startup, with a backup copy taken before any migration runs. No ORM: the schema is small and plain SQL keeps it inspectable. Planned tables: `settings` (key/value), `playback_progress`, `recent_items`, later `library_items`, `bookmarks`.

## 6. Design system foundation

`src/Aperture.App/Theme/Tokens.xaml` holds the initial tokens: charcoal surfaces (`#141517`, `#1C1D20`), three text opacities, a 4 px spacing scale, a four-step type ramp and two corner radii. Screens reference token keys only. The single accent colour is intentionally not chosen yet; it is proposed alongside real screenshots in M2 so it is judged on the actual player, not in isolation. Typography uses Segoe UI Variable (ships with Windows 11), which avoids a font dependency.

## 7. Packaging (proposed)

Development and the first release run **unpackaged and self-contained** (Windows App SDK runtime bundled), distributed as a signed zip or installer. MSIX packaging is evaluated before release for clean install/uninstall and file associations. This is a proposal, not a decision; it is confirmed in M5.
