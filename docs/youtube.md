# YouTube in Aperture

Aperture has a YouTube section for finding and watching YouTube videos next to your own files. It uses only YouTube's official interfaces. Videos play in YouTube's own embedded player, with YouTube's ads, controls and restrictions intact. Aperture never extracts stream addresses, downloads videos, skips ads or reads anything from your account that Google hasn't let it read.

Facts about YouTube's interfaces below were checked against Google's documentation on 2026-10-15.

## What works, and what each part needs

| Feature | Needs | How it works |
|---|---|---|
| Play a pasted link (`youtube.com/watch`, `youtu.be`, Shorts, embed and live links, with a start time) | Nothing | YouTube IFrame Player in WebView2. Title and channel come from YouTube's oEmbed endpoint, which needs no key |
| Continue watching, and a local Saved list | Nothing | Kept by Aperture in its own database (see "What Aperture keeps") |
| Search, popular videos for your region, details and availability before playing | Your own **YouTube Data API key** | YouTube Data API v3 (`search.list`, `videos.list`, `chart=mostPopular`) |
| Your liked videos and your playlists | The API key **and** signing in with your own **Google OAuth client** | `videos.list?myRating=like`, `playlists.list?mine=true`, `playlistItems.list`, with the read-only scope `youtube.readonly` |

Not available, because YouTube does not offer them to apps:

- **Watch Later and watch history.** The Data API returns these lists empty for every account. Aperture's local **Saved** list is offered instead, and **Continue watching** only knows about videos you watched in Aperture.
- **Downloads, background audio, picture-in-picture outside YouTube's player, ad skipping.** Not permitted by the YouTube API Services Terms and not attempted.
- **Videos the owner doesn't allow to be embedded, age-restricted videos, private videos and videos blocked in your region.** Aperture says why the video can't play here and offers **Watch on YouTube**, which opens it in your browser.

Your own videos never depend on YouTube. Without a network, a key or WebView2, only the YouTube section is affected.

## Setting up an API key (search and popular videos)

1. Open the [Google Cloud console](https://console.cloud.google.com/) and create a project (or pick one).
2. Under **APIs & Services › Library**, enable **YouTube Data API v3**.
3. Under **APIs & Services › Credentials**, choose **Create credentials › API key**.
4. Recommended: edit the key, and under **API restrictions** restrict it to **YouTube Data API v3**.
5. In Aperture, open **Settings › YouTube**, paste the key into **API key** and choose **Save**.

The key is stored in Windows Credential Manager (as `Aperture/YouTube`), never in Aperture's database, settings or logs. **Remove** deletes it.

### Quota

A new project gets 10,000 units a day. Aperture spends them as follows:

| Action | Cost |
|---|---|
| A search (one page of results) | 100 + 1 for the details of those results |
| Popular videos for your region | 1 |
| Details for up to 50 videos | 1 |
| Liked videos, your playlists, a playlist's videos | 1 each page |

So about 95 searches a day. Search runs only when you press Enter, never while you type, and lists are kept for the rest of the session. When the quota runs out, Aperture says so and pasted links still play.

## Signing in (liked videos and playlists)

Google requires each app that reads account data to use its own OAuth client. Aperture ships none, so you create one for yourself:

1. In the same Google Cloud project, open **APIs & Services › OAuth consent screen** (now called **Google Auth Platform**). Choose **External**, fill in the app name and your email, and save.
2. Under **Data access** (or **Scopes**), add `https://www.googleapis.com/auth/youtube.readonly`.
3. Under **Audience** (or **Test users**), add the Google account you'll sign in with.
4. Under **Clients** (or **Credentials › Create credentials › OAuth client ID**), choose application type **Desktop app**. Copy the **Client ID** and **Client secret**.
5. In Aperture, open **Settings › YouTube › Use your own Google sign-in client**, paste both and choose **Save**, then **Sign in**.

Your browser opens Google's sign-in page. After you allow access, Google sends the browser back to a one-time address on your own computer (`http://127.0.0.1:<random port>/`), where Aperture is listening for that single answer. The exchange uses PKCE and a random `state`, so another program can't use the answer. Aperture asks only for read-only access.

The refresh token and client secret are stored in Windows Credential Manager. **Sign out** revokes the token with Google and deletes it. Changing the client ID signs you out.

**Testing mode:** while your consent screen's publishing status is **Testing**, Google expires the sign-in after 7 days. Aperture then shows "Sign in again". Publishing the app (which for the `youtube.readonly` scope means Google's verification) removes the limit; for personal use, signing in again each week is the simpler choice.

## What Aperture keeps

In its database (`youtube_videos`), per video you've watched or saved:

- The video ID, your position and the length, and when you last watched it. These are Aperture's own records of your activity.
- When you saved it, if you did.
- A copy of the title, channel, thumbnail address and length from YouTube, with the date they were fetched.

YouTube's Developer Policies allow storing API data for at most 30 days. Aperture refreshes the copied details after 25 days when it can (a key is set and the network is up), and deletes them at 30 days if it couldn't. The entry then shows "YouTube video" with YouTube's default thumbnail until it is refreshed.

**Settings › YouTube › Delete YouTube data** removes all of it, clears the embedded player's cookies and cache, and signs you out. Turning off **Remember history** stops recording positions for YouTube too.

The embedded player stores YouTube's own cookies in Aperture's data folder (`WebView2`), separate from your browser. It is never signed in to your Google account.

## Before first use

The first time you open the YouTube section, Aperture shows what it is, links to the [YouTube Terms of Service](https://www.youtube.com/t/terms) and the [Google Privacy Policy](https://policies.google.com/privacy), and explains what it keeps. Nothing is sent to YouTube until you choose **Agree and continue**. **Not now** goes back.

## How the player is embedded

- The player page is an Aperture file served to WebView2 under the address `https://com.aperture.player/`. YouTube's Required Minimum Functionality asks embedded players to identify the app this way (it sends the `Referer` header); without it YouTube refuses with error 153.
- Nothing is drawn over the player. When a video can't play, the player is hidden and the explanation takes its place.
- The video pauses when Aperture is minimised and stops when you leave the player.
- Links inside the player open in your browser. YouTube's full-screen button makes Aperture's window full screen; Esc leaves it.
- WebView2 comes with Windows 11. If it is missing or broken, the player says so; your own videos are unaffected.

## Autoplay

YouTube autoplay is its own setting (**Settings › YouTube › Play the next video automatically**, also in the player's Up next panel), off by default and separate from autoplay for TV episodes. When on, the next video in the list you started from plays after a 10-second countdown you can cancel.

## Terms

Using YouTube in Aperture means agreeing to the [YouTube Terms of Service](https://www.youtube.com/t/terms). Aperture's use of the API follows the [YouTube API Services Terms of Service](https://developers.google.com/youtube/terms/api-services-terms-of-service) and [Developer Policies](https://developers.google.com/youtube/terms/developer-policies). If you distribute Aperture with your own key or OAuth client, Google's audit and quota rules for that project apply to you.
