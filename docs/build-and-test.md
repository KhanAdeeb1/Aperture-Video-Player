# Build and test

## Requirements

| What | Needed for |
|---|---|
| Windows 11 (x64 or ARM64) | Building and running the app |
| .NET SDK 10.0.100 or a later 10.0.1xx band (pinned by `global.json`) | Everything |
| Visual Studio 2026, or VS 2022 17.14+ with the ".NET desktop development" and "WinUI application development" workloads | Optional; for XAML designer and debugging. The command line works without it. Unverified: the exact minimum VS version that understands .NET 10 + Windows App SDK 2.x |
| Any OS with the .NET 10 SDK | Building and testing the platform-neutral projects only |
| Internet access on the first Windows build | Downloading the pinned libmpv build, see `third_party/README.md` |
| libmpv 0.35 or later (`libmpv2` on Ubuntu/Debian) | Running the engine tests on Linux |
| FFmpeg and bash | Generating the test files |

## On Windows

```powershell
# Platform-neutral tests
dotnet test tests/Aperture.Core.Tests/Aperture.Core.Tests.csproj
dotnet test tests/Aperture.Data.Tests/Aperture.Data.Tests.csproj
dotnet test tests/Aperture.Media.Tests/Aperture.Media.Tests.csproj
dotnet test tests/Aperture.Server.Tests/Aperture.Server.Tests.csproj   # conversion tests need FFmpeg on PATH

# Engine tests against real files (see "Engine tests" below)
powershell -File tools/Get-Libmpv.ps1 -RuntimeIdentifier win-x64
dotnet test tests/Aperture.Playback.Mpv.Tests/Aperture.Playback.Mpv.Tests.csproj

# Build the app (pick x64 or ARM64)
dotnet build src/Aperture.App/Aperture.App.csproj -c Debug -p:Platform=x64

# Run it
dotnet run --project src/Aperture.App/Aperture.App.csproj -p:Platform=x64
```

In Visual Studio, open `Aperture.sln`, set `Aperture.App` as the startup project, choose `x64`, and press F5.

The app runs unpackaged and self-contained, so no separate Windows App SDK runtime install is required.

Building the app also publishes the media server (`src/Aperture.Server`) self-contained into `Server\` next to `Aperture.exe`. Building the app runs `tools/Get-Libmpv.ps1` for the target platform, which downloads the pinned libmpv once, checks its SHA-256 and places `libmpv-2.dll` next to `Aperture.exe`. ANGLE (`av_libglesv2.dll`) comes from NuGet.

Expected: the first-launch screen ("Open a video to begin"). Open a video with Ctrl+O, by dropping it on the window, or by passing its path: `Aperture.exe "D:\Videos\clip.mkv"`. Drop a subtitle file on a playing video to add it. If libmpv is missing, the player shows "Aperture's playback engine couldn't start" instead of crashing.

Set `APERTURE_VIDEO_RENDERER=software` to force the software renderer (useful to tell a GPU driver problem from an engine problem). Press I during playback for the active renderer, decoder and stream details.

## On Linux or macOS

Only the platform-neutral projects build there:

```bash
dotnet test tests/Aperture.Core.Tests/Aperture.Core.Tests.csproj
dotnet test tests/Aperture.Data.Tests/Aperture.Data.Tests.csproj
dotnet test tests/Aperture.Media.Tests/Aperture.Media.Tests.csproj
dotnet test tests/Aperture.Playback.Mpv.Tests/Aperture.Playback.Mpv.Tests.csproj   # needs libmpv2
dotnet test tests/Aperture.Server.Tests/Aperture.Server.Tests.csproj                 # conversion tests need ffmpeg
```

The media server itself runs on Linux too, which is how it is developed and tested from this repository's Linux environment (Windows is the supported platform):

```bash
APERTURE_DATA_DIR=/tmp/aperture-dev dotnet run --project src/Aperture.Server
```

`Aperture.Video` also builds there (`EnableWindowsTargeting`), but cannot run. The app's C# can still be compile-checked on Linux by a scratch project that targets `net10.0-windows10.0.22621.0` with `EnableWindowsTargeting`, includes the app's `.cs` files and stands in for the XAML compiler's output with generated partial classes (one field per `x:Name` and an `InitializeComponent`). XAML markup itself is only checked by the Windows CI build. `dotnet build Aperture.sln` fails on non-Windows hosts because the WinUI XAML compiler (`XamlCompiler.exe`) is a Windows executable. This was confirmed in the Milestone 0 Linux environment.

## Engine tests

`tests/Aperture.Playback.Mpv.Tests` runs the real libmpv engine headless (`vo=null`, `ao=null`) against generated files: twelve container/codec combinations, an anamorphic file, audio-only, a file cut in half, damaged, empty and truncated files, a subtitles-only file, a text file renamed `.mp4`, and a missing path. It also checks resume position, volume, mute, speed, picture shape, audio and subtitle switching, external and auto-loaded subtitles, and diagnostics. From Milestone 9 it also checks seek previews (the right colour at the right time on a red-then-blue clip, the picture's shape for anamorphic video, none for sound-only or damaged files, a newer request replacing a waiting one), the A–B loop, clearer dialogue and even volume on a 5.1 file, and subtitles found in a release's `Subs/<video name>/` folder.

```bash
bash tools/test-media/make-engine-test-media.sh /tmp/engine-media
APERTURE_TEST_MEDIA=/tmp/engine-media APERTURE_TEST_RESULTS=/tmp/results.md \
  dotnet test tests/Aperture.Playback.Mpv.Tests/Aperture.Playback.Mpv.Tests.csproj
```

The generator also makes three 90 s episodes of a show, *Harbour* (`Harbour.S01E01-03.mkv`), with cold opens of different lengths (10, 3 and 15 s), the same 20 s opening and the same 20 s closing credits on a black picture; E02 has chapters. `SignalReaderTests` decodes them through libmpv and checks that the intro and credits are found where each episode actually has them.

Without `APERTURE_TEST_MEDIA`, or without libmpv, the file tests are reported as **skipped**, never passed. `APERTURE_TEST_RESULTS` collects one row per file (container, codecs, size, what happened) into a markdown table.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request:

- **Core, data, library, YouTube and media server tests** on `ubuntu-latest` and `windows-latest`, with FFmpeg installed so the server's remux and conversion tests run (they are reported as skipped, never passed, where FFmpeg is missing). The server tests run the real server on loopback over HTTPS.
- **Test media**: an Ubuntu job generates the visual-review clips and the engine test files with FFmpeg.
- **libmpv engine tests** on Ubuntu (distribution `libmpv2`) and Windows (the pinned build). The measured results table is written to the job summary.
- **Windows app build** for `x64` and `ARM64`, checking that `libmpv-2.dll` and `av_libglesv2.dll` are next to the app, with a launch smoke test on x64, and a check that the bundled media server starts in the background, answers `status` and stops.
- **Visual review**: a Windows job sets the display to 1920×1080, builds the app and runs `tools/visual-review/Capture-States.ps1`, which drives the real app through every state with keyboard, mouse and UI Automation and saves a screenshot of each. It checks rendered pixels on an orientation clip (amber top, blue bottom) with the GPU (ANGLE on WARP) and software renderers, windowed and full screen. Screenshots, notes and the app log are uploaded as the `visual-review` artifact; on pushes, downscaled copies are also committed to the `ci/visual-review` branch so they can be viewed without downloading the artifact.

To run the visual review locally on Windows (it uses a throwaway data folder, never your own):

```powershell
# make the clips on any machine with FFmpeg and bash, then:
powershell -File tools/visual-review/Capture-States.ps1 -Exe <path-to-Aperture.exe> -MediaDir <clips> -OutDir <output>
```

The visual review's YouTube step needs the network (GitHub's runners have it). It plays a public, embeddable video from a pasted link without an API key, saves it, checks the unavailable-video explanation, and screenshots the consent screen and Settings › YouTube. Search, liked videos and playlists need a key or a sign-in and are covered by unit tests against recorded API responses, not by CI.

The visual review's Milestone 9 steps point at the timeline and screenshot the seek preview, press B, Shift+B, Shift+L twice and D and check each notice, open More and Settings › Sound, and on a damaged file press "Copy details for a bug report" and check the clipboard holds a report with the media folder and file name removed.

The visual review's casting step starts `tools/visual-review/fake-tv.py` (a stand-in DLNA TV, Python standard library only), opens a video, chooses More › Play on a TV, starts the media server from the panel, picks the TV, checks the remote reads "Playing on …" and "Paused on …", stops casting, and checks the stand-in TV really fetched the file over HTTP. It needs `python` on the path, as GitHub's Windows runners have.

The visual review's media server step shares the test library, sets a passphrase and presses Start in Settings, signs in over HTTPS with `curl` the way a phone would and lists the library, checks the device appears in Settings, closes Aperture and checks the server still answers, then stops and restarts it from Settings.

## Data location

Aperture stores its own data (database, logs) in `%LOCALAPPDATA%\Aperture`. Set `APERTURE_DATA_DIR` to an absolute path to use a different folder, for example for a portable copy or a clean test profile.

## Native dependencies

libmpv is required at runtime and is never committed. `third_party/README.md` records the exact build, its hashes and license, and how to update the pin.

## Casting to a real DLNA renderer (by hand)

The automated tests use a fake renderer. To try a real one on Linux, install gmrender-resurrect and run it on a network interface with a private address (the media server only accepts TVs on home-network addresses, and some containers' interfaces aren't private):

```bash
sudo apt-get install gmediarender gstreamer1.0-plugins-good gstreamer1.0-plugins-bad gstreamer1.0-libav
# if no interface has a 192.168/10/172.16 address, make one (here an ifb device):
sudo ip link add ifb0 type ifb; sudo ip link set ifb0 up multicast on
sudo ip addr add 192.168.78.1/24 dev ifb0; sudo ip route add 239.0.0.0/8 dev ifb0
gmediarender -I ifb0 -f "Test TV" -u 0a1b2c3d-1111-2222-3333-444455556666 --port 49494 \
  --gstout-videosink fakevideosink --gstout-audiosink fakeaudiosink
# a TV that accepts fewer formats, to see conversion: add --mime-filter "video/mpeg,video/mp4"
```

`fakevideosink`/`fakeaudiosink` play in real time without a screen (plain `fakesink` runs faster than real time). Then run the server as above and call its control address on 127.0.0.1:8080 with the `X-Aperture-Control` header set to the `controlToken` from `server.json`: `GET /control/cast/renderers?search=1`, `POST /control/cast/start` with `{"rendererId","path","title","startSeconds","durationSeconds"}`, `GET /control/cast/status`, `POST /control/cast/pause|play|stop`, `POST /control/cast/seek` with `{"seconds"}`. `tools/visual-review/fake-tv.py` can stand in for a TV the same way.
