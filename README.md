# Aperture

A calm, carefully designed video player for Windows 11, built with C#, WinUI 3 and libmpv.

**Status:** Milestone 1, design system and first impression. Home (Continue Watching, Recently Played, search), playback with auto-hiding controls, full screen, mini-player, settings, and loading, empty, error and unsupported-file states all run in the app. Playback uses Windows Media Foundation as an interim engine until libmpv is integrated.

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
tests/               unit tests
tools/visual-review  test-clip generator and the screenshot driver used by CI
docs/                design system, architecture, plan, reports
```
