# What Aperture does (release 0.1.0)

A list of everything in this release, grouped by where you find it. Each group links to the document or milestone report with the detail. What is *not* in the release, and what hasn't been tested, is in [the Milestone 10 report](reports/milestone-10.md#5-known-limitations).

## Playing a file

- Open from the file picker (Ctrl+O), by dropping a file on the window, from a video's **Open with** menu in Explorer, or on the command line.
- libmpv engine, which plays what FFmpeg reads. Tested: MP4, MKV, MOV, WebM, AVI, WMV, MPEG-TS and OGV containers; H.264, HEVC 10-bit, VP9, AV1, MPEG-2, MPEG-4, WMV, ProRes, FFV1 and Theora video; AAC, AC-3 5.1, Opus, MP3, MP2, WMA, PCM, FLAC and Vorbis sound. See [tested configurations](tested-configurations.md).
- Play, pause, seek, stop, frame steps, speed 0.25× to 2× with natural-sounding voices, volume and mute, picture shape (original, fill, stretch, 16:9, 4:3, 2.39:1).
- Audio and subtitle tracks named by language and format; external subtitle files, loaded automatically when named like the video or found in `Subs` folders, or added by hand or by dropping them.
- Subtitle and sound sync (Z/X, Shift+Z/X), subtitle size, colour and background.
- Resume where you left off, or be asked, or always start over. Each file remembers its tracks, offsets, picture shape, speed and sound options; each show remembers its languages.
- Full screen, a mini player that stays on top, controls that fade when idle, and windows that come back to a sensible place when a display is unplugged.
- A picture of the video when pointing at the timeline; bookmarks with notes (B); A–B loop (Shift+L); clearer dialogue (D) and even volume (E).
- Playback info (I) with codec, resolution, frame rate, decoding, bitrate, dropped frames and sync.
- Specific screens for a missing, damaged, unsupported or non-video file; one automatic reopen if a file fails mid-play; a bug report copied to the clipboard with names and paths removed.
- Every shortcut can be changed (Settings › Keyboard shortcuts); F1 lists them.

Detail: [Milestone 2](reports/milestone-2.md), [Milestones 3 and 4](reports/milestones-3-4.md), [Milestone 9](reports/milestone-9.md).

## Library

- Folders you choose, scanned in the background with progress and Cancel, rescanned quietly after launch.
- Films, shows, personal videos and podcasts, sorted from file and folder names; filters, sort and search.
- Duplicate copies grouped into one card that plays the best copy, with the others one click away.
- Watched marks, Continue watching, Recently played.
- Correct any detail by hand; corrections are never overwritten.
- Optional details and posters from TMDB with your own key (off until you add one).
- Your media files are only read, never renamed, tagged, moved or written beside.

Detail: [Milestones 3 and 4](reports/milestones-3-4.md).

## Shows

- Episodes in season order, next and previous episode, and Continue watching that offers the next episode.
- Intro, recap and credits found by comparing episodes' sound and picture, with Skip buttons, optional automatic skipping only when the evidence is strong, and hand corrections.
- A next-episode card with a countdown you can cancel, a "Still watching?" check, and per-show settings.

Detail: [Milestone 5](reports/milestone-5.md).

## YouTube (optional)

- Paste a link or search with your own API key; watch in YouTube's own embedded player; Saved and Continue watching.
- Sign in with your own Google client to see liked videos and playlists.

Detail: [YouTube in Aperture](youtube.md).

## Phones, tablets and TVs (optional)

- A media server, started only when you ask, that lets phones and tablets on your home or private network browse shared folders and play in the browser, with a passphrase, encryption, synced positions, and conversion with your own FFmpeg when needed. See [Watching on your phone](media-server.md) and the [security review](security-review.md).
- Play on a DLNA TV or media player found on your network, with Aperture as the remote. See [Playing on a TV](casting.md).

## Installing

- A per-user installer (no administrator rights) for x64 and ARM64, a portable zip, upgrade that keeps your library, and an uninstaller that asks before removing your data. See [Installing, upgrading and removing](install.md).
