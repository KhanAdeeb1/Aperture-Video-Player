# Aperture

A calm, carefully designed video player for Windows 11, built with C#, WinUI 3 and libmpv.

**Status:** Milestone 2, core playback engine. libmpv plays local files into a Direct3D 11 surface under the WinUI controls, with play, pause, seek, stop, volume, full screen, picture shape, audio and subtitle tracks, external subtitles, speed, resume and a playback-info panel. Missing, damaged and unsupported files get specific messages. Milestone 1's home, settings and mini-player are unchanged.

## Documents

- [Milestone 0 audit and plan](docs/milestone-0-audit-and-plan.md): audit, risks, phased plan, definition of done for v0.1
- [Design system](docs/design-system.md): tokens, components, design review answers, interaction and accessibility rules
- [Architecture](docs/architecture.md): layers, modules, folder structure, playback engine evaluation
- [Dependencies and licenses](docs/dependencies.md)
- [Build and test](docs/build-and-test.md)
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
src/Aperture.Data    SQLite settings and playback history
src/Aperture.Playback.Mpv  libmpv engine (P/Invoke, events, tracks, diagnostics)
src/Aperture.Video   Direct3D 11 video presenter (ANGLE and software paths)
tests/               unit tests
tools/visual-review  test-clip generator and the screenshot driver used by CI
tools/test-media     engine test-file generator
tools/Get-Libmpv.ps1 fetches the pinned libmpv build
third_party/         pins for native binaries (binaries themselves are not committed)
docs/                design system, architecture, plan, reports
```
