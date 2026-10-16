# Watching on your phone: Aperture's media server

Aperture's media server lets a phone, tablet or another computer on your home network browse the library folders you choose and play them in its web browser. Nothing is installed on the phone. The server is a separate program, `Aperture.Server.exe`, in the `Server` folder next to `Aperture.exe`. It keeps running after you close Aperture and stops only when you stop it.

It is off until you start it, shares nothing until you choose folders, and nobody can sign in until you set a passphrase.

## Setting it up

1. **Settings › Media server › Shared folders.** Turn on the library folders devices may see. Only those folders' items are listed or played. Devices never see file paths, and can't ask for any file by name.
2. **Passphrase.** Type it twice and Save. Use at least 10 characters; a few unrelated words are easy to type on a phone. Changing it later signs every device out.
3. **Start.** The first time, Windows Defender Firewall asks whether `Aperture.Server` may communicate. **Allow it on Private networks only. Leave Public unticked.** If your home Wi-Fi is set to *Public* in Windows, devices can't connect; change it under Settings › Network & internet › Wi-Fi › *your network* › Network profile type › Private. Aperture never changes firewall rules or network settings itself.
4. **Connect a device.** Settings shows an address such as `http://192.168.1.20:8080/`. Open it in the phone's browser while the phone is on the same Wi-Fi. That page has two steps:
   - **Install the certificate, once.** On iPhone or iPad: tap Allow, then Settings › Profile Downloaded › Install, then Settings › General › About › Certificate Trust Settings and turn on *Aperture Media Server*. On Android: Settings › Security › More security settings › Encryption & credentials › Install a certificate › CA certificate, and choose the downloaded file (names vary between phones). You can check the fingerprint shown on the page against the one in Aperture's Settings.
   - **Open Aperture** at the `https://…:8443` address the page links to, and sign in with the passphrase. Add it to the home screen to open it like an app.

The device stays signed in for 30 days after it was last used. Settings › Media server › Signed-in devices lists each one with when and from where it was last used, and signs any or all of them out at once.

### Why a certificate

Everything between the phone and the server, including the passphrase and the video, is encrypted with HTTPS. A home server has no public name, so no public certificate authority can vouch for it. Aperture makes its own certificate authority on your computer and the phone trusts it.

That authority is limited (RFC 5280 *name constraints*) to private addresses (192.168.x.x, 10.x.x.x, 172.16–31.x.x, 169.254.x.x, 100.64–127.x.x, private IPv6) and to `.local` and single-label names. A certificate checker that follows the standard refuses it for any public website, so even a copied key couldn't impersonate your bank. Aperture's tests check that the server certificate never names a public address, and OpenSSL was seen enforcing the limits; whether a particular phone enforces them was not tested, so treat installing it like trusting this computer. To remove that trust later, delete the profile (iPhone: Settings › General › VPN & Device Management) or the certificate (Android: Encryption & credentials › User credentials).

On Windows the authority's private key is kept in your user's certificate store (`Current User › Personal`), not in a file, and is never sent anywhere. The certificate the server presents is renewed every time it starts and whenever the computer's network addresses change.

## Watching away from home

The server **never** accepts a connection from a public internet address, whatever the settings. Don't forward ports on your router for it; it would refuse those connections anyway.

To watch away from home, use a private network you control, such as [Tailscale](https://tailscale.com/) (free for personal use) or your own WireGuard VPN:

1. Install Tailscale on the computer and on the phone, and sign in to the same account on both.
2. Turn on **Settings › Media server › Allow devices on a private network**. It is off by default.
3. On the phone, with Tailscale connected, open the `100.x.y.z` address Settings now lists, the same way as at home.

Only addresses in Tailscale's ranges (100.64.0.0/10 and fd7a:115c:a1e0::/48) are added by that setting. Use the numeric address: Tailscale's `*.ts.net` names aren't in the certificate. A slow connection gets a smaller stream (see below), which needs FFmpeg.

## Laptops: sleep, lid, power and network

The server can only answer while the computer is awake and connected. Aperture never changes your Windows power settings; this is how each one affects the server.

| Situation | What happens | What you can do |
|---|---|---|
| **Idle sleep** | With **Keep the computer awake while a device plays** on (the default), the server asks Windows not to sleep from idle while a device is playing or has played within the last minute, and lets go when playback stops. Settings shows "Keeping the computer awake" while it does. | Nothing, or turn the setting off to let Windows sleep on its usual timer. |
| **Closing the lid, the power button, Start › Sleep** | The computer sleeps regardless. Playback on the phone stops; its position up to the last 10 seconds is saved. | Windows decides what closing the lid does (Control Panel › Hardware and Sound › Power Options › *Choose what closing the lid does*). Setting "Do nothing" when plugged in keeps it serving with the lid shut; that is your choice to make, and Aperture won't make it. |
| **Waking up** | The server carries on where it was. A phone that was playing shows an error; press play to continue from the saved position. | — |
| **Battery** | Converting uses the processor or graphics chip heavily, and Windows may slow both on battery. Direct play (most files on modern phones) uses almost nothing. | Plug in for long conversions. |
| **Changing networks** | The server keeps running and renews its certificate for the new addresses. The address on the phone changes if the computer's address changes. | Give the computer a fixed address in your router (a "DHCP reservation"), or try `https://computername.local:8443`, which the certificate covers; whether it resolves depends on the phone and network (not tested). |
| **No network** | Settings says the computer isn't on a home network right now, and devices can't reach it. | Reconnect; nothing needs restarting. |
| **A work VPN on the computer** | Some VPNs block or reroute local traffic. | Disconnect it, or ask whether it allows local network access. |

Setting **Start the server when I sign in to Windows** adds one entry to your user's sign-in list (`HKCU\…\Run`, nothing system-wide); turning it off removes it.

## Direct play, repackaging and converting

For each playback the phone tells the server what it can play and briefly measures its connection. The server then picks, in this order:

1. **Direct play**: the original file, byte for byte, when the phone plays its video, sound and container, and the connection can carry its bitrate with room to spare. Seeking is instant.
2. **Repackaging**: H.264 video in a container the phone can't open (MKV, AVI, TS) is moved into a stream without re-encoding; only unplayable sound (DTS, Dolby, FLAC on some phones) is converted to AAC. Cheap.
3. **Converting**: anything else (HEVC on Android, VP9 on iPhone, 10-bit H.264, old codecs), or a connection too slow for the original, is converted to H.264 and AAC at the best of 1080p, 720p, 480p or 360p that fits the connection, never larger than the original.

Phones with HLS (iPhone, iPad, Safari, Chrome on Android) get an HLS stream they can seek within what has been converted so far. Browsers without HLS (Firefox, most desktop browsers) get one continuous MP4. The original file is only read; conversions are written to `%LOCALAPPDATA%\Aperture\Server\Conversions` and deleted when the stream has been unused for a few minutes (15 at most), and whenever the server starts or stops.

The player's **Playback details** shows what was chosen and why: the source ("HEVC 3840×2160, Dolby Digital 5.1, MKV, 18 Mbps"), the stream, the converter and its speed, and the measured connection.

### FFmpeg

Repackaging and converting need FFmpeg, which you install yourself; Aperture doesn't include it because of its size and licence. Without it, files the phone can play still play, and the others say that they need FFmpeg.

- Install it with `winget install Gyan.FFmpeg`, or download a Windows build from [gyan.dev](https://www.gyan.dev/ffmpeg/builds/) or [BtbN](https://github.com/BtbN/FFmpeg-Builds/releases).
- Aperture looks in `Server\ffmpeg\` next to `Aperture.exe`, then on `PATH`. Or press **Choose ffmpeg.exe…** in Settings. `ffprobe.exe` must be in the same folder.
- **Hardware encoding.** When the server starts, it tries a one-frame test encode with NVIDIA NVENC, Intel Quick Sync and AMD AMF, in that order, and uses the first that works; otherwise the processor (x264, slower and hotter on a laptop). Settings shows which one is in use. Graphics drivers must be current for the hardware encoders.
- **Conversions at once** is 1 by default; a second device asking for a conversion is told the server is busy. Repackaging doesn't count against the limit.

## Security design

- **Two ports.** HTTPS on 8443 serves the web app, the library and media. Plain HTTP on 8080 serves only the setup page and the certificate (both public by nature) and two control requests that answer this computer alone and need a random token from `Server\server.json`. Both ports can be changed in Settings › Media server › Ports.
- **Who may connect.** This computer, private home-network addresses and link-local addresses; private-network (Tailscale) addresses only when allowed. Every other address gets 403 before anything else runs.
- **Signing in.** The passphrase is stored only as a salted PBKDF2-SHA256 hash (600,000 iterations) and checked in constant time. After 5 wrong tries an address waits 30 seconds, doubling up to 15 minutes, and the server takes at most 30 tries a minute overall. A device gets a random 256-bit token in an `HttpOnly`, `Secure`, `SameSite=Strict` cookie; the database stores only its SHA-256. Changing the passphrase signs every device out.
- **Every request is checked.** Library requests need a signed-in device. Media addresses carry a random token tied to one device and one item, and every media request checks again that the device is still signed in and the item still shared, so signing a device out or unsharing a folder takes effect immediately.
- **No file browsing.** Devices name items by number. An item is served only if its folder is shared, its path lies inside that folder, its path has no `.` or `..` parts, and the last scan found it. Conversion segment names must match `seg00000.ts`.
- **Browser protections.** A strict Content Security Policy (no inline script or style), `X-Content-Type-Options`, no framing, no referrer, and a custom header required on every changing request so another site can't submit forms to it.
- **Nothing secret in logs.** The server logs to `Logs\server.log` in Aperture's data folder. Request logging is off because media addresses contain tokens; passphrases, tokens and media file paths are never written (FFmpeg's error messages have the path removed).

## The command line

The same controls are available without the app, for example over Remote Desktop:

```
Aperture.Server                  run in this console (Ctrl+C stops it)
Aperture.Server status           is it running, its addresses and converter
Aperture.Server stop             stop the running server
Aperture.Server set-passphrase   set the passphrase (typed hidden, signs every device out)
Aperture.Server folders          list library folders and whether each is shared
Aperture.Server share ID         share a folder (unshare ID to stop)
Aperture.Server devices          list signed-in devices
Aperture.Server sign-out ID|all  sign devices out
Aperture.Server ports 8443 8080  choose the ports for the next start
```

`APERTURE_DATA_DIR` points it at a different data folder, as it does for the app.

## Limits

- Tested with browsers on Linux and Windows and by automated tests, **not yet on a real iPhone or Android phone**.
- Devices on your home network must use its IPv4 or private IPv6 address. A public IPv6 address can't be told apart from the internet, so it is refused even at home.
- Subtitles aren't shown on the phone yet, and only the first sound track plays.
- When repackaging starts part-way into a file, it starts at the nearest keyframe, so it can begin a few seconds early; MKV files without an index start from the beginning.
- A continuous MP4 (browsers without HLS) can't be seeked beyond what has arrived; seeking further starts a new stream from there.
- Progress made on a phone appears in Aperture's Continue watching the next time Home refreshes.
