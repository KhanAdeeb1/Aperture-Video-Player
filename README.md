# Aperture

A calm, carefully designed video player for Windows 11, built with C#, WinUI 3 and libmpv.

**Status:** Milestone 0. The repository holds the project skeleton, the architecture and the delivery plan. Nothing plays video yet; playback arrives in Milestone 1.

## Documents

- [Milestone 0 audit and plan](docs/milestone-0-audit-and-plan.md): audit, risks, phased plan, definition of done for v0.1
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
src/Aperture.Core    platform-neutral models and contracts
tests/               unit tests
docs/                architecture, plan, reports
```
