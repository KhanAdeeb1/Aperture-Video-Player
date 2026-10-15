# Milestone 1 report: design system and first impression

Date: 2026-10-15

## 1. Implemented

- **Design system**, documented in [design-system.md](../design-system.md): identity, a design review for each state, colour tokens with measured contrast ratios, a high-contrast mapping, type ramp, spacing, sizes, radii, motion, components, keyboard map, overlay timing, windowing and accessibility rules. Tokens live in `Theme/Tokens.xaml`, component styles in `Theme/Styles.xaml`, durations in `Theme/Motion.cs`.
- **All eight requested states, in the real app** (none mocked):
  1. First launch with no media: a composed empty Home with the mark, one sentence and one "Open file" button.
  2. File open: the Windows file picker filtered to video types, Ctrl+O, drag and drop onto the whole window with a drop overlay, and a file passed on the command line.
  3. Home: Continue Watching (real stored positions, progress bars and time left), Recently Played as a responsive grid, search in the title bar (Ctrl+F) with result and no-result states, shell thumbnails at one 16:9 proportion, and a hover ring. Each card has a context menu with Play, Show in folder and Remove from history.
  4. Playback with controls: timeline with a time tooltip, play/pause, mute and volume, elapsed and remaining time, a More menu (speed, Show in folder), mini-player and full-screen buttons, and a resume notice with "Start over".
  5. Playback with controls hidden: the overlay, title bar, caption glyphs and pointer fade after the idle delay from Settings.
  6. Mini-player: the Windows compact-overlay window (always on top) with play/pause, timeline and return. Leaving it restores the previous window size and position.
  7. Settings: resume playback, hide-controls delay, remember history, clear history (with confirmation), version, and a button that opens the data folder.
  8. Loading, empty, error and unsupported: an opening state, empty Home, no search results, file not found, not a video file, unsupported format, and damaged file, all in one panel style.
- **Real playback** through an interim Windows Media Foundation engine behind the `IPlaybackEngine` interface. This is not the engine decision. libmpv replaces it later, and the render path still needs your approval ([architecture.md §4.5](../architecture.md)).
- **SQLite storage**, brought forward from M4 because Continue Watching needs real positions: numbered migrations, a backup before migrating, playback history, and settings. Progress is saved every 5 s while playing and on close. A video counts as finished at 95% or with 20 s left.
- **Keyboard and window behaviour**: the full keyboard map in design-system.md §5, genuine full screen through the full-screen presenter, a 1280 × 800 DPI-scaled first window, and a 640 × 400 minimum size.
- **Diagnostics**: `Logs\aperture.log` in the data folder, including startup failures with full exception detail.
- **Visual review automation on Windows CI**: synthetic test clips generated with FFmpeg (CI only), and a UI Automation script that drives the app through every state, checks that playback actually advances, and captures 27 screenshots.

## 2. Files changed

65 files changed relative to `claude/milestone-0-audit-84kzf1`.

| Area | Files | Responsibility |
|---|---|---|
| Core | `Playback/*`, `Media/MediaFileTypes.cs`, `Library/*`, `Settings/*` | Engine contract, time formatting, file types, watch progress rules, home shelves, progress recording, settings model |
| Data (new project) | `src/Aperture.Data/*`, `Migrations/0001_initial.sql` | SQLite database, migrations, history and settings stores |
| App theme | `Theme/Tokens.xaml`, `Styles.xaml`, `Motion.cs`, `App.xaml` | Design tokens and component styles |
| App views | `Views/HomeView`, `PlayerView`, `SettingsView`, `MediaCard`, `IdleOverlay`, `SecondsToClockConverter` | The screens and their states |
| App shell | `MainWindow.xaml(.cs)`, `App.xaml.cs` | Title bar, navigation, file open, drag and drop, full screen, mini-player, keyboard, orderly close |
| App services | `Services/*`, `Playback/MediaFoundationEngine.cs`, `Interop/*` | Composition root, logging, thumbnails, Explorer actions, interim engine, pointer hiding |
| Tests | `tests/Aperture.Core.Tests/*` (new folders), `tests/Aperture.Data.Tests/*` (new project) | 50 core tests and 9 data tests |
| CI and tools | `.github/workflows/ci.yml`, `tools/visual-review/*` | Data tests, test media job, visual review job |
| Docs | `docs/design-system.md` (new), `README.md`, `architecture.md`, `build-and-test.md`, `dependencies.md`, this report | Design system and updated architecture and dependency notes |

New dependency: `Microsoft.Data.Sqlite` 10.0.13 (MIT), already planned in M0 for storage. Media Foundation is part of Windows. FFmpeg is used only on the CI Linux runner to make test clips and is not shipped. All three are recorded in [dependencies.md](../dependencies.md).

## 3. Verification

| What | Where | Result |
|---|---|---|
| `dotnet test tests/Aperture.Core.Tests` and `tests/Aperture.Data.Tests` | This Linux environment | Passed 50 and 9, failed 0 |
| Same | GitHub Actions `ubuntu-latest` and `windows-latest` (run 11, commit `cd68389`) | Success on both |
| App build, x64 and ARM64, warnings as errors | `windows-latest`, run 11 | Success on both |
| Launch smoke test | `windows-latest` x64, run 11 | Success |
| Visual review script: every state driven through UI Automation | `windows-latest` x64 at 1920 × 1080, run 11 | All steps completed. Each of 7 clips was confirmed playing (for example, "Night Drive.mp4: position advanced 1.5 -> 3.5 s of 1080 s"). 27 screenshots were captured. |

Run 10 found a real launch crash: a `GridLength` row height bound to a `Double` resource. It was fixed in `cd68389`, and the smoke test has passed since.

Not executed: running the app interactively on a real Windows 11 machine, display scaling above 100%, multiple monitors, ARM64 at runtime, high-contrast themes, and screen readers.

## 4. Visual review

The screenshots exist but I have not looked at them yet. CI captured all 27 states (first launch at two sizes, open dialog, playback with controls visible and hidden, opening, More menu, paused, mini-player and idle mini-player, full screen with controls visible and hidden, Home at 1280, 1920 and 1024 wide, hover, keyboard focus, search results, no results, settings, and four error states). They are in the `visual-review` artifact of CI run 11. This session's network policy refuses the host that serves artifact downloads, so I could not open them, and I make no claim here about how the screens look.

What I would check first: icon glyphs rendering (the mini-player and speed icons especially), thumbnail extraction for the synthetic clips, card spacing at 1024 wide, and the readability of the scrim and timeline over bright frames.

## 5. Known limitations

- The visual review above has not been done by me yet; see section 4.
- Playback uses the interim Media Foundation engine, so formats Windows cannot decode (the FFV1 test file, for example) show the unsupported-format state. libmpv will remove most of these cases.
- Light theme is not designed; the app always uses the dark theme.
- Display scaling above 100% is designed for (effective pixels, Per-Monitor V2) but not verified, because CI runs at 100%.
- Library sections backed by folder scanning, metadata artwork, audio and subtitle track menus, and screen transitions are deferred (design-system.md §6).
- The solution's default configurations and the Node.js 20 warning in CI, both noted in M0, are unchanged.

## 6. Regression check

Nothing built in M0 was removed. `AppDataLocation` and its 7 tests are unchanged and still pass. The M0 shell's dark surface and extended title bar are kept and extended. All M0 CI jobs still run and pass, alongside the new ones.

## 7. Next step

1. You review the screenshots (CI run 11, `visual-review` artifact) and the design-system document, and tell me what to change before we go further.
2. Then the libmpv integration spike, which was M0's planned M1: pin a build, prototype both render paths, and bring back a render-path recommendation for approval.

Decisions still open: the libmpv and FFmpeg license route, and the render path.
