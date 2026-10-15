# Milestone 5 report: TV series, intro skipping and autoplay

Date: 2026-10-15

**Nothing needs your approval to merge.** No new packages or native libraries were added, and the render path is unchanged. One behaviour is deliberately conservative and is yours to loosen if you want: Aperture only skips an intro by itself when three other episodes agree on it or a chapter confirms it (section 5).

## 1. Implemented

- **Shows, seasons and episodes.** Library episodes are grouped by show (title plus year, so two shows with the same name stay apart) and ordered by season and episode, with specials last. Files that hold two episodes (`S01E01E02`, `S01E01-02`) are recognised and shown as "S1 E1–E2". Copies of the same episode (different releases) stay one episode; the copy from the same folder as the one you are watching is preferred, so a series keeps to one release.
- **Next and previous episode.** A Next episode button appears in the player's controls for library episodes, Shift+N and the media Next key play the next one, and Shift+P, the media Previous key and More options › Previous episode go back. The order is rebuilt from the database after every scan, so it stays right after a rescan, a rename or new episodes being added.
- **Continue Watching for shows.** Home shows one card per show: the episode in progress ("S1 E2 · 12 min left"), or "Up next · S1 E3 · Name" when you finished the last one. Films and other files keep their own cards.
- **Watched state.** An episode is marked watched when you reach confident credits, when you move on from the credits or the end, or by hand (Milestone 4 menu). A watched episode opened again starts from the beginning.
- **Intro, recap and credits detection, by measurement.** For each episode, Aperture decodes the sound (fast, headless, with the libmpv it already uses), takes an audio fingerprint, measures where the picture is dark near the end, and reads chapters. It then compares the episode with up to four others of the same show:
  - The **intro** is the stretch near the start whose sound recurs in other episodes, wherever it sits in each. A cold open of any length is fine; nothing assumes episodes share timestamps.
  - The **credits** are closing music shared with other episodes and/or a long dark picture with text. A scene after the credits is recognised: Skip Credits goes to it, and the next-episode card waits for the real end.
  - A **recap** is at least two short clips before the intro whose sound appears in the previous episodes.
  - Chapters named "Opening", "Intro", "Previously" or "Credits" are used when measurement confirms them.
- **Confidence thresholds.** Every result has a confidence. Below 0.6 nothing is shown. From 0.6 a Skip button is offered. Skipping by itself needs 0.85 (three agreeing episodes, or a confirmed chapter) and is only done when you chose "Automatic" for that kind. An automatic skip happens once, only when playback enters the intro at its start, and leaves "Watch the intro" on screen for 5 seconds to undo it. Seeking back into an intro is respected.
- **Skip button.** "Skip intro", "Skip recap" or "Skip credits" sits bottom right while the segment plays. N presses it.
- **Next-episode card and countdown.** A small card at the bottom right shows the next episode with "Play now" and "Cancel". When autoplay is on it counts down (5, 10, 15, 20 or 30 s; default 10) with a thin progress line. Esc, Cancel, pausing or turning autoplay off stops it for that episode, and the card comes back at the very end without counting. After three episodes in a row played by autoplay alone, it asks "Still watching?" and waits.
- **Autoplay off means off.** With autoplay off (in Settings or for the show), the card offers the next episode and never counts down or plays it.
- **Per-show preferences.** More options › This show: autoplay on or off, intros (Button, Automatic, Never) and recaps, or "Use my usual settings". Settings › TV episodes holds the defaults: autoplay, countdown length, intros, recaps, the credits card, and "Find intros and credits" in the background (with progress, "Checking Night Shift S1 E2 (2 of 3)…").
- **Manual corrections.** More options › Intro and credits opens a side panel showing the times in effect and where they came from. "Starts here" and "Ends here" set a boundary at the current position; "These times are right" confirms; "This file has none" turns a segment off; "Go back to what was found" removes your correction.
- **Corrections stored per file and release.** Segments and corrections are keyed by the file's content fingerprint, so they follow a renamed or moved file, are shared by identical copies, and never move a different release of the same episode. Re-analysis never overwrites a correction.
- **Background analysis.** One file at a time, off the interface thread; the episode you open and the next one go first. Each file's measurements are cached in Aperture's data folder so it is decoded once. A file is analysed again only when the detection method or the episodes it is compared with change. Media files are only read; nothing is written next to them.

## 2. Files changed

- **Core** (`src/Aperture.Core/Series/`): `SeriesCatalog` (shows, order, next/previous, copies), `ContinueWatching`, `MediaSegment` and `SegmentPolicy` (kinds, sources, confidence rules), `EpisodeCues` (`EpisodeCueTracker`, `NextEpisodeCountdown`, autoplay rules), `SeriesPreferences`, `ISeriesStore`. Also `LibraryItem` (episode range, labels, year in the content key), `MediaNameParser` (multi-episode files), `AppSettings` (TV episode settings), `PlayerCommand` and `ShortcutMap` (skip, next, previous), `AppDataLocation` (analysis folder).
- **Data**: migration `0003_series.sql` (`media_segments`, `segment_corrections`, `episode_analysis`, `series_preferences`, `library_items.episode_end`), `SqliteSeriesStore`, and small changes to `SqliteLibraryStore` and `SqliteSettingsStore`.
- **Media** (`src/Aperture.Media/Analysis/`): `AudioPrint`, `PrintMatcher`, `SegmentDetector`, `EpisodeAnalysisPlan`, `EpisodeAnalysisCoordinator`, `AnalysisCache`.
- **Playback.Mpv**: `MpvSignalReader` (sound, dark picture and chapters from headless libmpv).
- **App**: `Services/AppServices.cs`, new `Services/SeriesState.cs`, `MainWindow.xaml.cs`, `Views/PlayerView.xaml` and `.xaml.cs`, new `Views/PlayerView.Episodes.cs`, `Views/HomeView.xaml.cs`, `Views/MediaCardModel.cs`, `Views/SettingsView.xaml` and `.xaml.cs`.
- **Tests**: Core `Series/*`, parser and settings tests; Data `SqliteSeriesStoreTests`; Media `SegmentDetectorTests`, `EpisodeAnalysisTests`, `AnalysisCacheTests`, `SyntheticAudio`; engine `SignalReaderTests`.
- **Tools**: `tools/test-media/make-engine-test-media.sh` (three *Harbour* episodes), `tools/visual-review/make-test-media.sh` (three structured *Night Shift* episodes), `tools/visual-review/Capture-States.ps1` (Episodes step).
- **Docs**: architecture §10, design system (components, keys), dependencies, build and test, README, this report.

## 3. Verification

Run locally on Linux (.NET 10, Ubuntu libmpv 0.37):

| Suite | Result |
|---|---|
| Core | 235 passed, 0 failed |
| Data | 29 passed, 0 failed |
| Media (library and analysis) | 56 passed, 0 failed |
| libmpv engine, with generated media | 64 passed, 0 failed |

What the new tests cover:

- **Different cold opens and releases.** Five synthetic episodes put the same intro at 0.5 s, 30 s, 62 s, 95 s and 2:20; each intro is found at its own time. Each release is compared with episodes from its own folder, and a correction to one release's file does not apply to another release of the same episode.
- **Not skipping story because a timestamp matches.** A special whose story sits exactly where episode 1's intro is gets no intro. With only one other episode agreeing, an intro is offered but never skipped automatically. A duplicate copy of the same file is not counted as agreement, and silence never matches silence.
- **Representative episodes through the real decoder.** `SignalReaderTests` decodes three *Harbour* episodes (cold opens 10, 3 and 15 s; E02 with chapters) through libmpv and finds the intros at 10, 3 and 15 s and the credits at 70 s. E02 reaches 0.95 from its chapter; E01 and E03, with two agreeing peers, are offered but not skipped.
- **Credits:** dark picture plus shared music gives the highest confidence; a dark ending alone is offered but not trusted for autoplay; a dark scene with story after it is not credits; a scene after the credits is not skipped and the next-episode card waits for the real end.
- **Recap:** clips from the previous episodes before the intro are found; an episode without one gets none. (The intro's own theme being taken for a recap was a bug found by the real-decoder test and fixed.)
- **Corrections persist** in the database, win over detection and survive re-analysis; "none" turns a segment off; clearing returns to detection.
- **Autoplay:** no countdown when autoplay is off (globally or for the show), cancelled by Esc/pause and not restarted, the three-in-a-row limit, no countdown at credits that are not confident.
- **Navigation after rescans:** order, next and previous, specials, multi-episode files, copies and same-name shows from different years.
- **Analysis queue:** priorities, skipping already-analysed files, re-analysis when peers change, unreadable files recorded and not retried, background analysis turned off.

The app's C# was compile-checked on Linux against the Windows App SDK reference assemblies, with every XAML event handler checked to exist. On Windows CI every job passes: the tests on Windows and Ubuntu, the app build for x64 and ARM64, and the visual review below.

**A Windows-only problem found and fixed after the first merge (PR #7).** On Windows the credits of the test episodes reached only 0.80 instead of 0.95, because the dark picture was never seen. The cause: FFmpeg sends its log to only one libmpv instance per process, and in the app that is the player, so the analysis reader never received the `blackdetect` filter's lines. The reader now measures darkness itself: libmpv writes a tiny grey copy of the last part of the picture (32 by 18 pixels, 4 frames a second) to a temporary file, and a frame counts as dark when 85% of its pixels are at or below 31 of 255, held for at least a second. The temporary file is deleted afterwards; the media file is only read. A new engine test opens a player in the same process first and still finds the dark ending (it failed with the old reader). Engine tests: 68 passed, 0 failed.

## 4. Visual review

The visual-review job drives the real app through a new Episodes step on three generated *Night Shift* episodes (145 s each, cold opens of 10, 25 and 4 s, the same 20 s opening, credits on black from 2:00). It waits for the analysis to finish (from the log), then checks with UI Automation that:

- "Skip intro" appears during E1's opening and jumps to its end (0:30); shot `70-skip-intro`.
- The next-episode card counts down at the credits; shot `71-next-episode-countdown`.
- Esc puts it away and E1 keeps playing; shot `72-countdown-cancelled`.
- At the very end the card returns without counting, and Play now opens S1 E2; shot `73-next-episode-playing`.
- On E2, letting the countdown run out opens S1 E3 by itself.
- The Intro and credits panel opens from More options; shot `74-intro-credits-panel`.
- With autoplay off, the card shows at the credits without a countdown and E1 is still playing 15 s later; shot `75-autoplay-off`.
- Settings › TV episodes and Home's Up next card; shots `76-settings-episodes` and `77-home-up-next`.

Design review answers for the new parts: the skip button and next-episode card sit bottom right, above the controls when they are visible, so the picture's centre stays clear. Only "Play now" uses the accent. The card has no artwork, gradient or animation beyond its progress line. The corrections panel reuses the side-panel shape of the info and sync panels. 

Result on Windows CI: every check above passed. The log shows each episode's intro found at its own time (0:09, 0:25 and 0:04, confidence 0.80 with two agreeing episodes, so offered and not skipped automatically) and the credits from 2:00 at 0.95. Skip intro moved playback to 0:31, the countdown read "Next episode in 10 s", and with autoplay off the card read "Next episode" with no countdown. Looking at the shots found one layout fault, now fixed: when the player had just opened, the next-episode card could sit over the seek bar, because the controls had not been measured yet; the card now moves up whenever the controls change size.

## 5. Known limitations

- **Automatic skipping is conservative.** It needs three other episodes agreeing on the intro (or a confirmed chapter). A show with only two episodes in the library gets a Skip button but no automatic skip. This is deliberate: the milestone asks not to skip when uncertain.
- **Files outside the library** are not analysed (there are no other episodes to compare them with), but their intro and credits can still be set by hand and are kept.
- **Analysis costs one decode of each episode's sound** (about 2 s for 10 minutes of synthetic media in the development container; real files depend on their audio codec). It runs one file at a time in the background and can be turned off.
- **Picture similarity is not used for intros.** Intros are matched by sound; the picture is only used for dark credits. An intro with no music or a different song each week will not be found and needs a correction.
- **The next-episode card has no thumbnail.** It shows the episode's number and name only.
- **Pausing cancels the countdown for that episode**, rather than holding it; the card stays with Play now.
- **Recaps are found only from the one or two previous episodes**, not from earlier seasons.

## 6. Regression check

- All earlier tests pass unchanged (Core, Data, Media library tests, and the 57 earlier engine tests).
- The library scan, Home rows, the player, shortcuts, panels and Esc order are unchanged for files that are not episodes. The Esc order gains two entries: the Intro and credits panel, then the next-episode card.
- Migration 3 only adds tables and one column, and asks the next scan to re-read files whose names may hold several episodes. A backup is taken before it runs (Milestone 1 rule).
- The earlier visual review steps run before the new Episodes step and are unchanged, except that the three *Night Shift* clips are now 145 s structured episodes instead of 20 s clips.

## 7. Next step

Milestone 6, when you send it. Two small follow-ups from this milestone that could go with it: a thumbnail on the next-episode card, and comparing picture similarity as well as sound for intros with no music.
