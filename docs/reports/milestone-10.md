# Milestone 10: final polish and release

**Aperture 0.1.0 is a release candidate, not a production-ready product.** It builds, installs, upgrades, uninstalls and plays for two hours with flat memory and handles on GitHub's virtual Windows machines, and every screen was reviewed. But several of the tests this milestone asked for need hardware CI doesn't have and have **not been executed**: hardware decoding, HDR, several monitors, display scaling above 100 %, Narrator, an ARM64 PC, a real phone, and YouTube playback on a real PC. They are listed in [section 5](#5-known-limitations) with what each needs. Until adi (or someone) runs them, 0.1.0 should be shared as a preview.

Decision taken this milestone: **Aperture is licensed GPL-3.0-or-later** (adi, 2026-10-15), so the release ships the pinned GPL libmpv as it is.

## 1. Implemented

- **One branch with everything.** Milestones 6 to 9 had been merged into each other's branches but not into the default branch. This branch starts from the Milestone 9 code and merges the default branch in, so its pull request brings Milestones 6 to 10 into `claude/milestone-0-audit-84kzf1` together.
- **Release build.** `tools/Build-Release.ps1` publishes the app self-contained for x64 or ARM64 (with the media server, libmpv, ANGLE, the licence, the notices and the licence texts) and makes:
  - `Aperture-0.1.0-<platform>-setup.exe`, a per-user Inno Setup installer;
  - `Aperture-0.1.0-<platform>-portable.zip`;
  - `SHA256SUMS-<platform>.txt`.
  It refuses a build that lacks the compiled XAML resources, which is how the first installed build failed (below).
- **Installer** (`installer/Aperture.iss`):
  - Installs for the current user into `%LOCALAPPDATA%\Programs\Aperture` with no administrator prompt. Windows 11 only; the ARM64 installer only on ARM64.
  - Start menu entry, optional desktop icon, an entry in Installed apps, and **Open with** for the 14 video types Aperture opens (a test keeps this list equal to the app's own). It never makes itself the default player.
  - **Upgrade** closes Aperture, stops the media server, removes the old program files (so nothing stale is left) and keeps the user's data.
  - **Uninstall** stops the server, removes the program and its registrations, then asks (default **No**) whether to also remove the library and settings. Yes removes the data folder, the keys in Credential Manager and the media server's certificate authority. A silent uninstall never removes data. Media files are never touched.
- **App icon and version.** The Aperture mark (a Lamp ring on charcoal) as the window, taskbar and installer icon, drawn by `tools/brand/make-icon.py`. The version (0.1.0) is set once in `Directory.Build.props`; Settings › About, the bug report and the TV casting user agent read it.
- **Licence and notices.** `LICENSE` (GPL-3.0), `THIRD-PARTY-NOTICES.md` listing every bundled component with its licence and source (the libraries inside libmpv were read from the DLL itself), and the other licence texts in `third_party/licenses/`. All are installed next to `Aperture.exe`.
- **Release workflow** (`.github/workflows/release.yml`): for a `v*` tag it checks the tag matches the version, runs the tests, builds both platforms, runs the two-hour soak test and creates a **draft** GitHub release with the files and `docs/release-notes.md`. A person publishes it.
- **New tests:**
  - `tools/packaging/Test-Install.ps1`: on a clean Windows profile, installs a stand-in 0.0.9, launches it, upgrades to 0.1.0 while the app and the media server run, opens a file from the command line, and uninstalls, checking each result (below).
  - `tools/packaging/Test-Soak.ps1`: opens and closes the app repeatedly, then plays one file continuously, sampling memory, CPU, handles, threads and the playback position.
  - `UpgradePathTests`: a database at every earlier schema version, with history in it, upgrades to the same schema as a fresh one, keeps its data and leaves a backup.
  - `InstallerTests`: the installer's Open with list equals the app's video types.
- **Fixes from the screen review** (all 77 screens from the CI visual review were looked at, at full size):
  - **The visual review had never sent the app a pointer event.** It moved the pointer by setting the cursor position, which moves the arrow but gives WinUI nothing, so every hover, idle and seek-preview screenshot was testing less than it claimed. It now moves the pointer with real mouse input, and two genuine bugs showed up:
    - Leaving one part of the timeline for another counted as leaving the window (the event bubbles), so the controls hid while the pointer rested on the timeline. Only a real exit from the window counts now.
    - Focus that Windows puts back on a button when a menu closes counted as keyboard navigation, so after using the More menu the controls never hid again (seen in the idle mini player and in full screen). Only Tab or arrow navigation into the controls keeps them up now.
  - Whether the pointer is on the controls is worked out from its position on every move, including moves the timeline handles itself.
  - The Skip and next-episode cards dropped onto the timeline while it faded; they now move with the controls.
  - Settings › Media server listed Hyper-V and WSL virtual adapters' addresses, which a phone can't reach; it now lists adapters with a default gateway, and Copy address copies one of those.
  - A missing file and a file that isn't a video offered "Copy details for a bug report", which isn't useful; only real playback failures do.
  - The visual review now steps through subtitle tracks until one is on, so the subtitle screenshot shows a subtitle.
  - The uninstaller removes anything left in the program folder, not only the files it installed.
- **Documents for the release:**
  - [What Aperture does](../features.md), the feature list;
  - [Installing, upgrading and removing](../install.md);
  - [Tested configurations and media samples](../tested-configurations.md);
  - [Security review of the media server and casting](../security-review.md);
  - [Release notes](../release-notes.md);
  - [Dependencies and licences](../dependencies.md), updated with the GPL decision and the build tools;
  - the README and [Build and test](../build-and-test.md), updated.
- **Milestone 9 screenshots** added to [its report](milestone-9.md#4-visual-review), including a fresh seek preview taken after the controls fix.

## 2. Files changed

| Area | Files |
|---|---|
| Packaging | `installer/Aperture.iss`, `tools/Build-Release.ps1`, `tools/packaging/Test-Install.ps1`, `tools/packaging/Test-Soak.ps1`, `.github/workflows/release.yml`, `.github/workflows/ci.yml` (Release package job) |
| Licence | `LICENSE`, `THIRD-PARTY-NOTICES.md`, `third_party/licenses/*` |
| Branding and version | `tools/brand/make-icon.py`, `src/Aperture.App/Assets/Aperture.ico`, `src/Aperture.App/Aperture.App.csproj`, `src/Aperture.App/MainWindow.xaml.cs`, `Directory.Build.props`, `src/Aperture.Server/Casting/CastManager.cs` |
| Fixes | `src/Aperture.App/Views/PlayerView.xaml`, `PlayerView.xaml.cs`, `PlayerView.Episodes.cs`, `src/Aperture.Server/Hosting/SetupEndpoints.cs`, `tools/visual-review/Capture-States.ps1` |
| Tests | `tests/Aperture.Data.Tests/UpgradePathTests.cs`, `tests/Aperture.Core.Tests/Packaging/InstallerTests.cs`, `tests/Aperture.Server.Tests/ControlAndCertificateTests.cs`, `src/Aperture.Data/ApertureDatabase.cs` (migrations visible to tests) |
| Docs | `README.md`, `docs/features.md`, `docs/install.md`, `docs/tested-configurations.md`, `docs/security-review.md`, `docs/release-notes.md`, `docs/dependencies.md`, `docs/build-and-test.md`, `docs/reports/milestone-9.md` and `milestone-9/`, this report and `milestone-10/` |
| Other | `.gitignore` (packaging folders) |

## 3. Verification

All of this ran; nothing below is estimated.

**Unit and integration tests** (Linux, .NET 10, after the last change):

| Suite | Result |
|---|---|
| Core (includes the installer's file-type check) | 423 passed, 0 failed |
| Data (includes the upgrade from every earlier database version) | 53 passed, 0 failed |
| Media (library) | 56 passed, 0 failed |
| YouTube | 35 passed, 0 failed |
| Media server | 61 passed, 0 failed |

**Windows CI** ([run 38823513918](https://github.com/KhanAdeeb1/Aperture-Video-Player/actions/runs/38823513918) and earlier runs of this branch): the same suites on Windows, the libmpv engine tests against every sample file on Windows (pinned build) and Ubuntu, the app build for x64 and ARM64, the launch smoke test, the media server start and stop check, and the 77-screen visual review all passed.

**Install, upgrade and uninstall** (`Test-Install.ps1`, CI job "Release package (x64)", [run 38820403742](https://github.com/KhanAdeeb1/Aperture-Video-Player/actions/runs/38820403742)). Every check passed:

- **Clean install of a stand-in 0.0.9:** the program, libmpv, the licence and notices are in `%LOCALAPPDATA%\Programs\Aperture`; Start menu, Installed apps and Open with for .mkv are set, and Aperture is not the default player. It launches, closes cleanly and creates its database.
- **Upgrade to 0.1.0 while Aperture and the media server run:** both were closed or stopped, the version changed, an old file was removed, the database was kept and not reset, and Installed apps has one entry.
- **The upgraded app opened a video from the command line,** kept playing, closed cleanly, and its log does not contain the file's name.
- **Silent uninstall:** the program folder, Start menu entry, Installed apps entry, Open with and the file type are gone; the library and settings were kept.

The first run of this test found a real release blocker: the published app crashed at launch (0xC000027B) because `dotnet publish` left out the compiled XAML resources, while the build CI had always tested worked. It was fixed, and the build script now refuses a release without them.

**Soak tests.**

| Run | Cycles | Playback | Private memory | Handles | CPU (share of 4 cores) | Playback vs wall time | Result |
|---|---|---|---|---|---|---|---|
| CI, x64 ([run 38820403742](https://github.com/KhanAdeeb1/Aperture-Video-Player/actions/runs/38820403742)) | 20 open/close; window up in 1.0 s median, 1.3 s slowest | 10 min, 1080p H.264 "Harbour Lights" | 100 MB settled, 102 MB at the end, 103 MB highest | 1,101 → 1,167 | 5.0 % median, 8.3 % highest | 586 s in 587 s | Passed |
| Release workflow, x64 ([run 38818818337](https://github.com/KhanAdeeb1/Aperture-Video-Player/actions/runs/38818818337)) | 50 open/close; window up in 0.5 s median, 1.8 s slowest | **120 min**, 1080p60 H.264 at 20 Mbit/s with AC-3 5.1, a 20 GB file | 165 MB settled, 171 MB at the end and highest (flat for the last 50 minutes) | 1,130 → 1,131 (rising and falling between about 1,100 and 1,170 as buffers are reused) | 21.2 % median, 26.6 % highest | 7,185 s in 7,198 s (13 s behind over two hours) | Passed |

The two-hour run used the build from commit 9690145, before this milestone's last changes. Those changes touch only the pointer handling, the uninstaller and the tests, not playback, and the 10-minute run on the final code agrees with it.

Software decoding throughout (no GPU on CI), and no sound device, so the sound output path was not exercised.

**Packages:** `Aperture-0.1.0-x64-setup.exe` 131 MB, `Aperture-0.1.0-x64-portable.zip` 191 MB; the ARM64 installer and zip are built the same way.

## 4. Visual review

All 77 screens from the Windows visual review were looked at at full size, first on the run before this milestone's fixes and again after them, checking each against the design rules (dark charcoal, one Lamp accent, no placeholder styling, no unfinished states, nothing on screen that doesn't need to be). What was wrong is listed under Implemented; everything else was consistent: one panel style for every error and empty state, Lamp only on primary buttons, progress, the loop chip and toggles, and no leftover test text.

Screenshots after the fixes, in `docs/reports/milestone-10/`:

- [07b](milestone-10/07b-mini-player-idle.jpg) and [08b](milestone-10/08b-fullscreen-controls-hidden.jpg): the idle mini player and full screen are now just the video (before, the controls stayed up after the More menu had been used).
- [100](milestone-10/100-seek-preview.jpg): resting on the timeline keeps the controls up and shows the preview (before, both vanished).
- [41](milestone-10/41-subtitles-shown.jpg): a subtitle on screen.
- [75](milestone-10/75-autoplay-off.jpg): the next-episode card sits above the timeline.
- [91](milestone-10/91-settings-media-server-running.jpg): Settings › Media server lists one address a phone can reach.
- [18](milestone-10/18-error-not-found.jpg) and [19](milestone-10/19-unsupported-file.jpg): a missing file and a non-video file without the bug-report button.
- [13](milestone-10/13-home-hover.jpg): hover now really comes from the pointer. One card shows the title-on-charcoal fallback because Windows hadn't made its thumbnail yet; which card does this changes from run to run (see Known limitations).

The installer uses Inno Setup's standard wizard with the Aperture icon; it was run silently in CI and has not been screenshotted.

## 5. Known limitations

### Tests asked for and not executed

| Test | Why not | What it needs |
|---|---|---|
| Hardware decoding (D3D11VA) | CI has no GPU; only software decoding ran. It is enabled (`hwdec=auto-safe`) and falls back to software | Play an HEVC and an AV1 file on a PC with a GPU and check Playback info (I) says hardware |
| HDR | Not supported by design: the ANGLE render path is 8-bit SDR, so HDR10 and HLG are tone-mapped to SDR ([architecture.md](../architecture.md)) | A D3D11 render path, a later milestone |
| Multiple monitors | CI has one virtual display. The display-change logic has unit tests | Move the window, full screen and mini player between two screens; unplug one |
| DPI scaling 125/150/200 % | CI runs at 100 % | Repeat the visual review at each scale |
| Narrator and high contrast | Not available on CI in a useful form. Every control has a UI Automation name (the visual review drives the app by them) and colours meet contrast targets | A run through Home, the player and Settings with Narrator; a high-contrast theme |
| ARM64 | Built and packaged only | Install and play on a Snapdragon PC |
| Media server with a real phone | Owed since Milestone 7; the server tests sign in the way a phone does | Sign in from a phone, play, convert |
| YouTube playback | YouTube refuses embeds on CI (error 150) | Play a video on adi's PC |
| Sound output and track switching by ear | CI has no sound device ("Audio: Off") | Listen while switching tracks |
| Real large media | Synthetic files only, up to 20 GB at 20 Mbit/s | A 4K remux, DTS/TrueHD, picture subtitles |
| Interactive "remove my data" on uninstall | The test uses a silent uninstall, which keeps data by design | Uninstall by hand and answer Yes |

### Limitations of the release

- **A card can show the title fallback instead of a thumbnail** the first time Home appears, when Windows hasn't made the file's thumbnail yet. It should appear once Windows has made it (inferred from the cards that had one in an earlier run, not tested).
- **Not code-signed.** SmartScreen warns on the installer. Signing needs a certificate (paid, or a free one for open-source projects through SignPath) and is the most useful next step for anyone else installing it.
- **No automatic updates.** A security fix in .NET or Kestrel reaches users only with a new Aperture. See the [security review](../security-review.md).
- **Downgrade isn't supported:** an older Aperture refuses a newer database (by design, so it can't damage it).
- **Casting is DLNA only;** Chromecast, AirPlay and Roku aren't supported.
- **The media server and TV conversions use the user's own FFmpeg.**
- **Transcripts and online subtitle search** (from Milestone 9) were not built; they were never approved.

## 6. Regression check

- Every earlier suite passes unchanged. The only test-visible change is that the database's migration list is now visible to tests.
- Real pointer input in the visual review changed some screenshots without changing the app: a hover ring on the first Home card, a tooltip where the pointer last rested, and controls hidden in shots taken after the idle delay. Each was checked.
- The controls fix keeps the earlier rules: controls stay up while paused, while a menu or panel is open, while dragging the timeline and while tabbing through them.
- The media server's address list changed only which addresses are shown; the server tests (61) pass.
- Upgrading from every earlier database version is now tested, so later migrations can't silently break old libraries.

## 7. Next step

Unresolved issues, in the order I'd take them:

1. **Run the hardware tests above on a real PC** (adi): GPU decoding, two monitors, 150 % scaling, Narrator, YouTube, a phone. Then 0.1.0 can be tagged.
2. **Licence follow-ups** (not legal advice):
   - The Windows App SDK is under Microsoft's licence, not the GPL. It is shipped with the app as a runtime library, which the GPL's system-library exception may or may not cover; this is a known grey area for GPL WinUI apps. An explicit additional permission in Aperture's licence for linking with the Windows App SDK and .NET would remove the doubt for Aperture's own code (not for libmpv's).
   - libmpv's corresponding source is pointed to upstream, not mirrored. Mirroring the exact source archives next to each release would make GPL compliance independent of other sites.
   - Patent licensing for H.264, HEVC and AAC decoding, if Aperture is ever distributed commercially.
3. **Code signing** and an opt-in "new version available" check.
4. **HDR output** through a D3D11 render path.
5. Transcripts and online subtitles, if wanted.

To publish 0.1.0 after that: merge this pull request, tag `v0.1.0` on the default branch, wait for the Release workflow, review the draft release and publish it.
