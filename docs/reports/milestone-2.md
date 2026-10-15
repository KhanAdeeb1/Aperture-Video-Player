# Milestone 2 report: core playback engine

Date: 2026-10-15

**Needs your approval:** the render path (section 1, "Render path"). It is the largest architectural decision in the project, it is implemented and working in CI, and it costs HDR. Details are in [architecture.md §4.2](../architecture.md).

## 1. Implemented

- **libmpv is the playback engine.** `Aperture.Playback.Mpv` binds the libmpv client and render APIs by hand (no wrapper package), runs mpv's event loop on its own thread, observes properties instead of polling, and sends commands asynchronously so the interface never waits on mpv. The Milestone 1 Media Foundation engine is gone. mpv runs as an embedded engine: no user config, scripts, on-screen controller, key bindings or network fetching (`ytdl=no`).
- **Pinned Windows build.** `tools/Get-Libmpv.ps1` downloads build `20261015` of shinchiro/mpv-winbuild-cmake (mpv `492bae0768`, reports itself as mpv 0.41.0-1175), checks its SHA-256, and places `libmpv-2.dll` next to the app at build time for x64 and ARM64. Nothing native is committed. See [third_party/README.md](../../third_party/README.md).
- **Render path.** libmpv's OpenGL renderer runs on ANGLE (OpenGL ES on Direct3D 11) using Aperture's own D3D11 device. Each frame is drawn into a D3D11 texture, copied to a composition swap chain, and shown in a XAML `SwapChainPanel`. The controls are ordinary XAML over the video. If ANGLE cannot start, a software path uses libmpv's CPU renderer. Why this one, and what it costs: architecture.md §4.2.
- **Everything requested:**
  - Open with the file picker, drag and drop, or the command line (unchanged from M1).
  - Play, pause, seek, stop. Stop (S or the media Stop key) saves the position, closes the file and returns Home.
  - Volume and mute; full screen and windowed (unchanged from M1, now over libmpv).
  - Picture shape: Original (honours non-square pixels), Fill window, Stretch, 16:9, 4:3 and 2.39:1, from More › Picture shape or R.
  - Audio and subtitle tracks: an "Audio and subtitles" menu listing each track by language, title, codec and channels (for example "English · Original", "Dolby Digital · 5.1"), plus C and A to cycle.
  - External subtitles: "Add subtitle file…" in the same menu, or drop a subtitle file on a playing video. Files named like the video (`Film.en.srt`) load automatically. A file that cannot be read gets a message and changes nothing.
  - Speed from 0.25× to 2× in the More menu, or [ and ], with \ for normal.
  - Resume from the last position (M1's history, now passed to mpv as the start position so the first frame shown is the right one).
  - Missing, damaged, unsupported and non-video files each get a specific screen. The checks for existence, extension, readability and empty files run before mpv is involved; mpv's own errors are mapped to Damaged, Unsupported or Failed.
- **Stream metadata and diagnostics.** A "Playback info" panel (I, or More › Playback info) shows the file (name, container, size, length), video (codec, resolution and the shape it is shown at, frame rate with the measured rate, pixel format, colour, software or hardware decoding, bitrate, dropped frames), audio (codec, channels, sample rate, bitrate, output), subtitles (format, embedded or external), and playback (speed, A/V sync, how much is buffered ahead, renderer, engine version). It refreshes every second.
- **Notices.** A short confirmation appears for keyboard changes (track, speed, picture shape). If one stream cannot be decoded but the other can, playback continues with a notice instead of failing.
- **Behaviour without a GPU.** On a machine with no GPU driver (the CI runners, many VMs), the video thread runs below normal priority and libmpv uses its cheapest scalers, so the interface stays responsive. The log records any time the interface thread falls more than half a second behind.

## 2. Files changed

57 files changed relative to `claude/milestone-1-design-system-77j8nm`.

| Area | Files | Responsibility |
|---|---|---|
| Engine (new project) | `src/Aperture.Playback.Mpv/*` | P/Invoke, library loading, event loop, track list, failure mapping, diagnostics, `MpvPlaybackEngine`, render context |
| Video (new project) | `src/Aperture.Video/*` | D3D11 device and swap chain, ANGLE frame source, software frame source, render thread, device-loss recovery |
| Core | `Playback/IPlaybackEngine.cs`, `PlaybackFailure.cs`, new `MediaTrack`, `AspectMode`, `PlaybackSpeed`, `TrackLabels`, `PlaybackDiagnostics`; `Media/MediaFileTypes.cs` | Engine contract extended for tracks, picture shape, subtitles and diagnostics; readable labels |
| App | `MainWindow.xaml.cs`, `Views/PlayerView.xaml(.cs)`, `Video/SwapChainPanelBridge.cs`, `Services/AppLog.cs`, `Aperture.App.csproj`; `Playback/MediaFoundationEngine.cs` deleted | Engine and surface startup, menus, keys, info panel, toasts, subtitle drop, libmpv fetch at build |
| Tests | `tests/Aperture.Playback.Mpv.Tests/*` (new), four new files in `tests/Aperture.Core.Tests` | Engine tests over real files; labels, speeds, picture shapes, subtitle file types |
| Tools and CI | `tools/Get-Libmpv.ps1`, `third_party/libmpv.json`, `tools/test-media/make-engine-test-media.sh`, `tools/visual-review/*`, `.github/workflows/ci.yml` | Pinned fetch, test files, engine test jobs, on-screen pixel checks, screenshot publishing |
| Docs | `architecture.md`, `dependencies.md`, `build-and-test.md`, `design-system.md`, `README.md`, `third_party/README.md`, this report | Render path decision, dependency register, keys, build steps |

New dependencies, each with its license and reason in [dependencies.md](../dependencies.md):
- **libmpv** (shinchiro build; treated as GPLv2+), the engine adi chose in M0.
- **Vortice.Direct3D11 / Vortice.DXGI 3.8.3** (MIT), typed D3D11 and DXGI bindings for the presenter. They bring SharpGen.Runtime 2.4.2-beta (MIT) transitively.
- **Avalonia.Angle.Windows.Natives** (BSD-3-Clause), used only for its ANGLE DLL.

## 3. Verification

### Representative files, played through libmpv

These are the engine tests in `tests/Aperture.Playback.Mpv.Tests`: each file is opened in a real libmpv instance (no video or audio output), and for the playable ones the test seeks to 5 s, pauses and checks the position holds, then plays to the end. Files are generated by `tools/test-media/make-engine-test-media.sh` (8 s each unless noted). Results below are from the Windows job with the pinned build (mpv 0.41.0-1175); the Ubuntu job with Ubuntu's libmpv gave the same outcome for every file.

| File | Container and streams | Result |
|---|---|---|
| h264-aac-timedtext.mp4 | MP4: H.264 1280×720, AAC, 2 timed-text subtitles | Plays; seek, pause, end OK |
| hevc10-ac3-51.mkv | MKV: HEVC 10-bit 1920×1080, AC-3 5.1 | Plays; seek, pause, end OK |
| vp9-opus.webm | WebM: VP9 1280×720, Opus | Plays; seek, pause, end OK |
| av1-opus.mkv | MKV: AV1 1280×720, Opus | Plays; seek, pause, end OK |
| mpeg2-anamorphic.ts | MPEG-TS: MPEG-2 720×576 stored, MP2 | Plays, shown at 1024×576 (non-square pixels honoured); seek, pause, end OK |
| mpeg4-mp3.avi | AVI: MPEG-4 Part 2 640×480, MP3 | Plays; seek, pause, end OK |
| wmv-wma.wmv | ASF: WMV2 640×360, WMA2 | Plays; seek, pause, end OK |
| prores-pcm.mov | MOV: ProRes 1280×720, PCM | Plays; seek, pause, end OK |
| ffv1-flac.mkv | MKV: FFV1 640×360, FLAC | Plays; seek, pause, end OK |
| theora-vorbis.ogv | Ogg: Theora 640×360, Vorbis | Plays; seek, pause, end OK |
| portrait.mp4 | MP4: H.264 540×960 | Plays; seek, pause, end OK |
| multi-track.mkv | MKV: H.264, 2 audio (eng AAC, jpn AC-3 5.1), 2 SubRip (eng, fre) | Plays; tracks listed with languages; audio switch, subtitle switch and subtitles off verified |
| audio-only.mp4 | MP4: AAC only | Plays |
| cut-in-half.mkv | First half of a 16 s MKV | Plays and ends cleanly at 7.9 s |
| damaged.mp4 | Random bytes | Damaged: "The file's contents aren't a format Aperture recognises." |
| truncated-no-index.mp4 | MP4 cut before its index | Damaged |
| empty.mp4 | 0 bytes | Damaged: "The file is empty." |
| subtitles-only.mkv | MKV with only a subtitle stream | Unsupported (nothing to play) |
| not-a-video.txt | Text file | Unsupported (not a video file) |
| missing.mp4 | Does not exist | Not found |
| extra-subtitles.ass | External ASS file | Loads as a third subtitle track and is selected |
| broken-subtitles.srt | Not a subtitle file | Rejected with a message; playback unaffected |
| multi-track.en.srt | Named like the video | Loaded automatically |

The engine tests also check: start at a saved position, volume, mute and speed reaching mpv, each picture shape setting the matching mpv options, diagnostics content, a failed file not affecting the next one, and Close releasing the file.

These are synthetic files. They cover the common containers and codecs, but they are not real-world encodes, and nothing here supports a claim of universal compatibility. Not tested: DRM-protected files, Blu-ray or DVD structures, image-based subtitles (PGS, VobSub), HDR10 or Dolby Vision, 4K60 and higher-bitrate files, network paths, and very long files.

### Builds, tests and on-screen checks

| What | Where | Result |
|---|---|---|
| Core and data tests (90 and 9) | This Linux environment, and CI on Ubuntu and Windows | Passed |
| Engine tests (42) | CI: Ubuntu with libmpv 0.37 from apt; Windows with the pinned build | Passed on both (see the table above) |
| App build, x64 and ARM64, warnings as errors | CI Windows | Success; `libmpv-2.dll` and `av_libglesv2.dll` present next to the app |
| Launch smoke test | CI Windows x64 | Success |
| Picture reaches the screen upright, GPU path | CI visual review, 1920×1080, Basic Render Driver | Pixel check passes windowed, after a resize to 1600×900, and in full screen (amber top, blue bottom as encoded) |
| Same, software path | CI visual review | Pixel check passes |
| Every Milestone 1 state, now over libmpv | CI visual review | All steps completed; 7 clips confirmed playing (position advancing) |
| FFV1 file (unplayable in M1) | CI visual review | Plays in the app |

What the CI runs found and I fixed:
- The GPU path drew the picture upside down. The on-screen pixel check caught it.
- On the GPU-less CI machine the interface fell 1 to 8 seconds behind while video played, so menus, toasts and the info panel appeared late.
- The libmpv fetch failed when MSBuild started Windows PowerShell from PowerShell 7.
- A test raced mpv's default track selection.
- A device-loss rebuild would have hit a null reference.

Not executed: the app on a real Windows 11 PC with a GPU, hardware decoding (CI has no GPU, so every file decoded in software), audio output (CI has no sound device), display scaling above 100%, multiple monitors, ARM64 at runtime, and screen readers.

## 4. Visual review

I reviewed the CI screenshots myself this time (CI now also commits downscaled copies to the `ci/visual-review` branch, since this environment cannot download artifacts).

- Video fills the player with correct letterboxing, and the Milestone 1 controls, title bar, mini-player and full-screen layouts sit over it as designed.
- Subtitles render with the intended size, outline and bottom margin, and move up while the controls are shown so they are never covered.
- Error screens are consistent with M1. The "nothing here to play" screen repeated its message in the detail line; it now gives the cause instead.

VISUAL_REVIEW_PLACEHOLDER

## 5. Known limitations

- **HDR is not supported** on this render path; HDR files are tone-mapped to SDR.
- **Not measured on real hardware.** Frame pacing, dropped frames at 4K60, GPU load and hardware decoding have only run on CI's software renderer.
- **License decision still open.** The pinned libmpv build is treated as GPL. Release needs the GPL or LGPL route decided (dependencies.md).
- **The pinned build can disappear.** shinchiro prunes old releases. If it does, the fetch fails loudly and a new pin is needed (third_party/README.md).
- **Device loss.** Recovery after a GPU reset or driver update rebuilds the surface, but it has not been exercised. The current file may need to be reopened.
- **Stop has no separate "stopped" screen.** Stop saves the position and returns Home, the same as Back. I chose this because a stopped player with nothing on screen looked like an error.
- **Picture shape and speed are not remembered** between files. Each file starts at Original and Normal, by design for now; remembering them per file is a small follow-up if you want it.
- **Ubuntu tests use libmpv 0.37** (the distribution package) and Windows uses 0.41. A deprecation warning about resetting the aspect override appears in the 0.41 log; behaviour is correct on both.
- Light theme, library folders and the deferred items from M1 are unchanged.

## 6. Regression check

All Milestone 1 behaviour was re-run in the visual review against the new engine: first launch at two sizes, open dialog, playback with controls visible and hidden, opening, More menu, paused, mini-player, full screen, Home at three widths, hover, keyboard focus, search, settings, and the error states. Resume, progress saving and Continue Watching use the same history code and tests as M1 (9 data tests pass). The M1 keyboard map is unchanged; the new keys are additions. One deliberate change: FFV1 and other formats Media Foundation could not decode now play instead of showing the unsupported screen.

## 7. Next step

1. Approve the render path, or tell me to prototype mpv's own window (Path B) for comparison, which would bring HDR back at the cost of a second overlay window for the controls.
2. Run it on your own PC with a GPU and a few of your real files, and tell me what you see, especially smoothness and anything that fails to open.
3. Decide the libmpv and FFmpeg license route before any public release.

Then the next milestone you give me.
