# Security review: the media server and casting

Date: 2026-10-15, for release 0.1.0. Scope: `src/Aperture.Server` (the media server process, its phone web app and TV casting) and how the desktop app talks to it. The method was a read of the code against the design in [media-server.md](media-server.md#security-design) and [casting.md](casting.md), plus the automated tests named below. No outside penetration test has been done.

## What the server exposes

| Port | Protocol | Who can reach it | What it answers |
|---|---|---|---|
| 8443 (changeable) | HTTPS, TLS from Aperture's own certificate authority | Every interface, but any address that is not this computer, a private home-network address or a link-local address is refused with 403 before routing. Private-network (CGNAT, e.g. Tailscale) addresses only when "Allow private networks" is on | Sign in, the phone web app, the library of shared folders, media, playback positions |
| 8080 (changeable) | Plain HTTP | Same address rule | The setup page and the authority's public certificate; two control requests that answer only loopback callers holding the token from `Server\server.json`; media for a TV during a cast, on one random path per cast that answers only that TV's address |

The server is a separate process started by the user from Settings or the command line. It is never started automatically at sign-in, and it stops with `Aperture.Server stop`, from Settings, or by the installer before an upgrade or uninstall.

## Controls reviewed

| Area | What the code does | Evidence |
|---|---|---|
| Network reach | `NetworkAccess.Classify` sorts the caller into loopback, local, private-network or public; public is refused for every route. Forwarded-for headers are not read, so a client can't claim another address | `SecurityTests.Public_addresses_are_refused_everything`, `Private_network_addresses_need_the_setting` |
| Passphrase | At least 10 characters. Stored only as a salted PBKDF2-SHA256 hash, 600,000 iterations, compared in constant time | `SecurityTests.Signing_in_needs_the_passphrase_and_sets_a_locked_down_cookie`, `Without_a_passphrase_nobody_can_sign_in` |
| Guessing | 5 wrong tries lock an address for 30 s, doubling to 15 min; at most 30 tries a minute across all addresses | `SecurityTests.Repeated_wrong_passphrases_lock_the_address_out` |
| Sessions | A random 256-bit token per device in an `HttpOnly`, `Secure`, `SameSite=Strict`, `__Host-` cookie; only its SHA-256 is stored. Lasts 30 days. Changing the passphrase or signing a device out ends it at once | `A_made_up_or_signed_out_cookie_is_refused`, `Changing_the_passphrase_signs_devices_out` |
| Cross-site requests | Every changing request must carry a custom header that a form on another site can't send; strict Content Security Policy (no inline script or style), `X-Content-Type-Options: nosniff`, no framing, no referrer | `Changing_requests_need_the_aperture_header`, `Responses_carry_security_headers` |
| File access | Devices name items by number, never by path. An item is served only if its folder is shared, its full path lies inside that folder, it has no `.` or `..` parts and the last scan found it. Conversion segment names must match a fixed pattern | `LibraryAndPlaybackTests.Devices_see_only_shared_folders`, `Made_up_addresses_reach_nothing` (several paths) |
| Media addresses | Carry a random token tied to one device and one item; each request re-checks that the device is signed in and the folder still shared | `Media_addresses_stop_working_when_access_is_withdrawn` |
| Request size | Bodies are capped at 64 KB (Kestrel limit); the speed test is bounded | `The_speed_test_sends_the_amount_asked_for_within_limits` |
| Control from the desktop app | Loopback only, plus a 256-bit token in a file in the user's own profile; compared in constant time; unknown requests get 404 | `ControlAndCertificateTests`, `CastingTests.Cast_control_needs_the_control_token` |
| Certificates | A private authority made on first start, valid 10 years, with X.509 name constraints so it can only vouch for private addresses and `.local`/`.lan` names; on Windows its key lives in the user's certificate store (protected by Windows for that user). The phone compares a SHA-256 fingerprint shown on the PC | `ControlAndCertificateTests.The_authority_may_only_vouch_for_private_names` |
| FFmpeg | Started with an argument list (no shell, no string concatenation), the user's own FFmpeg only, original files opened read-only; outputs go to a folder of Aperture's that is cleaned on start and stop. File paths are removed from FFmpeg errors before they are logged | `ConversionTests` |
| TV discovery (SSDP/UPnP) | Answers are accepted only from home-network addresses and only when their description URL points back at the address that answered (no fetching other hosts). XML is parsed with DTDs prohibited and no resolver (no XXE). Control URLs must stay on the same host | `UpnpTests` |
| Casting media | Served over plain HTTP (DLNA TVs don't do TLS) on a random path per cast, answered only for the chosen TV's address, and withdrawn when the cast ends | `CastingTests.Media_is_served_only_to_the_TV_it_was_made_for` |
| Logging | Request logging is off because media addresses carry tokens. Passphrases, tokens and media file paths are not written. Log files roll over | Code read: `FileLogger`, `ConversionManager` |

## Residual risks, in order of importance

1. **No automatic updates.** The server is built on ASP.NET Core (Kestrel) and .NET 10, which receive security fixes monthly. Aperture bundles the runtime it was built with and has no updater, so a fix only arrives when a newer Aperture is installed. Mitigation for now: rebuild and release when a .NET security update affects Kestrel or TLS. *Recommended next step: a "new version available" check against GitHub releases (opt-in).*
2. **Listening on every interface.** The server binds to all addresses and relies on its own address check to refuse the internet. On a PC with a public IPv4 address directly on an adapter (no router), the TLS port is reachable from the internet and answers 403. Windows Firewall also asks on first start; users should allow **private networks only**. docs/media-server.md says so.
3. **First trust of the certificate on an untrusted network.** The setup page is plain HTTP so a phone can fetch the certificate. Someone already on the same Wi-Fi could swap in their own. The phone shows the fingerprint and the PC shows its own; they must match. A user who skips the comparison is exposed on a hostile network (not a typical home).
4. **Casting is unencrypted on the home network.** DLNA has no TLS. Anyone able to watch the home network's traffic can see the video sent to the TV, and a device that spoofs the TV's address could fetch it while the cast lasts. Accepted: it is how every DLNA sender works; casting is optional and documented.
5. **One shared passphrase.** All devices use the same passphrase; there are no per-person accounts. Devices can be signed out one by one, and changing the passphrase signs everyone out.
6. **Sessions last 30 days.** A stolen phone stays signed in until it is signed out from Settings › Media server (or the passphrase is changed).
7. **The authority's key is exportable by the signed-in user.** Malware running as that user could copy it and impersonate the server to phones that trust it, but only for private addresses (name constraints). Such malware already has the user's files.
8. **Combining GPL libmpv and the Windows App SDK.** Not a security issue but a licence risk noted here for completeness; see [the Milestone 10 report](reports/milestone-10.md#5-known-limitations).

No issue found in this review needed a code change in the server, apart from one usability fix with a security side: Settings › Media server now lists the address of the adapter with a default gateway first (and leaves out Hyper-V, WSL and other virtual switches), so the address a person copies is one their phone can actually reach.

## What was not reviewed

- The phone web app's JavaScript beyond its Content Security Policy and the API it calls.
- Third-party code (ASP.NET Core, .NET's TLS through Windows SChannel, libmpv, FFmpeg). They are kept to current releases at build time.
- A real hostile-network test (ARP spoofing, rogue SSDP flood). The SSDP parser is tested with malformed and off-host answers only.
