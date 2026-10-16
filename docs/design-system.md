# Aperture design system

Status: Milestone 1, extended in Milestones 3 and 4 (new states use existing tokens only). Tokens live in [`src/Aperture.App/Theme/Tokens.xaml`](../src/Aperture.App/Theme/Tokens.xaml), component styles in [`Styles.xaml`](../src/Aperture.App/Theme/Styles.xaml), motion durations in [`Motion.cs`](../src/Aperture.App/Theme/Motion.cs). Screens reference token keys only; a raw colour, size or duration in a view is a bug. Change a token here and in the file together.

## 1. Identity in one paragraph

Aperture is a dark, quiet room with one warm light in it. Surfaces are deep charcoal steps, never pure black outside the video itself. Type is Segoe UI Variable, native to Windows 11, with hierarchy carried by weight and colour more than size. There is exactly one accent, **Lamp** (`#F2A93B`), the amber of a projector bulb, and it marks only three things: progress, the primary action, and the current selection. The mark is the same idea drawn as simply as possible: a ring with a point of light at its centre. Icons are Segoe Fluent Icons, so they match Windows instead of competing with it.

## 2. Design review

Answered for each state before it was built, as the project rules require.

| State | 1. Primary task | 2. Most important element | 3. Hidden until needed | 4. Removed | 5–6. Desktop sizes, real content |
|---|---|---|---|---|---|
| **First launch, no media** | Open a first video | The single "Open file" button | Nothing else exists to hide | Feature tours, sample content, marketing copy, a library setup wizard | One centred column, max 440 px, so it reads as a composed page from 640 px to 4K rather than a stretched dashboard |
| **File-open experience** | Choose a video | The Windows file dialog itself (native, filtered to video types, starts in Videos, "Play" as the commit button) | Drop target appears only while dragging | A custom in-app file browser (the system dialog is better and familiar) | Whole window is the drop target, so there is no small zone to aim for |
| **Home / library** | Get back to a video | Continue Watching row, larger cards, with remaining time | Card actions (right-click menu), search results | Library sections with no real data behind them (folder libraries arrive in M6), ratings, badges, hero banners | Continue Watching bleeds to the window edge so a cut-off card signals more; Recently Played is a responsive grid that adds columns as the window widens |
| **Playback, controls visible** | Watch, and occasionally seek or pause | The video | Speed and "Show in folder" live in the More menu | Previous/next (no playlist yet), track menus (M3), a centre play button over the picture | Controls are one 24 px-inset band at the bottom over a scrim, title in the title bar; video keeps 100 % of the window |
| **Playback, controls hidden** | Watch | The video | Everything, including the pointer and caption-button glyphs | Any persistent chrome | Same at every size, including full screen |
| **Mini-player** | Keep watching while working | The video | Volume, time, More and full-screen buttons | All of the above; only play/pause, timeline and "return" remain | Windows compact-overlay window (always on top, user-resizable) |
| **Settings** | Change one preference and leave | The setting rows themselves | Clear-history confirmation (a flyout) | Categories pages, search, toggles without behaviour, theme picker (light theme not designed yet) | One reading-width column (640 px), rows instead of nested cards |
| **Resume or restart (Milestone 3)** | Decide where to start | "Resume" (accent) | The choice only appears when Settings says "Ask each time"; otherwise the remembered default acts silently | A modal dialog over the video | The same small panel as M1's resume notice, bottom left over the paused first frame |
| **Sync and subtitle look panel (M3)** | Nudge subtitles or sound into sync, or make subtitles readable | The two offset steppers with the current value | Opened from More or G; closes on Esc | Free-form millisecond entry, font pickers, per-track styling | Floating panel top right, 340 px, so the picture and the subtitles being adjusted stay visible |
| **Shortcuts panel and editor (M3)** | Find or change a key | The key next to each action | Editing lives in Settings; the player panel only lists | Recording macros, per-screen maps | Player panel is two columns centred over the video; editor is a dialog with grouped rows |
| **Library (M4)** | Find something to watch | The grid of artwork | Scan progress shows only while scanning; corrections live in each card's menu | Ratings, cast, genres, trailers, a sidebar of filters | Kinds as a selector bar with counts, one filter box and a sort; the grid reflows from 1 column at 640 px to many at 4K |
| **Edit details (M4)** | Correct a wrong or doubtful match | The title field | Season and episode appear only for episodes; a suggestion panel only when there is one | Free-text tags, artwork upload | Dialog, fields in reading order |
| **Settings › Media server (M7)** | Let a phone in, safely | Start (accent) and the one-line status beside it | The connect address and fingerprint appear only while the server runs; device rows only once a device signed in; ports at the bottom | A dashboard of graphs, a file browser, a "share everything" switch, a public-access option | Same reading-width rows as the rest of Settings; shared folders and devices are indented rows under their heading |
| **Phone: sign in (M7)** | Get in | The passphrase field | Errors appear under the field only after a try | Account creation, "remember me" (sign-ins last 30 days anyway), social sign-in | One column, max 400 px, works at 320 px |
| **Phone: home and show (M7)** | Pick something to watch | Continue watching, with remaining time | Kinds sit behind a quiet tab row; seasons behind tabs | Search (short libraries scroll faster), ratings, cast | Same 16:9 cards as the desktop; one column of rails on a phone, a grid on a tablet |
| **Phone: player (M7)** | Watch | The video, in the browser's own player | "Playback details" collapsed under the title | Custom controls (the phone's native ones are better and accessible), casting buttons | Video full width; title, resume line, Next episode and details below |
| **Play on a TV picker (M8)** | Send this video to a TV | The list of TVs found, each one a button with its name | Opened from More › Play on a TV; Start media server only when the server is off; Search again only after a search | Brand logos, a permanent Cast button on the bar (Aperture can't tell if a TV is there until it searches), "all TVs" promises, device setup wizards | The same 340 px side panel as the other player panels, top right, over the video |
| **Casting, the remote (M8)** | Control what the TV plays, or stop | The heading "Playing on *TV*" and the timeline | The message line appears only when there is something to say (reconnecting, started from the beginning); in the mini-player only the heading, timeline and buttons remain | A video preview (the TV has the picture), volume (the TV's own remote does that), a second accent | One centred column, max 560 px, on true black where the video was; Stop casting is a plain button, not accent, so it is easy to find but not inviting |
| **Seek preview (M9)** | See where a point on the timeline is before jumping there | The picture, with the time under it | Appears only while the pointer is over the timeline (not for sound files or anything shorter than 10 s); can be turned off in Settings › Playback | Filmstrips, chapter thumbnails along the whole bar, a preview while hovering anywhere else | 192 × 108 view pixels at most (shape follows the video), just above the pointer, kept inside the window at any size |
| **Bookmarks and notes (M9)** | Mark a moment, and come back to it | "Bookmark this moment", then the list of times | B marks without opening anything; the panel opens from More or Shift+B; ticks on the timeline are 2 px | Categories, colours, sharing, exporting, rich text, screenshots of each bookmark | The same 340 px side panel as the other player panels; each row is a time (quiet accent button that jumps), a note field and a delete button |
| **A–B loop (M9)** | Repeat a passage | The chip by the time ("Loop 1:02 – 1:15") | Shown only while a loop is marked or running; the band on the timeline is the same accent | A loop editor, loop counts, saved loops | One quiet chip in the transport row; clicking it stops looping |
| **Sound options (M9)** | Hear dialogue clearly, or stop riding the volume | Two switches: Clearer dialogue, Even volume | In More › Sound and Settings › Sound only; D and E toggle them with a short notice | Equaliser, presets, "AI" labels for ordinary filters, a "night mode" that duplicates Even volume | Menu toggles in the player; standard setting rows in Settings, with a caption saying it runs on this PC |
| **Loading, empty, error, unsupported** | Understand what happened and what to do next | One plain sentence | Technical detail stays small and selectable below the message | Stack traces, error codes as headlines, apologetic copy | Centred, max 460 px; the same composition for every message so they read as one family. From M9 a quiet "Copy details for a bug report" sits under the actions, and "Try again" appears for failures a retry can fix |

## 3. Tokens

### Colour (dark theme, `Default` theme dictionary)

| Token | Value | Use | Contrast on `SurfaceBase` |
|---|---|---|---|
| `SurfaceBase` | `#141517` | Window background | – |
| `SurfaceRaised` | `#1C1D20` | Setting rows, artwork placeholder area | – |
| `SurfaceOverlay` | `#26282C` | Reserved for floating panels | – |
| `SurfaceVideo` | `#000000` | Behind the video only (letterboxing must be true black) | – |
| `SurfaceHover` / `SurfacePressed` | white 6 % / 3 % | Icon-button hover and press fills | – |
| `ArtworkEmpty` | `#202226` | Cards whose file has no thumbnail | – |
| `StrokeSubtle` / `StrokeStrong` / `StrokeHover` | white 8 % / 16 % / 35 % | Hairlines; hover ring on cards | – |
| `TextPrimary` | `#F3F5F7` at 95 % | Titles, primary labels | 15.2 : 1 |
| `TextSecondary` | at 72 % | Body text, resting card titles | 9.1 : 1 |
| `TextTertiary` | at 55 % | Captions, metadata | 5.7 : 1 |
| `TextDisabled` | at 36 % | Disabled controls only (exempt from 4.5 : 1) | 3.2 : 1 |
| `Accent` (Lamp) | `#F2A93B` | Progress, primary button, selection | 9.2 : 1 |
| `AccentHover` / `AccentPressed` | `#F6BC63` / `#D8922A` | Interaction states | – |
| `TextOnAccent` | `#1B1408` | Text on Lamp | 9.1 : 1 on Lamp |
| `StatusCritical` | `#E8806C` | Error glyphs only; error text stays neutral | 6.7 : 1 |
| `VideoTopScrim` / `VideoBottomScrim` | black 60 %→0 / 0→72 % | Legibility of overlays on any picture | – |
| `VideoPanel` | `#1C1D20` at 90 % | Small panels over video (resume notice) | – |

Ratios were computed from the token values with the WCAG 2.1 formula. The accent is also fed into WinUI's `SystemAccentColor*` keys so stock controls (slider, toggle, progress ring, accent button) match.

**High contrast:** every brush has a `HighContrast` counterpart mapped to a Windows system colour, so contrast themes override Aperture's palette completely.

**Light theme:** not designed yet. The app requests the dark theme explicitly rather than shipping an untested light one.

### Typography

Segoe UI Variable Display for titles and section headers, Segoe UI Variable Text for everything else, Segoe UI as fallback.

| Token | Size / line | Weight | Use |
|---|---|---|---|
| `Title` | 28 / 36 | SemiBold | One per screen at most (first launch, Settings) |
| `Subtitle` | 20 / 28 | SemiBold | Section headers on Home, error titles |
| `SectionHeader` | 16 / 22 | SemiBold | Reserved |
| `Body` / `BodyStrong` | 14 / 20 | Regular / SemiBold | Text, labels, card titles |
| `Caption` | 12 / 16 | Regular | Metadata, hints, timecode |

### Space, size, shape

- **Spacing scale (px):** 4, 8, 12, 16, 20, 24, 32, 40, 48, 64. Page gutter 40 (24 below 900 px wide).
- **Title bar:** 48 (32 in the mini-player). **Controls:** 32 high; icon buttons 40 × 40.
- **Artwork:** one proportion, 16:9, everywhere. Continue Watching 320 × 180; grid cards at least 224 wide and stretch to fill columns. Progress bar 3 px, inset 12 px.
- **Radii:** 4 (controls), 6 (artwork, setting rows), 8 (floating panels, drop target). Nothing is pill-shaped.
- **Reading width:** 640 (Settings), 440–460 (empty and error states). **Search:** 320.

### Motion

| Token | Duration | Use |
|---|---|---|
| `Fast` | 120 ms | Hover ring, drop overlay |
| `Medium` | 200 ms | Playback overlay and title bar fade |
| `Slow` | 300 ms | Reserved for screen transitions |

Screens switch instantly in M1. Nothing loops, floats or animates on its own.

## 4. Components

| Component | Where | Notes |
|---|---|---|
| `IconButtonStyle`, `VideoIconButtonStyle` | Title bar, transport | Chromeless; quiet fill on hover; system focus visual |
| `QuietButtonStyle` | "Open", "Start over" | Text button without chrome until hovered |
| `ApertureButtonStyle`, `ApertureAccentButtonStyle` | Dialog-like actions | WinUI buttons with Aperture's radius and height; at most one accent button per view |
| `MediaCard` | Home, Library | 16:9 artwork (poster, Windows thumbnail, or the empty fill), progress, title, one line of context (episode or year, time left or length, quality, number of copies); hover ring; a small "Check details" badge for doubtful matches and a ✓ for watched items; context menu built for the item (Play, Play from the beginning, Play another copy, Mark watched or unwatched, Edit or Check details, Show in folder, Remove from history) |
| `SettingRowStyle` | Settings | Title, one-line description, control on the right |
| Status panel | Player errors, Home empty and no-results | Glyph, title, one sentence, optional detail, at most two actions |
| Aperture mark | Title bar (16 px), first launch (64 px) | Ring plus Lamp dot; drawn with two ellipses, no image asset |
| Skip button | Player, bottom right above the controls | One `ApertureButtonStyle` button: "Skip intro", "Skip recap" or "Skip credits"; after an automatic skip it reads "Watch the intro" for 5 s. Shown only while the segment plays; never accent, so it does not compete with the video |
| Next-episode card | Player, bottom right | A 320 px panel on `VideoPanelBrush` (240 px in the mini-player): caption ("Next episode in 8 s", "Next episode"), the episode's number and name, a thin Lamp progress line only while counting, and two buttons, "Play now" (the one accent button) and "Cancel"/"Close". No artwork, no animation beyond the line. Esc, Cancel, Close or pausing stop the countdown |
| Intro and credits panel | Player side panel (More options › Intro and credits) | Same shape as the info and sync panels: a kind selector, the times in effect and where they came from (found, chapter, your correction), "Starts here" / "Ends here" at the current position, then quiet text buttons: go to the start, "These times are right", "This file has none", "Go back to what was found" |
| `YouTubeCard` | YouTube section | Same shape as `MediaCard`: 16:9 thumbnail from YouTube, length tag, progress for Continue watching, title, channel. Context menu: Play, Play from the beginning, Save or Remove from Saved, Remove from Continue watching, Watch on YouTube, Copy link |
| YouTube player | YouTube player screen | YouTube's own player at 16:9, never covered by Aperture's controls or panels. Below it the title, channel and three quiet buttons (Save, Watch on YouTube, Copy link). From 960 px wide an Up next list with the YouTube autoplay switch sits on the right. When a video can't play, the player is hidden and a status panel explains why, with Watch on YouTube |
| Media server rows | Settings › Media server | Standard setting rows. Start uses the accent button while off and the quiet button (Stop) while on. Shared folders and signed-in devices are indented rows (32 px) with a switch or a Sign out button. The connect address is body text, selectable, with Copy address |
| Phone web client | `src/Aperture.Server/wwwroot` | Plain HTML, CSS and JavaScript using the same tokens as the desktop (`app.css` mirrors `Tokens.xaml`): charcoal steps, Lamp as the only accent, 16:9 artwork with 6 px radius, a 4 px spacing scale and system fonts. Touch targets at least 44 px, safe-area insets respected, no inline styles (the Content Security Policy forbids them) |
| Play on a TV panel | Player, top right (More › Play on a TV) | 340 px panel on `VideoPanelBrush`: heading and close, one status line with a small progress ring while searching, one full-width `ApertureButtonStyle` button per TV (name, model below in caption), Start media server (accent, only when the server is off), Search again (quiet), and a caption on what works and the alternatives (Windows+K, a cable) |
| Casting view | Player, in place of the video | `SurfaceVideo` background; a 96 px ring with the TV glyph, heading (subtitle style, live region), title, message (caption), position and length either side of a slider, back 10 s / play-pause / forward 10 s video icon buttons, Stop casting (`ApertureButtonStyle`), and a caption saying how the file is sent and why |
| Seek preview card | Player, above the timeline | The picture on `SurfaceVideoBrush` with a 1 px `StrokeStrongBrush` hairline and medium radius, and the time below in a small `VideoPanelBrush` tag. Not focusable and hidden from screen readers (the timeline already announces the time) |
| Bookmarks panel | Player, top right (More › Bookmarks and notes) | 340 px panel on `VideoPanelBrush`: heading and close, "Bookmark this moment" (`ApertureButtonStyle`), then rows of time (quiet button in Lamp), note (`TextBox`, saved when focus leaves, Enter returns to the video) and delete. An empty list says which key marks a moment |
| Loop chip | Player transport row, after the time | `QuietButtonStyle` in Lamp: loop glyph, "Loop 1:02 – 1:15" in caption style, a small ✕. Only while a loop is marked |
| YouTube consent | First visit to YouTube | Status-panel layout: what the section is, links to the YouTube Terms and Google Privacy Policy, what Aperture keeps, then "Agree and continue" (accent) and "Not now" |

## 5. Interaction

### Keyboard

| Keys | Action | Where |
|---|---|---|
| Ctrl+O | Open file | Everywhere |
| Ctrl+F | Search (goes Home first) | Everywhere |
| Ctrl+, | Settings | Everywhere |
| Ctrl+L | Library | Everywhere |
| Esc | Close an open player panel; then leave full screen or mini-player; otherwise clear search, or go back | Everywhere |
| Alt+Left, Backspace | Back | Player, Library, Settings |
| Space, K, media Play/Pause key | Play / pause | Player |
| S, media Stop key | Stop: saves the position, closes the file and returns Home | Player |
| Left / Right (Shift for 30 s) | Seek 5 s back / forward | Player |
| J / L | Seek 10 s back / forward | Player |
| Home / End | Start / end | Player |
| Up / Down | Volume ±5 | Player |
| M | Mute | Player |
| F, F11, double-click | Full screen | Player |
| P | Mini-player | Player |
| C | Next subtitle track (wraps through Off) | Player |
| A | Next audio track | Player |
| R | Next picture shape (Original, Fill window, Stretch, 16:9, 4:3, 2.39:1) | Player |
| ] / [ | Faster / slower (0.25, 0.5, 0.75, Normal, 1.25, 1.5, 1.75, 2×) | Player |
| \\ | Normal speed | Player |
| I | Playback info panel | Player |
| , / . | One frame back / forward (pauses) | Player |
| Z / X | Subtitles 0.1 s earlier / later | Player |
| Shift+Z / Shift+X | Sound 0.1 s earlier / later | Player |
| G | Sync and subtitle look panel | Player |
| ? or F1 | Shortcuts panel | Player |
| N | Skip the intro, recap or credits on screen | Player |
| Shift+N, media Next key | Next episode | Player |
| Shift+P, media Previous key | Previous episode | Player |
| B | Bookmark this moment | Player |
| Shift+B | Bookmarks and notes panel | Player |
| Shift+L | Loop: mark the start, then the end, then stop | Player |
| D | Clearer dialogue on or off | Player |
| E | Even volume on or off | Player |
| Mouse wheel over the video | Volume ±5 | Player |

Every player key above is a default: Settings › Keyboard shortcuts changes, adds or removes keys, warns when a key is already used and offers to move it, and refuses keys Windows or Aperture use everywhere. Menus and tooltips show the current key, not the default. Player keys do nothing while a text box has focus, and inside the sync or shortcuts panel only Esc and the panel's own key act.

Space always means play/pause in the player, even when a transport button has focus (Enter activates the focused button). Tab moves through the title bar and then the content in reading order. Focus is drawn with the Windows system focus visual, never a custom ring.

### Pointer

- Hover on cards: a hairline ring on the artwork and the title brightens from secondary to primary. No scaling, no shadow.
- Hover on icon buttons: 6 % white fill.
- Player: click toggles play after a 250 ms wait so a double-click (full screen) does not also toggle play.
- Right-click on a card opens its menu (also Shift+F10 / the context-menu key).

### Playback overlay timing

Controls appear on pointer movement, any playback key, or a status change. While playing they fade out after the idle delay from Settings (2, 3 (default), 5 or 10 s), taking the title bar, the caption-button glyphs and the pointer with them. They stay visible while paused, while the pointer is over the controls, while a menu is open, while scrubbing, and while keyboard focus is inside the controls. Leaving the window while playing hides them at once.

### Window, full screen, scaling

- Opens at 1280 × 800 effective pixels (scaled by the window's DPI), centred, never more than 90 % of the work area. Minimum 640 × 400.
- Full screen uses the Windows full-screen presenter: no title bar, no taskbar, no borders, just the video and the same fading overlay. Esc or F returns to the previous window.
- Mini-player uses the Windows compact-overlay presenter (always on top). Returning restores the previous size and position.
- Layout is in effective pixels and the app manifest declares Per-Monitor V2 DPI awareness, so text and controls are sized by Windows at every scale factor. Home switches to 24 px gutters below 900 px wide; the Recently Played grid reflows by column count.

### Accessibility

- Every icon-only control has an automation name and a tooltip that includes its shortcut; names update with state (Play/Pause, Mute/Unmute, Full screen/Exit full screen).
- Section headers carry heading levels; error titles are assertive live regions; search result counts are polite live regions.
- Cards expose "Title, context" as their name (for example "Night Drive, 6 min left").
- High-contrast themes replace every Aperture colour with system colours.
- Body and caption text meet WCAG AA (4.5 : 1) on their surfaces.

## 6. Still deliberately left out

Light theme; screen transitions; a custom timeline control (WinUI's slider is used, restyled through the accent only); cast, ratings and genres from online metadata (only the title, year, episode title, a short overview and a poster are used).
