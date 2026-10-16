# Aperture

A calm, carefully designed video player for Windows 11, built with C#, WinUI 3 and libmpv.

**Status: release 0.1.0 candidate (Milestone 10).** Everything from Milestones 1 to 9 is in one build with a Windows installer, but it has only been tested on GitHub's virtual Windows machines (no GPU, no sound device, one display) and is **not production-ready**: hardware decoding, HDR, several monitors, display scaling above 100 %, Narrator, the ARM64 build, a real phone and YouTube playback on a real PC are still untested. See [tested configurations](docs/tested-configurations.md) and [the Milestone 10 report](docs/reports/milestone-10.md).

Aperture plays local video through libmpv with a calm, keyboard-friendly player; keeps a personal library of films, shows, personal videos and podcasts (never changing your files); finds intros and credits by comparing episodes; plays YouTube in YouTube's own player; lets phones on your home network watch your library; and plays on DLNA TVs. The full list is in [What Aperture does](docs/features.md).

## Install

Download the installer from the repository's Releases page (or, between releases, the `aperture-x64` artifact of a CI run) and see [Installing, upgrading and removing](docs/install.md). Windows 11, x64 or ARM64, no administrator rights needed.

## Licence

Aperture is free software under the [GNU General Public License, version 3 or later](LICENSE). It includes libmpv and FFmpeg (GPL builds) and other components listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

## Documents

- [What Aperture does](docs/features.md)
- [Installing, upgrading and removing](docs/install.md)
- [Tested configurations and media samples](docs/tested-configurations.md)
- [Security review of the media server and casting](docs/security-review.md)
- [Release notes](docs/release-notes.md)
- [Milestone 0 audit and plan](docs/milestone-0-audit-and-plan.md): audit, risks, phased plan, definition of done for v0.1
- [Design system](docs/design-system.md): tokens, components, design review answers, interaction and accessibility rules
- [Architecture](docs/architecture.md): layers, modules, folder structure, playback engine evaluation
- [Dependencies and licenses](docs/dependencies.md)
- [Build and test](docs/build-and-test.md)
- [YouTube in Aperture](docs/youtube.md): setup, quota, what is stored, limits
- [Watching on your phone](docs/media-server.md): the media server's setup, laptops and power, remote access, security design
- [Playing on a TV](docs/casting.md): how to cast, what plays directly, firewall, tested devices, what isn't supported
- [Milestone reports](docs/reports/)

## Quick start (Windows 11, .NET 10 SDK)

```powershell
dotnet test tests/Aperture.Core.Tests/Aperture.Core.Tests.csproj
dotnet run --project src/Aperture.App/Aperture.App.csproj -p:Platform=x64
```

On Linux or macOS only the platform-neutral projects build; see [Build and test](docs/build-and-test.md).

## Layout

```
src/Aperture.App     WinUI 3 shell (Windows only)
src/Aperture.Core    platform-neutral models, rules and contracts
src/Aperture.Data    SQLite settings, playback history and the library
src/Aperture.Playback.Mpv  libmpv engine (P/Invoke, events, tracks, diagnostics)
src/Aperture.Video   Direct3D 11 video presenter (ANGLE and software paths)
src/Aperture.Media   library scanning, optional online details, artwork cache
src/Aperture.YouTube YouTube Data API, oEmbed, Google sign-in, Continue watching and Saved
src/Aperture.Server  media server (separate process) and the phone web client
tests/               unit tests
tools/visual-review  test-clip generator and the screenshot driver used by CI
tools/test-media     engine test-file generator
tools/Get-Libmpv.ps1 fetches the pinned libmpv build
tools/Build-Release.ps1 builds the installer and portable zip
tools/packaging     install/upgrade/uninstall and soak tests
installer/          Inno Setup script
third_party/         pins for native binaries (binaries themselves are not committed)
docs/                design system, architecture, plan, reports
```
