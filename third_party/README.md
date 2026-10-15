# Third-party native binaries

Native binaries are never committed. Each one is pinned here and fetched by a script that checks its hash. Licenses and the reasons for each dependency are in [docs/dependencies.md](../docs/dependencies.md).

## libmpv

| | |
|---|---|
| Source | [shinchiro/mpv-winbuild-cmake](https://github.com/shinchiro/mpv-winbuild-cmake), GitHub release `20261015` |
| mpv revision | git `492bae0768` |
| x64 archive | `mpv-dev-x86_64-20261015-git-492bae0768.7z`, SHA-256 `5db58c1768587b35f2d8daa11b53de4aec58197c00ea1d676f998f183a358ba8` |
| ARM64 archive | `mpv-dev-aarch64-20261015-git-492bae0768.7z`, SHA-256 `c6bf2285aeebbacec7893201f8bc164145bb04ac725cb3d33ae2ef1280c64769` |
| What is used | `libmpv-2.dll` only (about 121 MB for x64, 83 MB for ARM64) |
| License | Treated as GPLv2+; see the open decision in dependencies.md |
| Pin file | [`libmpv.json`](libmpv.json) |

Fetch it (Windows, PowerShell 5.1 or 7):

```powershell
powershell -File tools/Get-Libmpv.ps1 -RuntimeIdentifier win-x64     # or win-arm64
```

The script downloads the archive, refuses it if the SHA-256 differs, extracts `libmpv-2.dll` to `third_party/bin/<rid>/` (ignored by git), and writes a stamp so later builds skip the download. Building `Aperture.App` with a runtime identifier runs it automatically.

To move to a newer build: run `tools/Get-Libmpv.ps1 -Discover` to list the newest release's assets, review the release, update `libmpv.json` with the new file names and hashes, and run the engine tests and visual review in CI before merging. shinchiro prunes old releases, so a pinned release can disappear upstream; if the download fails with 404, a new pin is needed.

## ANGLE

ANGLE comes from the `Avalonia.Angle.Windows.Natives` NuGet package (pinned in `Directory.Packages.props`; NuGet verifies the package hash), which places `av_libglesv2.dll` next to the app.
