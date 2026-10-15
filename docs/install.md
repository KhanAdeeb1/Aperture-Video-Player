# Installing, upgrading and removing Aperture

Aperture runs on **Windows 11** (x64, or ARM64 such as Snapdragon laptops). Each release has two downloads per kind of PC, plus a list of checksums:

| File | What it is |
|---|---|
| `Aperture-0.1.0-x64-setup.exe` | The installer for most PCs (Intel and AMD). |
| `Aperture-0.1.0-ARM64-setup.exe` | The installer for ARM PCs (Snapdragon). |
| `Aperture-0.1.0-x64-portable.zip`, `…-ARM64-portable.zip` | The same program as a folder, for people who prefer not to install. |
| `SHA256SUMS.txt` | SHA-256 of each file. Check one with `Get-FileHash .\Aperture-0.1.0-x64-setup.exe` in PowerShell. |

Releases are made by the [Release workflow](../.github/workflows/release.yml) from a version tag, as a draft that the owner reviews and publishes on the repository's Releases page. Between releases, the same files from any commit are attached to that commit's CI run (job "Release package", artifact `aperture-x64` or `aperture-ARM64`) for 14 days.

## Install

1. Run `Aperture-0.1.0-x64-setup.exe` (or the ARM64 one).
2. **Windows SmartScreen** will probably say "Windows protected your PC", because the installer isn't signed with a paid code-signing certificate. Choose **More info › Run anyway** if the file came from the project's own Releases page and its checksum matches.
3. No administrator rights are needed. Aperture installs for your Windows account only, into `%LOCALAPPDATA%\Programs\Aperture`, and adds itself to the Start menu and to Settings › Apps › Installed apps. A desktop shortcut is optional.
4. Aperture appears in **Open with** for video files (MP4, MKV, MOV, WebM, AVI, WMV, TS, M2TS, MPG, 3GP, OGV and others). It doesn't make itself the default player; to do that, right-click a video › Open with › Choose another app › Aperture › Always.

Nothing else needs installing: .NET, the Windows App SDK and the playback engine (libmpv) are inside. Optional extras:

- **FFmpeg**, only for the media server's conversions for phones and TVs that can't play a file as it is: `winget install Gyan.FFmpeg`. See [Watching on your phone](media-server.md#ffmpeg).
- **Keys** for online artwork (TMDB) and YouTube search, only if you want those. See [YouTube in Aperture](youtube.md).

### Portable use

Unzip the portable zip anywhere you can write to and run `Aperture.exe`. It keeps its library and settings in the same place as the installed version (below). To keep them next to the program instead, for example on a USB drive, start it from a shortcut or script that sets `APERTURE_DATA_DIR` to an absolute folder path first.

## Upgrade

Run the newer installer. It closes Aperture and stops the media server if they are running, replaces the program files (removing any left over from the old version), and keeps everything you've built up.

- Your library, watch history, bookmarks, settings and keys are kept.
- If the newer version needs to change the database, it first keeps a copy of the old one as `aperture.db.v<N>.bak` in the data folder. Every database version Aperture has shipped is tested to upgrade cleanly.
- Going back to an older version after an upgrade isn't supported: an older Aperture refuses a database from a newer one rather than damaging it. To go back, uninstall, put the `.bak` copy back as `aperture.db`, and install the older version.
- Phones and tablets stay signed in to the media server; TVs need nothing.

## Remove

Settings › Apps › Installed apps › Aperture › Uninstall (or Start › right-click Aperture › Uninstall).

The uninstaller removes the program, its Start menu entry and its "Open with" registration, then asks whether to **also remove your library, watch history, bookmarks and settings**. The answer defaults to **No**, so reinstalling later brings everything back.

| If you answer | What stays on the PC |
|---|---|
| No | `%LOCALAPPDATA%\Aperture` (database, logs, artwork and episode analysis caches, media server settings and its certificate), keys in Credential Manager, and the media server's certificate in your certificate store |
| Yes | Nothing of Aperture's, except a Windows Firewall rule for the media server if you allowed it (remove it in Windows Defender Firewall › Allow an app). Sign out of YouTube in Aperture first if you want Google to forget its access; otherwise remove it in your Google Account › Security › Third-party access |

**Your videos are never touched**, whichever you choose: Aperture only reads them.

A silent uninstall (`unins000.exe /VERYSILENT`, used by scripts) never removes your data.

## Where Aperture keeps things

| What | Where |
|---|---|
| Program | `%LOCALAPPDATA%\Programs\Aperture` (installer) or wherever you unzipped it |
| Library, history, settings | `%LOCALAPPDATA%\Aperture\aperture.db` |
| Logs (no file names at the default level) | `%LOCALAPPDATA%\Aperture\Logs` |
| Artwork downloaded from TMDB, intro and credits analysis | `%LOCALAPPDATA%\Aperture\Artwork`, `…\Analysis` |
| Media server settings, conversions in progress | `%LOCALAPPDATA%\Aperture\Server` |
| TMDB and YouTube keys, Google sign-in | Windows Credential Manager, entries starting `Aperture/` |
| Media server certificate authority | Your certificate store (Current User › Personal), "Aperture Media Server" |
