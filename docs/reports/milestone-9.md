# Milestone 9 report: advanced features and reliability

Date: 2026-10-15

**What's needed from you: two decisions and a quick try.**

1. **Local transcripts.** Should Aperture add speech-to-text that runs on your PC? It needs a new dependency (whisper.cpp plus a speech model of 75 MB to 1.5 GB) and keeps the CPU busy for minutes per hour of video. It is not built until you approve it.
2. **Online subtitle search.** Should Aperture search OpenSubtitles? It sends a fingerprint of the video and its title to their service, needs an account key, and would ask for your consent the first time. It is not built until you say yes.
3. **Try it on your PC.** Point at the timeline of a long video, press B, Shift+B, Shift+L twice and D. CI drives each of these in the real app and screenshots them, but nobody has watched them with their own eyes yet.

No packages were added.

## Assessment: what earned its place

Each of the nine features was weighed before any code was written. The rule was adi's: nothing is added just because it is technically possible.

| # | Feature | User value | Cost to build and keep | Resources | Privacy | Decision |
|---|---|---|---|---|---|---|
| 1 | Seek-preview pictures | High: finding a scene without scrubbing blind | Medium. A second libmpv instance; no new code paths in normal playback | One decoder on two threads while pointing, at most 120 small pictures in memory, nothing on disk | Stays on the PC | **Built**, with a Settings switch |
| 2 | Bookmarks and notes | High for lectures, films being studied, long videos | Low. One table, one panel | None | Local database only | **Built** |
| 3 | A–B loop | Medium: music, language practice, sport | Very low; mpv does the looping | None | n/a | **Built** |
| 4 | Clearer dialogue, even volume | High: the most common complaint about films at home | Low; FFmpeg filters already in libmpv | A few percent of one core | Processed on the PC | **Built**, off by default |
| 5 | Local transcripts and search | Medium | High: a new native dependency, model downloads, a long-running job | Heavy: minutes of CPU per hour of video | Fine if local | **Waiting for your decision** (new dependency) |
| 6 | Automatic subtitle matching | High for downloaded releases | Low for local files; online needs an account key, consent and a service to depend on | Lists two or three folders | Local: none. Online: sends file details | **Built for local files.** Online is waiting for your decision |
| 7 | Duplicate and quality copies | Already covered: Milestone 4 groups copies, shows quality and offers "Play another copy" | — | — | — | **Not changed.** No new need was found that justified more |
| 8 | Per-content preferences | Medium: shows in another language, a quiet film | Low | None | Local database only | **Built**: per file and per show |
| 9 | Error diagnostics and recovery | Medium: problems become fixable instead of mysterious | Low | A 2 MB cap on the log | The report removes paths, names and your user name, and only goes to your clipboard | **Built** |

Nothing in this milestone is AI. Clearer dialogue and even volume are ordinary sound filters (`dialoguenhance` and `dynaudnorm` from FFmpeg), and the captions say so. Nothing is uploaded anywhere.

## 1. Implemented

- **Seek previews.** Pointing at the timeline shows a small picture of the video at that point, with the time under it.
  - The pictures come from a second, hidden libmpv instance that only reads the file. It has no sound, no subtitles, software decoding on two threads, and keyframe seeks, so each picture costs one decoded frame.
  - Positions snap to a grid of at most 400 slots per file, so moving along the bar reuses pictures already made. The newest request replaces any waiting one, so a fast sweep doesn't queue work.
  - Up to 120 pictures stay in memory and are never written to disk.
  - It starts the first time you point at the timeline and is closed in the background when you leave the file. Normal playback pays nothing for it.
  - It is shown only for moving pictures at least 10 s long. Sound files and music with cover art get the time only.
  - Settings › Playback › "Show a picture when pointing at the timeline" turns it off.
- **Bookmarks and notes.**
  - B bookmarks the current moment and says so. A second B within a second of an existing bookmark says it is already there.
  - Shift+B, or More › Bookmarks and notes, opens a side panel listing the file's bookmarks, earliest first.
  - Each row has the time (click to jump), a note field (saved when focus leaves it; Enter returns the keys to the video) and Delete.
  - Bookmarks show as small ticks just above the timeline.
  - They are stored by path in Aperture's database and never touch the file.
- **A–B loop.**
  - Shift+L marks the start, then the end, then stops looping. More › Loop a section has the same three steps as separate items.
  - A loop shorter than half a second is refused with a notice.
  - While a loop is set, a Lamp chip by the time reads "Loop 1:02 – 1:15", and clicking it stops the loop. The looped stretch is drawn as a band on the timeline.
  - Loops belong to the open file only and are not saved.
- **Clearer dialogue and even volume.**
  - D and E toggle them, as do More › Sound and the defaults in the new Settings › Sound group.
  - Clearer dialogue lifts voices above music and effects. Surround sound is mixed to stereo first.
  - Even volume evens out loud and quiet scenes.
  - If a libmpv build lacks the filters, Aperture plays the sound unprocessed and says so instead of failing silently.
- **Subtitle matching (local files).** Aperture now finds subtitles that mpv's own matching missed:
  - files named like the video plus a language, "forced" or "SDH" (`Film.English.forced.srt`);
  - a shorter name the video's name starts with (`Show.S01E02.srt` for `Show.S01E02.1080p.WEB.mkv`);
  - the same episode code in a season folder (`S01E02` or `1x02`);
  - anything beside the only video in a folder;
  - release folders: `Subs`, `Sub`, `Subtitles` and `Subtitle`, including `Subs/<video name>/2_English.srt`.

  The track menu shows each found track's language, plus SDH or Forced. Folders are only listed, never changed, and the lists are capped so a huge folder doesn't delay playback.
- **Per-content choices.**
  - Each file remembers its own sound options, alongside its tracks, offsets, picture shape and speed from earlier milestones.
  - Each show remembers the audio and subtitle language, speed and sound options you last used. The next episode starts with them.
  - The order is the file's own choice, then the show's, then Settings.
- **Diagnostics and recovery.**
  - If a file that was playing fails, Aperture reopens it once where it was, with a notice, before showing an error.
  - The error panel has Try again (except for files Aperture can't play at all) and "Copy details for a bug report".
  - The report holds versions, the problem, and the last 60 lines of Aperture's log. File and folder paths, the media file's name and your Windows user name are removed. It is only put on the clipboard; Aperture sends nothing.
  - Settings › About has the same Copy report button.
  - The log now rolls over to `aperture.old.log` at 2 MB, so it can't grow without limit.

## 2. Files changed

- **Core:**
  - new `Playback/LoopPoints`, `Playback/SoundOptions` and `Playback/SeekPreview`;
  - new `Media/SubtitleFinder`, `Bookmarks/Bookmark` (with `IBookmarkStore`), `Series/ShowChoices` and `Diagnostics/DiagnosticReport`;
  - `PlaybackPreferences` and `AppSettings` gained sound options and the previews switch;
  - `Input/PlayerCommand` and `ShortcutMap` gained five commands with default keys B, Shift+B, Shift+L, D and E;
  - `ISeriesStore` gained show choices.
- **Data:**
  - new migration `0006_advanced.sql` (bookmarks, show choices, two `item_state` columns);
  - new `SqliteBookmarkStore`;
  - the settings, item-state and series stores read and write the new values.
- **Engine:**
  - new `MpvSeekPreviews`;
  - `MpvRenderContext` gained a software render context;
  - `MpvPlaybackEngine` gained the loop, the sound filters and subtitle matching (mpv's `sub-auto` is replaced by `SubtitleFinder`).
- **App:**
  - new `Views/PlayerView.Advanced.cs` and `Services/DiagnosticClipboard`;
  - `PlayerView.xaml` gained the timeline marks, the preview card, the loop chip, the Sound, Bookmarks and Loop menu items, the bookmarks panel, and Try again and Copy details on the error panel;
  - `PlayerView.xaml.cs` was wired for the new commands, choices and recovery;
  - `SettingsView` gained the previews switch, the Sound group and Copy report;
  - `AppLog` rolls over and reads its tail; `AppServices` has the bookmark store.
- **Tests:**
  - Core: `AdvancedPlaybackTests`, `ShowChoicesTests`, `SubtitleFinderTests` and `DiagnosticReportTests`;
  - Data: `AdvancedStoreTests`, plus updates to the schema and settings tests;
  - Engine: `AdvancedFeatureTests`;
  - `make-engine-test-media.sh` makes a red-then-blue clip and a release folder with `Subs/`.
- **Tools:** `Capture-States.ps1` has two new steps (below).
- **Docs:** architecture §14, design review rows, components and keys, dependencies, build and test, README, and this report.

## 3. Verification

These were run, on Linux (Ubuntu 24.04, .NET 10, distribution libmpv2 and FFmpeg 6.1):

| Suite | Result |
|---|---|
| Core | 422 passed, 0 failed |
| Data | 48 passed, 0 failed |
| Media (library) | 56 passed, 0 failed |
| Media server | 60 passed, 0 failed |
| YouTube | 35 passed, 0 failed |
| libmpv engine, against generated files | 75 passed, 0 failed, 0 skipped |

What the new engine tests prove, with the real libmpv:

- A preview at 4 s of the red-then-blue clip is red (average R 200, B 30) and at 15 s is blue (R 31, B 201). The same request a second time comes from memory.
- Anamorphic video gives a picture of the displayed shape, not the stored one. Sound-only and damaged files give no previews and no exception.
- A newer request replaces a waiting one.
- The A–B loop jumps back to the start once playback passes the end.
- Both sound filters apply to a 5.1 file in the distribution libmpv.
- A release's `Subs/<video name>/` files are found and labelled English, and French forced.

The app's C# was compile-checked on Linux against the WinUI reference assemblies (no errors, no warnings). The XAML, the real window and every new state are checked by CI's Windows jobs: the build for x64 and ARM64, the launch smoke test, and the visual review.

**Windows results (added in Milestone 10).** The Windows build for x64 and ARM64, the launch smoke test and the visual review passed in CI, last on run [38823513918](https://github.com/KhanAdeeb1/Aperture-Video-Player/actions/runs/38823513918) of the release branch. Two things were found and fixed in Milestone 10 on the way:

- The visual review moved the pointer by setting the cursor position, which sends WinUI no pointer events, so the first seek-preview screenshots showed the controls hidden and no preview. It now moves the pointer with real mouse input.
- With real pointer input, two genuine bugs showed up and were fixed: leaving a part of the timeline counted as leaving the window (the controls hid while the pointer rested on the timeline), and focus restored after closing a menu kept the controls up for good. See the [Milestone 10 report](milestone-10.md#1-implemented).

## 4. Visual review

The design review answers for the new states are in [design-system.md](../design-system.md#2-design-review), written before the screens were built. In short:

- **Nothing new is on screen during normal playback.** The preview appears only while pointing at the timeline, the loop chip only while a loop is set, and the bookmark ticks are 2 px.
- **Everything else lives in the More menu,** in panels shaped like the existing ones, or in Settings. Lamp is the only accent: the loop chip, the bookmark times and the loop band. There are no gradients, glass or emoji.
- **The new keys are in the shortcuts panel** and can be changed in Settings like every other key.

The visual review adds two steps:

- **Seek preview, bookmarks, loop, sound.** It points at 30 % of the timeline, then presses B, Shift+B, Shift+L twice and D, checking each notice. It also opens More and Settings › Sound. Screenshots:
  - `100-seek-preview`
  - `101-bookmark-toast`
  - `102-bookmarks-panel`
  - `103-loop-chip`
  - `104-clear-dialogue-toast`
  - `105-more-menu-advanced`
  - `106-settings-sound`
- **Error panel.** On a damaged file it checks that Try again is offered, then presses Copy details. It checks that the clipboard holds a report with neither the media folder nor the file name in it. Screenshot: `107-error-retry-copy`.

Screenshots, from the Windows CI run after the Milestone 10 fixes (`docs/reports/milestone-9/`):

- [100](milestone-9/100-seek-preview.jpg): pointing at the timeline shows a picture of 5:20 with the time, and the controls stay up while the pointer rests there.
- [101](milestone-9/101-bookmark-toast.jpg): "Bookmarked 12:26 · Shift+B shows bookmarks".
- [102](milestone-9/102-bookmarks-panel.jpg): the bookmarks panel with one bookmark and its note field.
- [103](milestone-9/103-loop-chip.jpg): the Lamp loop chip "Loop 12:29 – 12:32" and the band on the timeline.
- [104](milestone-9/104-clear-dialogue-toast.jpg): "Clearer dialogue on".
- [105](milestone-9/105-more-menu-advanced.jpg): the More menu with Bookmarks, Loop and Sound.
- [106](milestone-9/106-settings-sound.jpg): Settings › Sound.
- [107](milestone-9/107-error-retry-copy.jpg): a damaged file with Try again and Copy details for a bug report.

## 5. Known limitations

- **Transcripts and online subtitle search are not built** and are waiting for your decisions (top of this report).
- **Previews show the nearest keyframe** before the point, not the exact frame. Previews use keyframe seeks so that each one stays cheap. On files with keyframes far apart (some screen recordings), the picture can be from several seconds earlier.
- **Previews are not offered for streams.** They work for local and network-drive files only; YouTube has its own player.
- **Bookmarks follow the path.** A file that is moved or renamed loses its bookmarks, as it loses its history.
- **Even volume reacts to the next few seconds,** so the very start of a loud scene can still be loud for a moment.
- **Clearer dialogue mixes surround to stereo.** On a real 5.1 speaker set-up, turning it on gives stereo.
- **Recovery reopens a file once per opening.** A second failure shows the error. This is deliberate, so a broken drive can't loop forever.
- **Not yet watched by a person** on Windows; CI's screenshots are the only check so far.

## 6. Regression check

- All earlier suites pass unchanged, apart from two tests that were updated on purpose:
  - the schema test lists the two new tables;
  - the settings round-trip includes the new values.
- mpv's own subtitle auto-loading was replaced. The existing engine tests for exact-name and auto-loaded subtitles still pass against the new finder.
- The slider's own time tooltip was replaced by the preview card, which shows the time even when no picture is available.
- The new right-side panel closes the others, as they close each other, and Esc closes it first.
- Keys inside the bookmark note field go to the field, as in other text boxes.
- The timeline marks don't take pointer input, so seeking works as before.

## 7. Next step

Milestone 10, final polish and release, after this pull request is green, and transcripts or online subtitles if you approve them.
