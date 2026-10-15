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

Separate process:  Aperture.Server (media server for phones and tablets, §12)
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
| `Aperture.Core` | `net10.0` | Domain records and rules (`HistoryEntry`, `WatchProgress`, `HomeShelves`, `AppSettings`, `TimeFormat`), contracts (`IPlaybackEngine`, `ISettingsStore`, `IPlaybackHistory`, `ILibraryStore`, `IMediaProbe`, `ISecretStore`), the shortcut map (`Input/`), sync offsets and subtitle style, the file-name parser and copy grouping (`Library/`), `AppDataLocation` | M0, extended in M1 to M4 |
| `Aperture.Playback.Mpv` | `net10.0` | Hand-written P/Invoke over the libmpv client and render APIs, a dedicated event thread, property observation, command dispatch, track list, diagnostics, failure mapping, disposal | M2 |
| `Aperture.Video` | `net10.0-windows` | The Direct3D 11 video presenter: ANGLE frame source, software fallback, composition swap chain, render thread (§4.2) | M2 |
| `Aperture.Data` | `net10.0` | `Microsoft.Data.Sqlite` connection management, versioned SQL migrations, settings and playback-history repositories | M1 (brought forward from M4) |
| `Aperture.Media` | `net10.0` | Library scanning, the background library coordinator, optional TMDB lookup and the artwork cache (§8) | M4 |
| `Aperture.YouTube` | `net10.0` | YouTube Data API v3 client, oEmbed lookup, Google sign-in (loopback, PKCE) and `YouTubeService`, which keeps Continue watching, Saved and the 30-day details rule (§11). Knows nothing about local playback | M6 |
| `Aperture.Server` | `net10.0`, separate executable | The media server: Kestrel on two ports, its own certificate authority, device sign-in, the shared-library API, direct play, FFmpeg remuxing and conversion, the phone web client (`wwwroot`), and a command line. References `Core` and `Data` only (§12) | M7 |

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

### 4.2 Rendering path (chosen in Milestone 2, approved by adi)

WinUI 3 gives no per-control HWND, and libmpv's render API offers only OpenGL and software output. Two paths were credible (see the Milestone 0 audit). **Milestone 2 implements Path A over ANGLE**, and adi approved it during Milestone 3 and 4.

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

During playback, libmpv already reports duration, chapters and the track list, so the player needs no separate probe. Library scanning (M4) uses a second, headless libmpv instance (`MpvMediaProbe`: `vo=null`, `ao=null`, paused, no subtitles) to read length, picture size and bitrate, with a per-file timeout. FFprobe is not needed, so there is no second native dependency. When libmpv cannot be loaded the scanner still indexes files by name and leaves length and quality blank.

### 4.5 Engine integration (Milestone 2)

- `Aperture.Playback.Mpv` binds libmpv's C API by hand with `[LibraryImport]` (no wrapper package). `MpvLibrary` resolves `libmpv-2.dll` on Windows and `libmpv.so.2` on Linux and refuses any client API other than major version 2.
- mpv is configured as an embedded engine: no user `mpv.conf`, scripts, OSC, OSD, key bindings or terminal output, and `ytdl=no`, so it never fetches anything from the network.
- Before loading, the engine checks the file exists, has a video extension, and is readable and non-empty, so the common failures get specific messages without involving mpv. mpv's `end-file` error codes map to `NotFound`, `Unsupported`, `Damaged` or `Failed` (`MpvFailures`).
- `vo=null` and `ao=null` let the same engine run headless in tests. The engine tests in `tests/Aperture.Playback.Mpv.Tests` play real files on Linux and Windows CI.
- The Media Foundation interim engine from Milestone 1 has been removed.

## 5. Persistence

SQLite through `Microsoft.Data.Sqlite`, one database at `AppDataLocation.DatabasePath` (`%LOCALAPPDATA%\Aperture\aperture.db`, overridable with `APERTURE_DATA_DIR`). Schema changes are numbered SQL migrations tracked with `PRAGMA user_version`, applied in a transaction at startup, with a backup copy taken before any migration runs. No ORM: the schema is small and plain SQL keeps it inspectable. Milestone 1 created `settings` (key/value) and `playback_history` (path, title, position, duration, last played). Migration 2 (Milestone 4) adds `library_folders` (path, kind, last scan), `library_items` (path, parsed and manual details, an optional suggestion, size, modified time, fingerprint, length, picture size, missing flag) and `item_state` (per-file watched override and remembered playback choices: tracks, delays, picture shape, speed). Manual details are stored in their own columns and never overwritten by a scan or a lookup. Later: `bookmarks`.

## 6. Design system foundation

See [design-system.md](design-system.md). Tokens are in `Theme/Tokens.xaml` (with high-contrast counterparts), component styles in `Theme/Styles.xaml`, motion in `Theme/Motion.cs`. Screens reference token keys only. The accent, Lamp `#F2A93B`, was chosen in Milestone 1 and reviewed on real screenshots of the player.

## 7. Packaging (proposed)

Development and the first release run **unpackaged and self-contained** (Windows App SDK runtime bundled), distributed as a signed zip or installer. MSIX packaging is evaluated before release for clean install/uninstall and file associations. This is a proposal, not a decision; it is confirmed in M5.

## 8. Personal media library (Milestone 4)

```
Settings / Library screen ─► LibraryCoordinator (one background worker, cancellable)
                                 │ per folder
                                 ├─► LibraryScanner ─► MediaNameParser (Core) ─► ILibraryStore (SQLite)
                                 │        └─► MpvMediaProbe (headless libmpv) + FileFingerprint
                                 └─► LibraryLookup (only when the user turned it on and gave a key)
                                          └─► TmdbMetadataProvider ─► ArtworkCache (Aperture's data folder)
```

- **Only folders the user picks.** Folders are added through the system folder picker. `LibraryFolderRules` refuses drive roots, the whole user profile, Windows and Program Files, app-data folders, Aperture's own data folder, and folders inside or containing one already added.
- **Read-only.** The scanner opens files read-only and shared, reads 64 KB from each end for a fingerprint, skips hidden and system files, does not follow links out of the folder, skips cloud placeholders (reading them would download them), and ignores sample clips. Aperture never renames, moves, tags or deletes media; corrections live only in its database.
- **Safe to cancel and repeat.** Files are written in batches of 250 per transaction. Unchanged files (same size and modified time) are skipped on the next scan. Items are only marked missing after a scan that saw the whole folder, and an unplugged drive keeps its items. Each path is stored once (a unique, case-insensitive key), so a repeated scan cannot create duplicates.
- **Off the interface thread.** `LibraryCoordinator` runs one scan at a time on the thread pool, queues further requests, and raises progress, change and notice events that the app marshals with `DispatcherQueue`. The library screen refreshes at most once a second while a scan runs.
- **Categories and uncertainty.** `MediaNameParser` reads the file and folder names (year, `S01E02`, `1x02`, dates, camera names, podcast folders) and returns a category, details and a confidence. Uncertain items show a "Check details" badge, and Edit details lets the user correct the kind, title, year, season and episode.
- **Copies.** `CopyGroup.Build` groups items that are the same film or episode, or have the same fingerprint. The largest picture plays by default; the others are offered under "Play another copy".
- **Optional online details.** Off by default. With the user's own TMDB key (stored in Windows Credential Manager, never in the database or logs), `LibraryLookup` searches TMDB for films and shows, one request at a time with 250 ms between them. A confident match fills in details and a poster; a doubtful one is stored as a suggestion the user can accept. Manual details always win. Offline, a rejected key, or a TMDB outage leave the library exactly as it was, and the library works the same with the feature off.

## 9. Input and shortcuts (Milestone 3)

- `ShortcutMap` (Core) maps key chords to `PlayerCommand`s, with defaults, per-command customisation, conflict detection and a list of keys that cannot be assigned (Windows keys such as Alt+Tab, Alt+F4, the Windows key, Shift+F10 and the menu key, plus Aperture's own Ctrl+O, Ctrl+F, Ctrl+L, Ctrl+comma, Alt+Left, Backspace, Esc, Tab and Enter). The map is saved as JSON in `settings`.
- The window turns each key into a chord and asks the map for a command; the player executes it. Keys are ignored while a text box has focus, and while focus is inside the player's sync or shortcuts panels only Esc and the panel keys act, so typing and panel navigation never trigger playback commands.
- Settings › Keyboard shortcuts edits the map; the player's shortcuts panel (F1 or ?) lists the current keys.

## 10. Shows, intro and credits detection, and autoplay (Milestone 5)

```
Library (ILibraryStore) ─► SeriesCatalog.Build (Core) ─► SeriesState (App) ─► Home: Continue Watching
                                    │
                                    ├─► EpisodeAnalysisPlan ─► EpisodeAnalysisCoordinator (one background worker)
                                    │                                │ per file, cached in AnalysisCache
                                    │                                ├─► MpvSignalReader (headless libmpv: sound, dark picture, chapters)
                                    │                                └─► SegmentDetector ─► ISeriesStore (SQLite, by fingerprint)
                                    │
Player ─► EpisodeCueTracker (Core) ◄── segments + the user's corrections + SeriesPreferences + AppSettings
            └─► Skip button, automatic skip with undo, next-episode card and countdown
```

- **Shows and order.** `SeriesCatalog` groups library episodes by show (normalised title plus year, so two shows with the same name stay apart), orders them by season and episode with specials last, folds copies of the same episode into one `CopyGroup`, and understands files that hold several episodes (`S01E01E02`, `S01E01-02`, stored as `episode_end`). Next and previous follow this order and pick the copy from the same folder where there is one. The catalog is rebuilt from the database after every scan, so navigation is always computed from the current library, never from stored positions.
- **Continue Watching.** `ContinueWatching.Build` takes the history, the catalog and watched overrides. For each show it keeps one card: the episode in progress, or "Up next" when the last one was finished. Films and other files keep their own cards.
- **Watched.** An episode counts as watched when the user marks it, when it reaches confident credits, or when they move on from the credits or the end. Opening a watched episode starts from the beginning.
- **Detection by measurement.** For each episode file, `MpvSignalReader` decodes the sound at 5512 Hz faster than real time (to a temporary file in Aperture's data folder, deleted afterwards), writes a 32 × 18 grey copy of the last part of the picture, four frames a second, to another temporary file with libmpv's encoding mode and measures which stretches are dark (FFmpeg's own `blackdetect` cannot be used: FFmpeg logs to only one libmpv instance per process, the player's), and reads its chapters. `AudioPrint` turns the sound into a Haitsma–Kalker fingerprint (one 32-bit hash per 11.6 ms). `SegmentDetector` (version 1) then compares the file with up to four other episodes of the show, same season and nearest first:
  - *Intro*: a stretch in the first 45 % (at most 12 minutes) whose sound recurs in other episodes. `PrintMatcher` tests every offset, so a cold open of any length is fine; nothing assumes the intro sits where another episode's did. Confidence grows with the number of episodes that agree: one 0.65, two 0.8, three or more 0.9. A chapter named "Opening" or "Intro" that measurement confirms is 0.95.
  - *Credits*: in the last 45 % (at most 10 minutes), closing music shared with other episodes and/or a long, nearly dark picture. Both together are 0.95. When the credits end more than 15 s before the end of the file, the rest is treated as a scene after the credits: Skip Credits goes to it and the next-episode card waits for the real end.
  - *Recap*: at least two short clips before the intro whose sound appears in the previous one or two episodes.
- **What confidence allows.** `SegmentPolicy`: below 0.6 nothing is shown; from 0.6 a Skip button is offered; automatic skipping needs 0.85 (three agreeing episodes or a confirmed chapter) and is only done when the user chose "Automatic" for that segment kind, either in Settings or for the show. An automatic skip happens once, only when playback enters the segment at its start, and leaves a "Watch the intro" button for 5 s to undo it. A segment shorter than 3 s is ignored.
- **Different releases.** Segments are stored by content fingerprint, so each release (and each cut) is analysed and corrected on its own; a correction to one release never moves another. A file is analysed again only when the detection method or the episodes it was compared with change (`episode_analysis.signature`).
- **Background work.** `EpisodeAnalysisCoordinator` runs one file at a time on the thread pool. The episode being watched and the one after it go first. Measurements are cached per fingerprint (`AnalysisCache`, `Analysis\*.signals` in Aperture's data folder), so each file is decoded once. A file that cannot be read is recorded and not retried until the library changes. With "Find intros and credits" turned off, only the episode the user opens (and the next one) is analysed.
- **Corrections.** The player's Intro and credits panel lets the user mark a start and end at the current position, confirm what was found, say there is none, or go back to what was found. Corrections are stored in `segment_corrections` by fingerprint, survive re-analysis and rescans, and always win over detection (a confirmed or corrected segment counts as certain).
- **Autoplay.** `EpisodeCueTracker` decides when the next-episode card shows: at confident credits that run to the end, or at the end of the file. The countdown (5 to 30 s, default 10) runs only when autoplay is on for that show; pausing, Esc, Cancel or Close stops it for that episode, and after three episodes in a row played by autoplay alone it waits for the user. Per-show preferences (`series_preferences`) override the Settings defaults for autoplay, intros and recaps.
- **Read-only.** Analysis only reads media files. Nothing is written next to them.

## 11. YouTube (Milestone 6)

```
YouTubeView (App) ─► YouTubeService ─► YouTubeDataApi (key, optional OAuth token) ─► googleapis.com/youtube/v3
     │                    ├─► YouTubeOEmbed (no key) ─► youtube.com/oembed
     │                    ├─► YouTubeAccount ─► GoogleOAuth (loopback + PKCE) ─► ISecretStore (Credential Manager)
     │                    └─► IYouTubeStore (SQLite youtube_videos)
     └─► YouTubePlayerView ─► WebView2 ─► Assets/YouTube/player.html (https://com.aperture.player/) ─► YouTube IFrame Player
```

- **Isolation.** YouTube is its own module and its own two screens. It shares only the settings store, the secret store, the database and the shared `HttpClient` with the rest of the app, and never touches the libmpv engine, the library or local history. A YouTube failure (no network, no key, quota, WebView2 missing) is shown inside the YouTube screens only.
- **Playback** is YouTube's IFrame Player API in WebView2, never stream extraction. The page is served from a virtual host so the embed carries an app identity as its referrer, as YouTube's Required Minimum Functionality asks. The page reports state, time and error codes to the app with `postMessage`; the app sends `load`, `pause` and `stop`. Nothing is drawn over the player.
- **Playability.** Before playing, `YouTubeService.LookupAsync` reads `videos.list` status (embeddable, privacy, age restriction, region, live/upcoming, processing) when a key is set, or oEmbed otherwise (404 not found, 401 not embeddable). `YouTubePlayabilityRules` turns that, or a player error code (100, 101/150, 153), into one explanation with Watch on YouTube.
- **Stored data.** `youtube_videos` holds Aperture's own records (video ID, position, last watched, saved) and a dated copy of the title, channel, thumbnail and length. `YouTubeDataRules` refreshes the copy after 25 days and deletes it at 30, as the Developer Policies require.
- **Account.** Optional and read-only (`youtube.readonly`), with the user's own OAuth client. Tokens live in Credential Manager; an expired or revoked refresh token signs the user out instead of failing silently.
- **Autoplay** for YouTube is a separate setting (`youtube.autoplay`) using the same `NextEpisodeCountdown` as episodes, but never the episode autoplay setting.

Setup, quota and limits for users: [youtube.md](youtube.md).

## 12. Media server (Milestone 7)

```
Phone browser ──HTTPS :8443──► Aperture.Server (separate process, keeps running when the app closes)
     │                            ├─ network gate (NetworkAccess: loopback, home network; private network only if allowed)
     └──HTTP :8080 (setup page,   ├─ DeviceAuth (passphrase → device token cookie) · LoginThrottle
         certificate only)        ├─ ApiEndpoints ─► SharedLibrary ─► SqliteLibraryStore / SqlitePlaybackHistory
                                  ├─ MediaEndpoints ─► direct file (Range) │ ConversionManager ─► FFmpeg (HLS or fMP4)
Aperture.App ─ Settings ─────────►├─ SetupEndpoints /control/* (loopback + token from Server\server.json)
   (MediaServerHost: start,       └─ CertificateAuthority (name-constrained, user cert store) · KeepAwake
    stop, status; SqliteServerStore edits settings, folders, devices)
```

- **Separate lifecycles.** The app never hosts the server. `MediaServerHost` starts `Server\Aperture.Server.exe run --background` hidden and talks to it only through `server.json` (process id, ports, a per-start control token) and the loopback control address. The server holds `server.lock` so only one copy runs. Both processes share Aperture's SQLite database (WAL mode, each with its own connections); the server reads its settings with a 3-second cache, so changes in Settings apply without a restart (ports excepted). "Start with Windows" is an `HKCU\…\Run` entry that runs `Aperture.exe --media-server`, which starts the server and exits without a window.
- **Core rules** (`Aperture.Core/Server`): `NetworkAccess` (address zones), `Passphrase` and `DeviceToken`, `SharedMedia` (what may be served) and `RemoteProgress` (what a device may record), `StreamPlanner` (direct play, remux, convert or unavailable, with the bitrate ladder), `ServerSettings`, `ServerStateFile` and `ServerControlClient`. All platform-neutral and unit-tested.
- **Data**: migration 5 adds `server_shared_folders` (folder ids, removed with their folder) and `server_devices` (token hash, name, last seen, expiry, last address). Settings and the passphrase hash are `server.*` rows of `settings`.
- **Streams.** Direct play serves the file with range requests at `/media/{token}/file.ext`; the token is random per device and item. Remux and convert run FFmpeg with arguments passed one by one (no shell) and the input as `file:` + path: HLS (EVENT playlist, 4 s MPEG-TS segments, written with `temp_file`) for clients with HLS, or one fragmented MP4 piped from FFmpeg's standard output for those without. Conversions live in `Server\Conversions\<id>` and are reaped when unused. The H.264 encoder is chosen by a real one-frame test encode (NVENC, Quick Sync, AMF, then x264).
- **Progress sync.** The phone posts its position every 10 s, on pause and when the page is hidden. The server clamps it to the library's length and writes the same `playback_history` the desktop uses, unless the desktop's history setting is off.
- **Logging.** `Logs\server.log`, with ASP.NET Core's own categories at Warning so request addresses (which hold tokens) aren't written.

User guide, laptop behaviour and the security design: [media-server.md](media-server.md).

