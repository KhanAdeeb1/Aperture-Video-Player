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

Building the app runs `tools/Get-Libmpv.ps1` for the target platform, which downloads the pinned libmpv once, checks its SHA-256 and places `libmpv-2.dll` next to `Aperture.exe`. ANGLE (`av_libglesv2.dll`) comes from NuGet.

Expected: the first-launch screen ("Open a video to begin"). Open a video with Ctrl+O, by dropping it on the window, or by passing its path: `Aperture.exe "D:\Videos\clip.mkv"`. Drop a subtitle file on a playing video to add it. If libmpv is missing, the player shows "Aperture's playback engine couldn't start" instead of crashing.

Set `APERTURE_VIDEO_RENDERER=software` to force the software renderer (useful to tell a GPU driver problem from an engine problem). Press I during playback for the active renderer, decoder and stream details.

## On Linux or macOS

Only the platform-neutral projects build there:

```bash
dotnet test tests/Aperture.Core.Tests/Aperture.Core.Tests.csproj
dotnet test tests/Aperture.Data.Tests/Aperture.Data.Tests.csproj
dotnet test tests/Aperture.Playback.Mpv.Tests/Aperture.Playback.Mpv.Tests.csproj   # needs libmpv2
```

`Aperture.Video` also builds there (`EnableWindowsTargeting`), but cannot run. `dotnet build Aperture.sln` fails on non-Windows hosts because the WinUI XAML compiler (`XamlCompiler.exe`) is a Windows executable. This was confirmed in the Milestone 0 Linux environment.

## Engine tests

`tests/Aperture.Playback.Mpv.Tests` runs the real libmpv engine headless (`vo=null`, `ao=null`) against generated files: twelve container/codec combinations, an anamorphic file, audio-only, a file cut in half, damaged, empty and truncated files, a subtitles-only file, a text file renamed `.mp4`, and a missing path. It also checks resume position, volume, mute, speed, picture shape, audio and subtitle switching, external and auto-loaded subtitles, and diagnostics.

```bash
bash tools/test-media/make-engine-test-media.sh /tmp/engine-media
APERTURE_TEST_MEDIA=/tmp/engine-media APERTURE_TEST_RESULTS=/tmp/results.md \
  dotnet test tests/Aperture.Playback.Mpv.Tests/Aperture.Playback.Mpv.Tests.csproj
```

Without `APERTURE_TEST_MEDIA`, or without libmpv, the file tests are reported as **skipped**, never passed. `APERTURE_TEST_RESULTS` collects one row per file (container, codecs, size, what happened) into a markdown table.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request:

- **Core and data tests** on `ubuntu-latest` and `windows-latest`.
- **Test media**: an Ubuntu job generates the visual-review clips and the engine test files with FFmpeg.
- **libmpv engine tests** on Ubuntu (distribution `libmpv2`) and Windows (the pinned build). The measured results table is written to the job summary.
- **Windows app build** for `x64` and `ARM64`, checking that `libmpv-2.dll` and `av_libglesv2.dll` are next to the app, with a launch smoke test on x64.
- **Visual review**: a Windows job sets the display to 1920×1080, builds the app and runs `tools/visual-review/Capture-States.ps1`, which drives the real app through every state with keyboard, mouse and UI Automation and saves a screenshot of each. It checks rendered pixels on an orientation clip (amber top, blue bottom) with the GPU (ANGLE on WARP) and software renderers, windowed and full screen. Screenshots, notes and the app log are uploaded as the `visual-review` artifact; on pushes, downscaled copies are also committed to the `ci/visual-review` branch so they can be viewed without downloading the artifact.

To run the visual review locally on Windows (it uses a throwaway data folder, never your own):

```powershell
# make the clips on any machine with FFmpeg and bash, then:
powershell -File tools/visual-review/Capture-States.ps1 -Exe <path-to-Aperture.exe> -MediaDir <clips> -OutDir <output>
```

## Data location

Aperture stores its own data (database, logs) in `%LOCALAPPDATA%\Aperture`. Set `APERTURE_DATA_DIR` to an absolute path to use a different folder, for example for a portable copy or a clean test profile.

## Native dependencies

libmpv is required at runtime and is never committed. `third_party/README.md` records the exact build, its hashes and license, and how to update the pin.
