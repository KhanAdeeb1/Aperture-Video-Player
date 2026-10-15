# Aperture design system

Status: Milestone 1. Tokens live in [`src/Aperture.App/Theme/Tokens.xaml`](../src/Aperture.App/Theme/Tokens.xaml), component styles in [`Styles.xaml`](../src/Aperture.App/Theme/Styles.xaml), motion durations in [`Motion.cs`](../src/Aperture.App/Theme/Motion.cs). Screens reference token keys only; a raw colour, size or duration in a view is a bug. Change a token here and in the file together.

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
| **Loading, empty, error, unsupported** | Understand what happened and what to do next | One plain sentence | Technical detail stays small and selectable below the message | Stack traces, error codes as headlines, apologetic copy | Centred, max 460 px; the same composition for every message so they read as one family |

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
| `MediaCard` | Home | 16:9 artwork, progress, title, one line of context; hover ring; context menu (Play, Show in folder, Remove from history) |
| `SettingRowStyle` | Settings | Title, one-line description, control on the right |
| Status panel | Player errors, Home empty and no-results | Glyph, title, one sentence, optional detail, at most two actions |
| Aperture mark | Title bar (16 px), first launch (64 px) | Ring plus Lamp dot; drawn with two ellipses, no image asset |

## 5. Interaction

### Keyboard

| Keys | Action | Where |
|---|---|---|
| Ctrl+O | Open file | Everywhere |
| Ctrl+F | Search (goes Home first) | Everywhere |
| Ctrl+, | Settings | Everywhere |
| Esc | Leave full screen or mini-player; otherwise clear search, or leave Settings | Everywhere |
| Alt+Left, Backspace | Back | Player, Settings |
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
| ] / [ | Faster / slower (0.25× to 2×) | Player |
| \\ | Normal speed | Player |
| I | Playback info panel | Player |

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

## 6. Deliberately not in Milestone 1

Light theme; library sections backed by folder scanning (M6); poster art from metadata (M6–M7); track menus and subtitles (M3); screen transitions; a custom timeline control (WinUI's slider is used, restyled through the accent only).
