# Build and test

## Requirements

| What | Needed for |
|---|---|
| Windows 11 (x64 or ARM64) | Building and running the app |
| .NET SDK 10.0.100 or a later 10.0.1xx band (pinned by `global.json`) | Everything |
| Visual Studio 2026, or VS 2022 17.14+ with the ".NET desktop development" and "WinUI application development" workloads | Optional; for XAML designer and debugging. The command line works without it. Unverified: the exact minimum VS version that understands .NET 10 + Windows App SDK 2.x |
| Any OS with the .NET 10 SDK | Building and testing the platform-neutral projects only |

## On Windows

```powershell
# Platform-neutral tests
dotnet test tests/Aperture.Core.Tests/Aperture.Core.Tests.csproj

# Build the app (pick x64 or ARM64)
dotnet build src/Aperture.App/Aperture.App.csproj -c Debug -p:Platform=x64

# Run it
dotnet run --project src/Aperture.App/Aperture.App.csproj -p:Platform=x64
```

In Visual Studio, open `Aperture.sln`, set `Aperture.App` as the startup project, choose `x64`, and press F5.

The app runs unpackaged and self-contained, so no separate Windows App SDK runtime install is required.

Milestone 0 expectation: a dark charcoal window titled "Aperture" with the word "Aperture" in small text in the centre. It has no controls yet.

## On Linux or macOS

Only the platform-neutral projects build there:

```bash
dotnet test tests/Aperture.Core.Tests/Aperture.Core.Tests.csproj
```

`dotnet build Aperture.sln` fails on non-Windows hosts because the WinUI XAML compiler (`XamlCompiler.exe`) is a Windows executable. This was confirmed in the Milestone 0 Linux environment.

## Continuous integration

`.github/workflows/ci.yml` runs on every push and pull request:

- **Core tests** on `ubuntu-latest` and `windows-latest`.
- **Windows app build** on `windows-latest` for `x64` and `ARM64`.

CI builds the app but does not launch it; visual review uses a local run or screenshots.

## Data location

Aperture stores its own data (database, logs) in `%LOCALAPPDATA%\Aperture`. Set `APERTURE_DATA_DIR` to an absolute path to use a different folder, for example for a portable copy or a clean test profile.

## Native dependencies

From Milestone 1, libmpv is required at runtime. It is not committed to git; `third_party/README.md` will document the exact build, its hash and license, and the fetch script.
