# Aperture

A calm, carefully designed video player for Windows 11, built with C#, WinUI 3 and libmpv.

**Status:** Milestone 9 (below). In Milestone 5, shows in the library were put in order by season and episode, with next and previous episode, a Continue Watching row that offers what comes next, and watched tracking. Aperture finds intros, recaps and credits by comparing each episode's sound and picture with other episodes of the show, offers Skip buttons (skipping by itself only when you ask and the evidence is strong), shows the next episode with a countdown you can cancel, and lets you correct any times it found. Earlier milestones: On top of the libmpv player: customizable keyboard shortcuts, speed presets to 2× with optional pitch correction, subtitle and sound sync, subtitle size and colour, a resume-or-start-over choice, remembered per-file choices, and window placement that survives display changes. A personal library scans folders you choose (cancellable, in the background), sorts films, episodes, personal videos and podcasts by their names, finds duplicate copies, and lets you correct anything; online details and posters from TMDB are optional and use your own key. Aperture never changes your media files.

**Milestone 9** adds optional extras that stay out of the way until used: a picture of the video when pointing at the timeline, bookmarks with notes (B), A–B looping (Shift+L), clearer dialogue (D) and even volume (E) processed on your PC, subtitles found in release `Subs` folders and by episode number, audio and subtitle language carried from one episode of a show to the next, and a "Copy details for a bug report" button that removes file names before anything leaves the app (it only goes to your clipboard). See [the Milestone 9 report](docs/reports/milestone-9.md) for what was left out and why.

**Milestone 8** adds Play on a TV: Aperture finds DLNA TVs and media players on your home network, sends the video there (starting where you were, converting with your own FFmpeg only when the TV can't play the original), and becomes a remote for it, saving your place as it plays and handling a TV that disappears. Chromecast, AirPlay and Roku aren't supported; Windows+K or a cable covers those. See [Playing on a TV](docs/casting.md).

**Milestone 7** adds a media server: phones and tablets on your home network (or your own private network, such as Tailscale) can browse the folders you share and play them in the browser, with sign-in, encryption, positions synced with Aperture, and conversion with your own FFmpeg only when a device can't play the original. It runs on its own, separately from the app. See [Watching on your phone](docs/media-server.md).

**Milestone 6** adds a YouTube section: paste a link or search (with your own API key), watch in YouTube's own embedded player, keep a Saved list and Continue watching, and optionally see your liked videos and playlists after signing in with your own Google client. See [YouTube in Aperture](docs/youtube.md).

## Documents

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
third_party/         pins for native binaries (binaries themselves are not committed)
docs/                design system, architecture, plan, reports
```
