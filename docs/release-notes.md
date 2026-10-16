# Aperture 0.1.0

The first release of Aperture, a calm video player for Windows 11.

**This is an early release, not a finished product.** It has been tested automatically on GitHub's virtual Windows machines, which have no graphics card, no sound device and one screen. It has not yet been tested with hardware video decoding, HDR, several monitors, display scaling above 100 %, Narrator, on an ARM PC, or with a real phone. Please report what you find.

## Download

| PC | File |
|---|---|
| Most PCs (Intel, AMD) | `Aperture-0.1.0-x64-setup.exe` |
| ARM PCs (Snapdragon) | `Aperture-0.1.0-ARM64-setup.exe` (built but never started on real hardware) |
| Without installing | `Aperture-0.1.0-x64-portable.zip` or `…-ARM64-portable.zip` |

Check a download against `SHA256SUMS.txt`. The installer isn't code-signed, so Windows SmartScreen will warn you; see [Installing, upgrading and removing](https://github.com/KhanAdeeb1/Aperture-Video-Player/blob/claude/milestone-0-audit-84kzf1/docs/install.md). No administrator rights are needed, and Aperture doesn't make itself your default player.

## What's in it

- A libmpv player: most video formats, audio and subtitle tracks, subtitle and sound sync, speeds to 2×, resume, full screen and a mini player, customizable shortcuts, seek previews, bookmarks, A–B loop, clearer dialogue and even volume.
- A library of films, shows, personal videos and podcasts from folders you choose, with duplicate copies grouped and optional TMDB details with your own key. Your media files are only ever read.
- Shows in episode order with next episode, Continue watching, and intro, recap and credits detection with Skip buttons.
- Optional: YouTube in YouTube's own player, a media server for phones and tablets on your home network, and playing on DLNA TVs.

The full list: [What Aperture does](https://github.com/KhanAdeeb1/Aperture-Video-Player/blob/claude/milestone-0-audit-84kzf1/docs/features.md).

## Known limitations

- No HDR output (HDR video is shown tone-mapped to SDR).
- No automatic updates; install a newer release over this one.
- Casting is DLNA only (no Chromecast, AirPlay or Roku).
- The media server and casting need your own FFmpeg for files a device can't play as they are.

## Licence

GPL-3.0-or-later. Aperture includes GPL builds of libmpv and FFmpeg; see `THIRD-PARTY-NOTICES.md` in the program folder for every component, its licence and where its source is.
