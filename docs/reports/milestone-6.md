# Milestone 6 report: YouTube

Date: 2026-10-15

**One thing is yours to do, and only if you want search and your account.** Pasted links, the player, Saved and Continue watching work with nothing set up. Search and popular videos need a YouTube Data API key from your own Google Cloud project, and liked videos and playlists also need your own Google OAuth "Desktop app" client. Aperture ships neither, and no secret is in the repository. Setup takes a few minutes and is written down in [docs/youtube.md](../youtube.md). No packages were added.

## What YouTube allows (checked 2026-10-15)

| Interface | Used for | Requirements Aperture follows |
|---|---|---|
| IFrame Player API in WebView2 | All playback | Shown at least 200×200 and 16:9; nothing drawn over the player; ads and controls left alone; an app identity sent as the referrer (Required Minimum Functionality, error 153 otherwise); pauses when the app is hidden |
| oEmbed | Title and channel of a pasted link without a key | No key; 404 means not found, 401 means embedding is off |
| Data API v3, API key | Search, popular, video details and availability | The user's own key; 100 units a search, 1 for details; API data stored at most 30 days |
| Data API v3, OAuth `youtube.readonly` | Liked videos, the user's playlists | The user's own client; loopback redirect with PKCE (Google's method for desktop apps); revoke on sign out |
| Not available to apps | Watch Later and watch history (always returned empty), downloads, stream URLs, ad skipping | Not attempted. A local Saved list and Aperture's own Continue watching stand in, and say so |

## 1. Implemented

- **A YouTube section.** A YouTube button in the title bar opens it. The search box reads "Search YouTube or paste a link" there. A pasted YouTube link plays from any screen, including its start time (`t=1m30s`, `start=`, `#t=`). Watch, youtu.be, Shorts, embed and live links are recognised; anything else is searched.
- **Consent before first use.** The first visit explains what the section is, links the YouTube Terms of Service and Google Privacy Policy, and says what Aperture keeps. Nothing reaches YouTube until "Agree and continue".
- **Search and discovery** (with a key). Search runs on Enter only, shows embeddable videos as cards with length, and has Show more. The home of the section shows Continue watching, Saved, Liked videos and Your playlists (when signed in), and Popular in your region.
- **Playback in YouTube's own player.** WebView2 loads Aperture's own player page from `https://com.aperture.player/`, which hosts the IFrame Player. Below it are the title, channel, Save, Watch on YouTube and Copy link. From 960 px wide an Up next list sits beside it. YouTube's full-screen button makes Aperture's window full screen. Links open in the browser. The video pauses when Aperture is minimised and stops when you leave.
- **Unavailable, restricted and unsupported videos.** Before playing, Aperture checks the video's status (with a key) or oEmbed (without). Not found, private, embedding disabled, age-restricted, blocked in your region, an upcoming premiere, still processing, a player error, and a refused app identity each get their own sentence and Watch on YouTube. The player is hidden in those cases, never covered.
- **Continue watching.** Aperture records the position of YouTube videos you watch in Aperture (every 10 s and on leaving), using the same thresholds as your own videos, and resumes there. Turning off Remember history stops it. Removing an item is in the card's menu.
- **Saved.** Save from the player or a card's menu. This replaces Watch Later, which YouTube doesn't let apps read or change.
- **Account, only when authorised.** Settings › YouTube › Use your own Google sign-in client takes a client ID and secret, then Sign in opens Google in the browser. Access is read-only. The refresh token and the secret go to Windows Credential Manager. An expired sign-in (7 days in Google's Testing mode) shows "Sign in again" instead of failing quietly. Sign out revokes the token.
- **Separate autoplay.** "Play the next YouTube video automatically" is its own setting, off by default, in Settings and in the Up next panel. When on, the next video in the list plays after a 10 s countdown with Play now and Cancel. Episode autoplay is untouched.
- **Clear quota, key and network messages.** Quota used up, key rejected, API not enabled, sign-in expired and offline each say what happened and what to do; pasted links still play in every case.
- **30-day rule.** Details copied from the API are refreshed after 25 days when possible and deleted at 30. Positions and saved dates are Aperture's own records and stay.
- **Delete YouTube data** in Settings removes the YouTube table's rows, clears the player's cookies and cache, and signs out.

## 2. Files changed

- **Core** (`src/Aperture.Core/YouTube/`): `YouTubeLink` (link parsing), `YouTubeVideo`, `YouTubePlayability` (status and player errors to one reason and its wording), `YouTubeEntry`, `YouTubeDataRules` (Continue watching, Saved, 30-day rule), `IYouTubeStore`. `AppSettings` gains `YouTubeAutoplay` and `YouTubeAccepted`.
- **Data**: migration `0004_youtube.sql` (`youtube_videos`), `SqliteYouTubeStore`, two settings keys in `SqliteSettingsStore`.
- **New project `src/Aperture.YouTube`**: `YouTubeDataApi`, `YouTubeOEmbed`, `GoogleOAuth` (PKCE, loopback receiver, refresh, revoke), `YouTubeAccount`, `YouTubeService`, `YouTubeApiException`.
- **App**: `Assets/YouTube/player.html`, new `Views/YouTubeView`, `Views/YouTubePlayerView`, `Views/YouTubeCard`; `MainWindow.xaml(.cs)` (YouTube button, screens, link paste, full screen, Esc and Back), `Services/AppServices.cs`, `Views/SettingsView.xaml(.cs)` (YouTube group), `Aperture.App.csproj`.
- **Tests**: Core `YouTube/YouTubeLinkTests`, `YouTube/YouTubeRulesTests`; Data `SqliteYouTubeStoreTests` and the schema test; new `tests/Aperture.YouTube.Tests` (`YouTubeDataApiTests`, `OEmbedTests`, `GoogleSignInTests`, `YouTubeServiceTests`).
- **CI and tools**: `ci.yml` runs the YouTube tests; `Capture-States.ps1` has a YouTube step.
- **Docs**: new `docs/youtube.md`; architecture §2 and §11, dependencies, design system components, build and test, README, this report.

## 3. Verification

Run locally on Linux (.NET 10):

| Suite | Result |
|---|---|
| Core | 277 passed, 0 failed |
| Data | 35 passed, 0 failed |
| Media | 56 passed, 0 failed |
| YouTube (new) | 35 passed, 0 failed |

What the new tests cover:

- **Links**: every supported link shape, start times in all three forms, other hosts and malformed IDs refused.
- **Data API** against recorded responses: search keeps only embeddable results and reads their details; durations, HTML-encoded titles, age restriction, region lists, live and upcoming; requests in chunks of 50; the key in the query and the token in the `Authorization` header; quota, bad key, API disabled and expired sign-in errors mapped to their own messages.
- **oEmbed**: found, 404 not found, 401 not embeddable, offline.
- **Sign-in** through a real loopback listener: the RFC 7636 PKCE test vector, the authorisation address (scope, S256, offline access), a wrong `state` refused, the browser's refusal reported, stray requests to the port answered 404, the code exchanged and only the refresh token stored; a revoked grant (`invalid_grant`) signs out; changing the client signs out.
- **Service**: lookup falls back from the API to oEmbed; progress written with details on first record; details refreshed after 25 days and deleted at 30 with positions kept; Saved; forgetting; Delete YouTube data clears the store and signs out.
- **Store**: round trips, rows dropped when neither watched nor saved, unknown length keeps the known one.

The app's C# was compile-checked on Linux against the Windows App SDK reference assemblies, with every XAML event handler checked. The XAML, WebView2 and real playback are verified by the Windows CI jobs on this PR. **Not verified:** a real API key and a real OAuth client, since none exists for this project; those paths are tested only against recorded Google responses.

## 4. Visual review

The visual-review job's new YouTube step drives the real app with no key configured:

- The consent screen; shot `80-youtube-consent`.
- After agreeing, the section with the "add a key to search" notice; shot `81-youtube-no-key`.
- A pasted link (*Me at the zoo*, `jNQXAC9IVRw`) plays in the embedded player, confirmed from the player's own "playing" state in the log, with nothing of Aperture's showing over it; shot `82-youtube-player`. Save is pressed.
- After a restart, the Saved shelf is there and the consent is not asked again; shot `83-youtube-saved`.
- A link to a video ID that doesn't exist shows the explanation instead of a blank player; shot `84-youtube-unavailable`.
- Settings › YouTube; shot `85-settings-youtube`.

Design review answers: the section reuses the Home layout, card shape, shelves and status panel, so it reads as part of Aperture rather than as a web page. YouTube's red is not used anywhere in Aperture's own interface; the player's own branding is left as YouTube draws it. The one accent button per view is "Agree and continue" on the consent screen and "Play now" on the countdown. Nothing overlays the player, which is both YouTube's rule and the calmer choice. The results of the CI run are recorded on the PR.

## 5. Known limitations

- **Search and account features need your own Google project.** Google doesn't allow sharing one key or client publicly without its audit, so each user sets up their own. Pasted links work without it.
- **10,000 units a day** by default, so about 95 searches.
- **Sign-in lasts 7 days** while your OAuth consent screen is in Testing mode. Publishing it for `youtube.readonly` needs Google's verification.
- **No Watch Later or watch history**: YouTube's API doesn't give apps either. Saved and Continue watching cover what Aperture can see. A subscriptions feed was left out; it would need one request per channel.
- **Continue watching only knows what you watched in Aperture.** By the same rule as your own files, a video shorter than 30 seconds is never "in progress".
- **Age-restricted videos** can't play embedded at all; they open on YouTube.
- **WebView2** must be present (it ships with Windows 11). If it's missing, the YouTube player says so and nothing else is affected.
- **Region** comes from Windows' region setting, used for Popular and for region checks.

## 6. Regression check

- All earlier tests pass unchanged (Core, Data, Media); the libmpv engine tests run in CI. The engine, the video presenter and the library were not changed.
- The local player has no YouTube dependency: YouTube is in its own project and its own two screens, and its failures stay there. Opening a local file closes the YouTube player first.
- Migration 4 only adds a table; the Milestone 1 backup rule applies.
- The search box on Home and Library still filters as you type. Only pressing Enter with a YouTube link opens YouTube; other text keeps filtering.
- Esc, Back and Ctrl+F gain the YouTube screens; their behaviour on earlier screens is unchanged. Episode autoplay and its setting are separate from YouTube autoplay.
- The earlier visual review steps run unchanged before the YouTube step.

## 7. Next step

Milestone 7 (media server), which is already queued. If you'd like search here, create the API key described in [docs/youtube.md](../youtube.md) and paste it into Settings › YouTube.
