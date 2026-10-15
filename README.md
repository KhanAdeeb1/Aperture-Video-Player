# Aperture

A calm, carefully designed video player for Windows 11, built with C#, WinUI 3 and libmpv.

**Status:** Milestone 5. Shows in the library are put in order by season and episode, with next and previous episode, a Continue Watching row that offers what comes next, and watched tracking. Aperture finds intros, recaps and credits by comparing each episode's sound and picture with other episodes of the show, offers Skip buttons (skipping by itself only when you ask and the evidence is strong), shows the next episode with a countdown you can cancel, and lets you correct any times it found. Earlier milestones: On top of the libmpv player: customizable keyboard shortcuts, speed presets to 2× with optional pitch correction, subtitle and sound sync, subtitle size and colour, a resume-or-start-over choice, remembered per-file choices, and window placement that survives display changes. A personal library scans folders you choose (cancellable, in the background), sorts films, episodes, personal videos and podcasts by their names, finds duplicate copies, and lets you correct anything; online details and posters from TMDB are optional and use your own key. Aperture never changes your media files.

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
src/Aperture.Data    SQLite settings, playback history and the library
src/Aperture.Playback.Mpv  libmpv engine (P/Invoke, events, tracks, diagnostics)
src/Aperture.Video   Direct3D 11 video presenter (ANGLE and software paths)
src/Aperture.Media   library scanning, optional online details, artwork cache
tests/               unit tests
tools/visual-review  test-clip generator and the screenshot driver used by CI
tools/test-media     engine test-file generator
tools/Get-Libmpv.ps1 fetches the pinned libmpv build
third_party/         pins for native binaries (binaries themselves are not committed)
docs/                design system, architecture, plan, reports
```
