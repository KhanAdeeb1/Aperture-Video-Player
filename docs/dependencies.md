# Dependency and license register

Every dependency must appear here with its license and the reason it is needed before it is added to `Directory.Packages.props` or `third_party/`. "Checked" means the fact was looked up on the date shown; it is not legal advice.

## In use (Milestone 1)

| Dependency | Version | License | Why | Notes |
|---|---|---|---|---|
| .NET SDK / runtime | 10.0 (SDK 10.0.1xx) | MIT | Language and runtime | .NET 10 is the current LTS line; .NET 11 was at RC 2 on NuGet (checked 2026-10-15) |
| Microsoft.WindowsAppSDK | 2.5.1 | Microsoft Software License (requires acceptance) | WinUI 3 and windowing APIs | Latest stable on NuGet, published 2026-09-16 (checked). It is a metapackage that also pulls AI, ML, Search and Widgets components; switching to only the WinUI/Foundation component packages to shrink the self-contained app is to be evaluated in M5 |
| Microsoft.Windows.SDK.BuildTools | 10.0.28000.2705 | Microsoft Software License | Windows SDK build tooling required by WinUI projects | Build-time only |
| Microsoft.Data.Sqlite | 10.0.13 | MIT (bundles SQLite via SQLitePCLRaw: SQLite public domain, SQLitePCLRaw Apache-2.0) | Settings and playback history (`Aperture.Data`) | Moved forward from M4 because the home screen's Continue Watching row needs real stored positions. Latest stable 10.0.x on NuGet (checked 2026-10-15) |
| Windows Media Foundation (`Windows.Media.Playback.MediaPlayer`) | Part of Windows | Windows component, nothing redistributed | **Interim** playback engine behind `IPlaybackEngine` until libmpv lands | Plays what Windows can decode; HEVC/VP9/AV1 depend on Store codec extensions. Windows Server (CI) needs the `Server-Media-Foundation` feature |
| xunit, xunit.runner.visualstudio, Microsoft.NET.Test.Sdk | 2.9.3, 3.1.4, 17.14.1 | Apache-2.0 / Apache-2.0 / MIT | Unit tests | Test-only, not shipped. xunit v3 (4.x) is available; staying on v2 until there is a reason to move |

## Planned

| Dependency | License | Why | Milestone | Open questions |
|---|---|---|---|---|
| **libmpv** (`libmpv-2.dll`) | GPLv2+ by default; LGPLv2.1+ only when built without GPL-only files (`-Dgpl=false`). Verified in mpv's `Copyright` file on master, 2026-10-15 | Playback engine | M1 | The mpv project does not ship Windows binaries. Third-party builds (for example the `mpv-winbuild-cmake` builds) must be checked for which license they were built under and which FFmpeg configuration they include. **Decision needed from adi before release:** either accept GPL obligations for the whole app (Aperture's source would have to be distributed under GPL-compatible terms) or produce/obtain an LGPL build and dynamically link it, shipping license texts and offering corresponding source. Development can proceed with either; release cannot. |
| FFmpeg (inside libmpv) | LGPL-2.1+ or GPL depending on build flags | Demuxing and decoding | M1 | Same as above. Also: shipping H.264/HEVC/AAC decoders can carry patent-licensing implications in some jurisdictions; needs a decision before public distribution |
| libass, and other libmpv deps (HarfBuzz, FreeType, FriBidi, libplacebo…) | ISC / MIT / FreeType / LGPL (per library; to be enumerated from the chosen build) | Subtitles, shaping, rendering | M1 | Enumerate from the exact build into `THIRD-PARTY-NOTICES.md` |
| FFprobe | Same as FFmpeg build | Library scanning metadata | M6 | May be unnecessary if libmpv can serve the same need |
| CommunityToolkit.Mvvm | MIT | Source-generated observable properties/commands, removes MVVM boilerplate | M2 | Only if ViewModels grow enough to justify it |
| Microsoft.Extensions.DependencyInjection / Logging | MIT | Composition root and structured logging abstractions | M1–M2 | Logging sink: a small rolling file logger of our own vs. Serilog (Apache-2.0), decided in M4 |

## Build and CI tools (not shipped)

| Tool | License | Why |
|---|---|---|
| FFmpeg (Ubuntu package, CI only) | LGPL/GPL per build | Generates synthetic test clips for the visual-review job (`tools/visual-review/make-test-media.sh`). Nothing it produces is committed or shipped |

## Rules

- No dependency is added without a row here and a sentence on why it is necessary.
- Native binaries are never committed. `third_party/README.md` (M1) records source URL, version, SHA-256 and license for each, and a script fetches them.
- `THIRD-PARTY-NOTICES.md` is generated before the first release from this register.
