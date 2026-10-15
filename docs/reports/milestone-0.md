# Milestone 0 report: repository audit and product plan

Date: 2026-10-15

## 1. Implemented

- Audited the repository: it was empty (no commits, branches or files), so there was no existing code or functionality to preserve.
- Wrote the audit, architecture, playback engine evaluation, folder structure, dependency and license register, risk register, phased plan and v0.1 definition of done ([milestone-0-audit-and-plan.md](../milestone-0-audit-and-plan.md), [architecture.md](../architecture.md), [dependencies.md](../dependencies.md)).
- Created the project skeleton: solution, shared build settings with warnings as errors, central package versions, SDK pin, an empty themed WinUI 3 window, initial design tokens, one real core class (`AppDataLocation`) with tests, and a CI workflow.
- Documented the build and test workflow for Windows and for non-Windows hosts ([build-and-test.md](../build-and-test.md)).

## 2. Files changed

| File | Responsibility |
|---|---|
| `Aperture.sln`, `Directory.Build.props`, `Directory.Packages.props`, `global.json`, `.editorconfig`, `.gitignore` | Solution and shared build configuration |
| `src/Aperture.App/*` | WinUI 3 shell: `App`, `MainWindow`, `Theme/Tokens.xaml`, `app.manifest` (per-monitor DPI v2, long paths) |
| `src/Aperture.Core/Storage/AppDataLocation.cs` | Resolves the per-user data folder; `APERTURE_DATA_DIR` override |
| `tests/Aperture.Core.Tests/Storage/AppDataLocationTests.cs` | 7 tests for the above |
| `.github/workflows/ci.yml` | Core tests on Linux and Windows; app build for x64 and ARM64; launch smoke test on x64 |
| `README.md`, `docs/*` | Plan, architecture, dependencies, build instructions, this report |

## 3. Verification

| What | Where | Result |
|---|---|---|
| `dotnet test tests/Aperture.Core.Tests` | This Linux environment (.NET SDK 10.0.112) | Passed 7, failed 0, skipped 0 |
| Same, Release | GitHub Actions `ubuntu-latest` and `windows-latest` | Success on both |
| `dotnet build src/Aperture.App -c Release -p:Platform=x64` and `ARM64` | GitHub Actions `windows-latest` | Success on both |
| Launch smoke test: start `Aperture.exe`, wait 15 s, require a live process with a main window | GitHub Actions `windows-latest`, x64 | Success: "Aperture started and opened its window: 'Aperture'" |
| WinUI build on Linux | This environment | Fails as expected at `XamlCompiler.exe: Exec format error`; documented |

Not executed: running the app interactively on a real Windows 11 machine, the ARM64 build at runtime, and any playback (none exists yet).

## 4. Visual review

Not done. The window was launched only in headless CI, which checks that it opens, not how it looks. The shell is intentionally empty: charcoal surface `#141517`, extended title bar, the word "Aperture" in small tertiary text. Still rough: no app icon, caption buttons use system defaults, no accent colour (chosen in M2 against real screenshots).

## 5. Known limitations

- No video playback; libmpv is not integrated yet (M1).
- The render path for libmpv inside WinUI is unproven; both candidate paths are documented as unverified (architecture.md §4.2).
- The Windows App SDK 2.5.1 metapackage pulls components Aperture does not use; trimming is deferred to M5.
- The solution file contains default "Any CPU"/x86 configurations from the .NET CLI; build the app with an explicit `-p:Platform=x64` or `ARM64`.
- CI uses `actions/checkout@v4` and `actions/setup-dotnet@v4`, which log a Node.js 20 deprecation warning; harmless today, to be updated when bumping CI.
- The repository has no default branch yet, so this work lives on `claude/milestone-0-audit-84kzf1` without a pull request.

## 6. Regression check

Nothing existed before this milestone, so there was nothing to regress. All new checks are green.

## 7. Next step

Milestone 1: libmpv integration spike. Pin a libmpv build (recording its license), write the P/Invoke layer with tests, prototype both render paths, measure them, and bring back a render-path recommendation for approval.

Decisions needed from adi (neither blocks M1):
1. libmpv/FFmpeg license route (GPL app vs LGPL build), before any public release.
2. Render path, at the end of M1.
