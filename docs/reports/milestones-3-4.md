# Milestones 3 and 4 report: refined playback and keyboard, personal media library

Date: 2026-10-15

**Nothing needs your approval to merge.** The render path from Milestone 2 is approved (you approved it during this milestone) and is unchanged. One choice is yours to make later: whether to turn on online details with your own TMDB key (section 5).

## 1. Implemented

### Milestone 3: refined playback and keyboard

- **Customizable shortcuts.** Every player action has a default key (table in [design-system.md §5](../design-system.md)). Settings › Keyboard shortcuts › Customize lets you change a key, add a second one, remove one, or reset one or all. When a key is taken it says by what and offers "Use it here". Keys Windows or Aperture use everywhere (Alt+Tab, Alt+F4, the Windows key, Ctrl+O, Esc, Tab and others) are refused with a reason. The map is saved in the database. Tooltips and menus show the current key.
- **Discoverable.** F1 or ? opens a shortcuts panel over the video, and "Keyboard shortcuts" is in the More menu.
- **No focus conflicts.** Player keys are ignored while any text box has focus (search, the API key box, number boxes), so typing never pauses or seeks. Inside the sync panel or shortcuts panel only Esc and the panel's own key act, so arrow keys move between the panel's buttons instead of seeking. Space always plays and pauses in the player. Esc closes the topmost thing first: a panel, then full screen or the mini-player, then search, then the screen.
- **Speed presets:** 0.25, 0.5, 0.75, Normal, 1.25, 1.5, 1.75 and 2×, from the More menu or [ and ], with \ for normal.
- **Pitch correction** is on by default and can be turned off in Settings (Playback › Keep voices natural at other speeds).
- **Subtitle and sound sync.** Z and X move subtitles 0.1 s earlier or later; Shift+Z and Shift+X do the same for sound. G (or More › Sync and subtitle look) opens a small panel with both offsets, steppers and reset buttons, plus subtitle size, colour and background. The panel sits top right so the subtitles being adjusted stay visible.
- **Subtitle appearance** (size, colour, background box) is also in Settings and applies to every file.
- **Resume or restart.** Settings › When a video was left part-way: Resume (default), Ask each time, or Start from the beginning. "Ask" opens the file paused on the saved frame with Resume and Start over buttons. Card menus also offer "Play from the beginning".
- **Remembered per file:** audio and subtitle track, subtitle and sound offsets, picture shape and speed are saved when you leave a file and restored when you return. Volume and mute are remembered across sessions.
- **Mini-player** (P) stays always on top, keeps only play/pause, the timeline and the return button, and returns to the window's previous size and position.
- **Auto-hiding controls** (Milestone 1 behaviour, unchanged) with the delay from Settings, and they stay up while a panel or menu is open.
- **Mouse:** the wheel over the video changes volume; double-click toggles full screen; single click plays or pauses.
- **Frame steps:** comma and period step one frame back or forward and pause.
- **Playback info panel** (I) from Milestone 2, unchanged.
- **Display and window changes.** The windowed size and position are remembered before full screen or the mini-player. When returning, if that display has been unplugged or its resolution changed, the window is moved onto the display it overlaps most (or the primary one) and shrunk to fit its work area. The subtitle lift above the controls is recomputed whenever the window size changes.
- **Audio-only files** show a quiet placeholder instead of a black rectangle.

### Milestone 4: personal media library

- **Library folders you choose.** Add a folder from the Library screen, Home's first-launch panel, or Settings › Library. The system folder picker is used, then a short question: what is in it (Let Aperture decide, Movies, Shows, Personal videos, Podcasts). Drive roots, your whole user folder, Windows, Program Files, app-data folders, Aperture's own data folder and folders already covered are refused with a reason. Folders can be rescanned or removed (with a confirmation that says files are not deleted).
- **Background scanning with progress and cancel.** Scans run off the interface thread, one at a time, and show progress ("Finding videos… 1,240 so far", "Reading video details… 312 of 1,240") on the Library screen with a Cancel button. Aperture rescans quietly a few seconds after launch. Cancelling keeps everything found so far; the next scan picks up the rest.
- **Categories:** Movies, Shows (episodes), Personal videos and Podcasts, sorted from file and folder names: years, `S01E02`, `1x02`, "Episode 12", dates and camera names, and "Podcasts" folders. Audio files are only picked up in podcast folders.
- **Library screen** (Ctrl+L or the title bar button): kinds with counts, filter (All, Unwatched, In progress, Watched), sort (Recently added, Title, Year), and the title bar search filters the grid as you type.
- **Home** now shows Continue watching, then rows for Movies, Shows, Personal videos and Podcasts (unwatched, newest first, with "See all"), then Recently played. Search on Home covers the library and history.
- **Watched and unwatched:** items count as watched at 95 % (Milestone 1 rule), and any card can be marked watched or unwatched by hand. Watched cards show a small check.
- **Duplicates and quality copies.** Items that are the same film or episode, or have identical contents, are grouped into one card. The copy with the largest picture plays by default; the card says "2 copies" and its menu offers "Play another copy", described as "720p · 1.2 GB" or "Same file".
- **Uncertain matches are shown as uncertain.** Items whose names gave little to go on carry a "Check details" badge. "Edit details" lets you correct the kind, title, year, season, episode and episode title; "Use file name" undoes your corrections. Corrections are stored in Aperture's database and are never overwritten by a scan or a lookup.
- **Optional online details and posters (off by default).** Settings › Library › Look up details online, with your own TMDB API key (link to get one is in Settings). The key is kept in Windows Credential Manager, not in Aperture's database or logs. A confident match fills in the title, year, episode title and a poster; a doubtful one is offered as a suggestion in Edit details ("Use this"). Offline, a rejected key or a TMDB outage change nothing, and the notice in Settings says what happened. Settings shows TMDB's required attribution.
- **Artwork** is the TMDB poster when there is one, otherwise the Windows thumbnail of the file, otherwise the empty artwork fill. Posters are stored in Aperture's data folder, never next to your media.
- **Per-item state:** resume positions (Milestone 1 history) plus the Milestone 3 remembered choices and watched marks, stored per file.
- **Safe migration.** Migration 2 adds the library tables. The database is copied before migrating, the migration runs in one transaction, and a database from a newer Aperture is refused rather than damaged.
- **Your files are never modified.** The scanner opens files read-only and shared, reads 64 KB from each end for a fingerprint, skips hidden and system files, cloud-only placeholders (reading would download them) and sample clips, and does not follow links out of the folder. Nothing is renamed, moved, tagged, deleted or written next to your media.

## 2. Files changed

86 files changed relative to `claude/milestone-0-audit-84kzf1` (which contains Milestone 2).

| Area | Files | Responsibility |
|---|---|---|
| Core | `Input/` (shortcut map, key chords, commands), `Playback/` (sync offsets, subtitle style, remembered choices, speed presets), `Library/` (name parser, copy groups, folder rules, queries, item and folder records, store contracts), `Windowing/WindowPlacement.cs`, `Settings/AppSettings.cs` | Platform-neutral rules for everything above |
| Data | `Migrations/0002_library.sql`, `SqliteLibraryStore`, `SqliteItemStateStore`, settings store additions | Library folders, items, per-file state; migration with backup |
| Media (new project) | `LibraryScanner`, `LibraryCoordinator`, `LibraryLookup`, `TmdbMetadataProvider`, `ArtworkCache`, `FileFingerprint` | Scanning, background coordination, optional lookup, poster cache |
| Engine | `MpvPlaybackEngine` (sync offsets, pitch, subtitle style, frame steps, open paused), `MpvMediaProbe` (headless inspection) | Engine features and library inspection |
| App | `MainWindow`, `Views/LibraryView`, `HomeView`, `SettingsView`, `PlayerView`, `MediaCard`, `MediaCardModel`, `ShortcutsDialog`, `EditDetailsDialog`, `CardActionHandler`, `Services/AppServices`, `ThumbnailCache`, `Interop/WindowsCredentialStore` | Screens, panels, dialogs, wiring, secret storage |
| Tests | 8 new or extended test files in Core, 4 in Data, 6 in the new `Aperture.Media.Tests`, 2 in the engine tests | See section 3 |
| Tools and CI | `ci.yml` (library tests), `make-test-media.sh` (a library folder tree), `Capture-States.ps1` (library, panels, editor, resume steps) | Automated checks |
| Docs | `architecture.md` (§8 library, §9 input), `design-system.md`, `dependencies.md`, `build-and-test.md`, `README.md`, this report | |

**No new dependencies.** Library inspection reuses libmpv headless instead of adding FFprobe. Credential Manager is called through four `advapi32` functions. TMDB is reached with .NET's `HttpClient`, only when you turn it on (dependencies.md, "Online services").

## 3. Verification

VERIFICATION_PLACEHOLDER

## 4. Visual review

VISUAL_PLACEHOLDER

## 5. Known limitations

- **TMDB needs your key.** Without one, the library uses file and folder names only (which is the default and works fully offline). TMDB's terms allow free non-commercial use with attribution; commercial distribution would need their commercial terms.
- **Library folders are scanned on launch and when you ask, not watched live.** A file added while Aperture is open appears after the next scan (Scan button, or the next launch).
- **Name parsing is heuristic.** Unusual naming (for example a film whose title is a year, or episodes numbered only by date) can land in the wrong kind. Those items are marked "Check details" where the parser was unsure, and Edit details fixes them.
- **Network folders** work if Windows can list them, but scanning over a slow share has not been measured. Cloud-only placeholders are skipped on purpose.
- **Very large libraries** were tested with the scanner's batching and in-memory queries, not with a real 50,000-file collection.
- **Shortcuts are per player action only.** App-wide keys (Ctrl+O, Ctrl+L and so on) are fixed.
- **Subtitle styling applies to text subtitles.** Image-based subtitles (PGS, VobSub) keep their own look, and ASS subtitles keep their authored style except for size.
- Still open from Milestone 2: HDR, the libmpv/FFmpeg licence route, and testing on a real PC with a GPU.

## 6. Regression check

REGRESSION_PLACEHOLDER

## 7. Next step

1. Try the library on your own folders: add one through the Library screen and tell me what lands in the wrong place.
2. If you want posters, get a free TMDB key and paste it in Settings › Library.
3. Then the next milestone you give me.
