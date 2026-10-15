# Milestone 8 report: Smart TV and cross-device continuity

Date: 2026-10-15

**What's needed from you: one try on a real TV.** Casting was tested against a real open-source DLNA renderer on Linux, against a fake TV in automated tests, and from the real desktop app on Windows against a stand-in TV. It has not been tried on a Samsung, LG, Sony or any other real TV. To try it, turn the TV on and make sure it's on the same Wi-Fi as the PC. In Aperture, open a video and choose More › Play on a TV, press Start media server if asked, then pick the TV. The guide, including what to check on the TV if it isn't found, is [docs/casting.md](../casting.md). Conversion for files the TV can't play needs FFmpeg (`winget install Gyan.FFmpeg`). No packages were added.

## 1. Implemented

- **The protocol is DLNA/UPnP AV, chosen after evaluating the options.** It is the one TV protocol a Windows app can drive without a vendor's SDK or approval, and many smart TVs and media players have it built in. The others were rejected for these reasons:
  - **Google Cast:** it needs a registered receiver app and a sender SDK with no supported Windows desktop version.
  - **AirPlay:** it is licensed to device makers only.
  - **Roku:** it needs a published channel.
  - **Miracast:** Windows already does it with Windows+K, and does it better than an app could.

  The panel says plainly what works and points to Windows+K or a cable for anything else. There is no Cast button on the player bar. Aperture can't know whether a TV is there until it searches, so a permanent button would be a promise it can't keep.
- **Discovery.** Aperture sends a standard SSDP search from each of the PC's home-network addresses. It then reads each TV's description and asks which formats the TV accepts. TVs are accepted only from home-network addresses, and only when their answer, description and control addresses are all the same machine.
- **Sending to a TV.** More › Play on a TV opens a small panel that lists the TVs found, with Search again. If the media server is off, the panel says so and offers Start media server. Picking a TV:
  1. saves the desktop's position;
  2. chooses how to send the file:
     - **direct play** when the TV lists the format and the codecs are ones TVs play;
     - **MPEG-TS repackaging** for H.264 in a container the TV doesn't take;
     - **H.264/AAC MPEG-TS conversion** with the user's FFmpeg otherwise;
  3. starts the TV at the desktop's position. If the TV refuses to start part-way, the message says so.

  If the TV fails to play the original, Aperture tries once more with a converted stream.
- **Connection and playback status.** The player becomes a remote showing:
  - the state: *Connecting to…*, *Playing on…*, *Paused on…*, *Loading on…* or *Waiting for…*;
  - the title and the TV's position on a timeline you can drag;
  - back 10 s, play/pause and forward 10 s;
  - Stop casting;
  - one line saying how the file is sent and why.

  Space, the arrow keys and the timeline control the TV, and the desktop doesn't play while casting. The cast runs in the media server, so it carries on if Aperture is closed. Opening the same video again shows the remote.
- **Resume and progress sync, without the PC and TV fighting.**
  - While casting, the media server is the only thing that writes the video's position. The desktop player doesn't save while it is a remote.
  - Positions the TV reports are saved only once they are trustworthy: the TV has reached the place asked for, or has played steadily on its own. A jump back that Aperture didn't ask for counts only after a minute of playback. Reports about an older stream are ignored.
  - Progress is saved every 10 s, on pause, on stop and on loss. Reaching the end on the TV marks the video finished.
  - The Remember played videos setting is respected.
  - When casting stops, the desktop player moves to where the TV stopped, paused, and says so.
- **Disconnects.**
  - **TV stops answering:** after 3 missed answers the remote shows *Waiting for…* and says the place is saved. If the TV returns within 90 s, the cast carries on. Otherwise it ends as disconnected, keeping the last real position.
  - **Something else starts playing on the TV:** Aperture ends its cast without stopping the TV.
  - **TV stops on its own:** "Stopped on … Your place is saved."
  - **App loses the server:** after 5 missed status checks it leaves the remote.

  None of these can overwrite the saved position with 0:00 or a stale value.
- **Stopping cleanly.** Stop casting tells the TV to stop (waiting at most 3 s), saves the position, and closes the media address at once, so the TV can't fetch anything more. The player then picks up where the TV left off.
- **Security.**
  - Each cast gets a random 48-character address that serves exactly one file, only to that TV's IP address, only while the cast runs. No path is sent to the TV.
  - Control goes through the media server's loopback control address and its per-start token, like the rest of Settings.
  - TV descriptions are parsed with DTD processing off and size limits. TV names are trimmed to one line.

## 2. Files changed

- **Core** (`src/Aperture.Core/Casting/`):
  - new `CastModels`, `DlnaTime`, `TvStreamPlanner` and `CastPositionTracker`;
  - `Server/StreamPlanner` gained `TransportStream`;
  - `Server/ServerControl` gained the cast control client and JSON options.
- **Server** (`src/Aperture.Server/Casting/`):
  - new `SsdpDiscovery`, `UpnpDescription`, `UpnpRenderer`, `CastSession`, `CastManager` and `CastEndpoints`;
  - `Hosting/MediaServer`, `ServerContext` and `SetupEndpoints` wire it in;
  - `Media/ConversionManager` gained MPEG-TS output to a pipe.
- **App:**
  - `Services/MediaServerHost` gained the cast calls;
  - `Views/PlayerView.xaml` gained the More menu item, the picker panel and the casting view;
  - new `Views/PlayerView.Casting.cs`;
  - `PlayerView.xaml.cs` (no saving while casting, re-attach on open, commands, panels) and `PlayerView.Episodes.cs` updated.
- **Tests:**
  - Core: `Casting/TvStreamPlannerTests`, `CastPositionTrackerTests` and `DlnaTimeTests`;
  - Server: `CastingTests`, `UpnpTests`, and `FakeRenderer`, a DLNA TV on loopback that can be unplugged, refuse seeking, fail originals or be taken over.
- **Tools:** new `tools/visual-review/fake-tv.py`, a stand-in TV using the Python standard library. `Capture-States.ps1` has a Casting step.
- **Docs:**
  - new `docs/casting.md`;
  - new architecture §13;
  - design review rows and components;
  - dependencies, build and test (including how to test against gmrender-resurrect), and README;
  - this report and its screenshots in `docs/reports/milestone-8/`.

## 3. Verification

- **Linux, local** (FFmpeg 6.1):
  - Core 392 passed;
  - Data 43;
  - Media 56;
  - Server 60, including 15 casting tests, two of them with real FFmpeg conversion;
  - YouTube 35;
  - Playback 13 passed, with 33 engine tests skipped here because the engine test media isn't generated locally (CI runs them);
  - the desktop app's C# compile-check passed with no warnings.
- **Windows CI on the final commit:**
  - all test suites pass, with Server 60 of 60 and FFmpeg installed;
  - the x64 and ARM64 builds pass;
  - launch and media-server smoke tests pass;
  - libmpv engine tests pass on Windows and Ubuntu;
  - the visual review passes, including the new Casting step.
- **By hand, against gmrender-resurrect** (`gmediarender` 0.1, libupnp 1.14.18, GStreamer 1.24) over real SSDP multicast:
  - discovery;
  - direct play of an H.264/AAC MP4 with a resume at 30 s;
  - pause, seek to 80 s, play and stop, with the saved position 85 s;
  - play to the end, marked finished;
  - killing the renderer mid-cast showed *Waiting for…*, and restarting it gave "Stopped on Test TV. Your place is saved." with the position intact;
  - another controller taking over the TV ended the cast without a Stop;
  - a second instance limited to MPEG/MP4 got an HEVC/Opus MKV converted to 720p MPEG-TS, and a seek to 40 s started a new stream it played.
- **Visual review on Windows, against the stand-in TV:**
  - the TV was found over real SSDP on the runner;
  - the cast showed *Playing on Living Room TV* at the desktop's position (10:51);
  - Space showed *Paused on Living Room TV*;
  - Stop casting gave "Stopped casting at 10:51. Press Space to carry on here.";
  - the TV's log shows it fetched the file (HTTP 206 with the DLNA headers) and received Pause and Stop.

## 4. Visual review

Screenshots are in `docs/reports/milestone-8/`:

- [95](milestone-8/95-cast-picker-server-off.jpg): the panel when the media server is off.
- [96](milestone-8/96-cast-picker.jpg): the TV found.
- [97](milestone-8/97-casting.jpg): the remote while playing.
- [98](milestone-8/98-cast-stopped.jpg): back in the player after stopping.

They follow the design rules: dark, Lamp as the single accent (only the Start button, the timeline and Search again), no gradients or glass, and the same panel shape as the other player panels.

The remote view is calm and centred on true black where the video was. Stop casting is deliberately a plain button.

One thing was seen and fixed: the first CI run's screenshot step searched again before its first search had finished, which cleared the list. That was a timing issue in the test script, not in the app.

Design review answers for the picker and the remote are in `docs/design-system.md`.

## 5. Known limitations

- **Not yet tried on a real TV.** Brand quirks are likely, such as TVs that ignore seeking, refuse some MIME types or need particular metadata. Aperture handles the common ones (DIDL-Lite metadata, DLNA content-features, relative then absolute seeking, falling back to conversion), but only real TVs will tell.
- **DLNA only.** There is no Chromecast, AirPlay or Roku support, for the reasons above.
- **No subtitles from Aperture on the TV,** and no audio track choice: the TV plays the original file's default track, and a converted stream carries the first one.
- **Converted streams can't seek within themselves.** Moving along the timeline starts a new stream from that point, which takes the TV a few seconds.
- **Without FFmpeg,** Aperture can't look inside a file first. It sends the original whenever the TV lists the format, so a TV may still fail on, say, HEVC in MP4. It then says so.
- **Firewall and network.** The media server must be allowed on Private networks, and routers that block multicast (guest networks, AP isolation) hide TVs.
- **Volume is left to the TV's own remote.** One cast runs at a time.

## 6. Regression check

- All earlier test suites pass unchanged on Linux and Windows.
- The media server's phone features are unaffected: the M7 server tests and the visual review's media server step pass.
- The player's own playback, saving and panels behave as before when not casting. The only changes are a new More menu item and no saving while casting.
- `ConversionManager` changes only when the new MPEG-TS pipe output is asked for.
- No database migration.

## 7. Next step

Please try Play on a TV with your own TV, following [docs/casting.md](../casting.md), and say how it went, including the TV's make and model. Anything it gets wrong on a real TV is the next fix. Milestone 9 (advanced features) is queued after this.
