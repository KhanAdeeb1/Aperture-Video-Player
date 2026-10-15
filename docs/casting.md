# Playing on a TV

Aperture can send the video you're watching to a TV on your home network, then work as a remote for it: pause, move along the timeline, skip back or forward, and stop. Your place is saved as the TV plays, so you can stop on the TV and carry on at the PC, or the other way round.

**Not every TV can be reached this way, and not every TV plays every file.** Aperture uses DLNA (UPnP AV), a standard many smart TVs and media players have built in. It does not use Chromecast, AirPlay or Roku. For those, and for any screen Aperture can't find, use Windows' own wireless display (**Windows+K**, Miracast) or an HDMI cable; Aperture then just plays full screen on that display.

## How to use it

1. Turn the TV on and make sure it is on the same home network as the PC. On some TVs, DLNA has to be allowed first (often called *DMR*, *Media renderer*, *Screen share*, *AllShare* or *Allow remote devices*; names vary).
2. In Aperture, open a video, then **More › Play on a TV**.
3. If the media server is off, the panel says so and offers **Start media server**. The TV fetches the video from it, so it has to run. You don't need to share any folders or set a passphrase for this.
4. Pick the TV. Aperture shows *Connecting to …*, then *Playing on …* with the TV's position. The video starts where you were on the PC.
5. **Stop casting** ends it on the TV and brings the player back to where the TV stopped, paused; press Space to carry on at the PC.

While casting, the desktop player is a remote: Space, the arrow keys and the timeline control the TV, and the desktop doesn't play. If you close the window or open another video, the TV keeps playing; the cast is run by the media server, not the window. Opening the same video again shows the remote for it.

### What you'll see when things go wrong

| Situation | What Aperture does |
|---|---|
| No TV found | "No TVs found. Check the TV is on and on the same network as this computer." with **Search again**. Other common causes: DLNA is off on the TV, or the network is set to Public in Windows (see Firewall below). |
| The TV can't play the original file | Aperture tries once more with a version converted to H.264/AAC MPEG-TS, which nearly every DLNA TV plays (needs FFmpeg, see below). If that fails too, or FFmpeg isn't installed, Aperture says the TV couldn't play the file and returns to the desktop player. Nothing on the PC changes. |
| The TV can't start part-way | Some TVs refuse to seek. The video starts at the beginning on the TV and Aperture says so. Your saved place on the PC is not overwritten by the TV's 0:00; it moves only once the TV has really played on for a minute. |
| The TV is switched off, or the Wi-Fi drops | After three missed answers: "Waiting for …", "… stopped answering. Aperture keeps trying, and your place is saved." If the TV comes back within 90 seconds, casting carries on. After that the cast ends and the last position the TV really reached is kept. |
| Someone starts something else on the TV (its own remote, a phone app) | Aperture notices the TV is playing something that isn't ours, ends the cast without stopping the TV, and keeps your place. |
| The video reaches the end on the TV | It is marked finished, the same as on the PC. |
| The TV stops by itself (its own Stop button) | "Stopped on …. Your place is saved." |

## Which files play directly

Before sending, Aperture asks the TV which formats it accepts and looks at the file with FFprobe (if FFmpeg is installed). Then, in this order:

1. **Direct play**: the original file, byte for byte, when the TV lists the container and Aperture can tell the video and sound are ones TVs play: H.264 (8-bit) with AAC, AC-3, E-AC-3 or MP3 sound. HEVC, VP9 or AV1 count only if the TV names them. Seeking is instant.
2. **Repackaging**: H.264 in a container the TV doesn't list (often MKV) is moved into MPEG-TS without re-encoding; only unsupported sound is converted.
3. **Converting**: anything else (HEVC on a TV that doesn't list it, 10-bit H.264, old codecs) is converted to H.264 and AAC in MPEG-TS, up to 1080p and never larger than the original, using the same hardware encoder the media server already picked.

Repackaging and converting need FFmpeg (Settings › Media server › Converting for devices). Without it, Aperture can't look inside the file first: it sends the original whenever the TV lists the file's type, and otherwise says that converting it needs FFmpeg on this computer. Moving along the timeline of a repackaged or converted stream starts a new stream from that point; the TV takes a few seconds to pick it up. Your files are only ever read.

## Firewall and network

- The TV fetches the video from the media server's setup port (8080 by default, plain HTTP, the same port as the phone setup page). The first time the server starts, allow `Aperture.Server` on **Private networks only**. If your Wi-Fi is set to *Public* in Windows, the TV can't connect and searches find nothing; change it under Settings › Network & internet › Wi-Fi › *your network* › Network profile type › Private.
- Searching sends a standard SSDP query to 239.255.255.250:1900 from each of the PC's home-network addresses, and listens for answers for a few seconds. Routers that block multicast between Wi-Fi and wired devices (some mesh systems, "AP isolation", guest networks) hide the TV; put both on the same main network.
- Aperture never changes firewall, router or TV settings.

## Security design

- **TVs only on your home network.** A TV's answer is accepted only from a home-network or loopback address, and only if the address it gives for its description is the same address that answered. Its control addresses must be on that same address. Descriptions are read with DTD processing off and a size limit.
- **One file, one TV, while casting.** Each cast gets a random 48-character address (`/cast/<token>/…`) that serves exactly that file, only to the TV's own address, and only until the cast ends. Nothing else is reachable through it; no path is in it, and the TV is never sent a path. Phones signed in to the media server are unaffected and still need HTTPS and a passphrase.
- **The PC controls the cast through the server's loopback control address**, with the per-start token in `server.json`, like the rest of Settings › Media server. The path is checked there too: it must be a full path to an existing video, with no relative parts.
- **The TV's name is only shown**, trimmed to one line of 80 characters, never interpreted.

## Progress, and why the PC and TV don't fight

- **One writer at a time.** While a cast runs, the media server is the only thing that saves the video's position. The desktop player doesn't save at all while it is a remote, so the two never overwrite each other.
- **Only real positions are saved.** TVs report 0:00 while loading, keep reporting the old place for a moment after a jump, and some jump back to the start when they can't seek. A position is saved only once the TV has reached the place Aperture asked for, or has clearly been playing on its own from where it is for a few seconds; a place earlier than asked for needs a minute of steady playback first. Reports about a previous stream are ignored.
- **Saved** every 10 seconds while playing, on pause, when the cast ends and when it is lost; finished when it ends at the end. Your **Remember played videos** setting is respected.
- When casting ends, the desktop player moves to where the TV stopped.

## Tested devices and protocols

| Renderer | Protocol | How it was tested | Result |
|---|---|---|---|
| gmrender-resurrect (`gmediarender` 0.1, Ubuntu 24.04 package 0.1-1build3, libupnp 1.14.18, GStreamer 1.24.1), a widely used open-source DLNA renderer | UPnP AV MediaRenderer 1 (AVTransport, ConnectionManager), SSDP discovery | By hand, against the real media server on Linux over real SSDP multicast: discovery; direct play of an H.264/AAC MP4 with a resume point; pause, play, seek, stop; playing to the end; killing the renderer mid-cast (Reconnecting, then carrying on after a restart); something else taking it over. A second instance limited to MPEG/MP4 types received an HEVC/Opus MKV converted to 720p MPEG-TS, and a seek started a new stream. | All worked as described above. |
| Aperture's test renderer (`tests/Aperture.Server.Tests/FakeRenderer.cs`) | Same | 15 automated tests in CI, including a TV that refuses seeking, fails originals, is unplugged and plugged back in, reaches the end, or is taken over; and FFmpeg fallback and conversion. | Pass in CI on Ubuntu and Windows. |
| Aperture's stand-in TV (`tools/visual-review/fake-tv.py`) | Same | The Windows visual review: finds it over real SSDP, casts from the desktop app, pauses and stops. It fetches the start of the file to prove it is served. | Run in CI on Windows. |

**Not yet tested on a real TV.** Samsung, LG, Sony, Philips and Panasonic TVs implement DLNA rendering, and Aperture sends what they are known to need (DIDL-Lite metadata with the file's type, DLNA content-features headers, range requests, and a fallback from relative to absolute seeking), but none has been tried. Treat any brand as unverified until it has been.

### Not supported, and why

| | Why not | Use instead |
|---|---|---|
| Chromecast / Google TV | The Cast protocol needs a receiver app registered with Google and their sender SDK, which has no supported Windows desktop version; the default receiver plays only a narrow set of formats. | Windows+K if the TV supports Miracast, a cable, or the phone web client and the phone's own Cast button. |
| AirPlay (Apple TV, AirPlay TVs) | Apple licenses AirPlay to device makers; there is no supported way for a Windows app to send to it. | A cable, or the phone web client on an iPhone and its AirPlay button. |
| Roku | Needs a Roku channel of our own published through Roku. Many Roku TVs also speak DLNA ("Play on Roku" / Media Player), which may work. | DLNA if offered, otherwise a cable. |
| Miracast inside Aperture | Windows already does this well (Windows+K), with the whole screen. | Windows+K. |
