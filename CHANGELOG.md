# Changelog

This changelog tracks **public GitHub releases of Listen**. Internal development iterations before the first public release are intentionally not listed.

## 2.0.1

- Media keys (play / pause, next, previous, stop) now reach Listen while another app has the focus on Windows; when Listen is idle they still go to the app that is playing.
- Play queue: a dropped track settles in its new place instead of sliding back first.

## 2.0.0

Listen 2: rewritten in Rust with a native UI, for **Windows, macOS and Linux**.

- One app for Windows 10/11, macOS 11+ (Apple silicon and Intel) and Linux x64, about 5 MB of memory while playing (Listen 1.x: about 39 MB).
- New look: a neutral black, white and silver interface with glass surfaces; each track's artwork softly tints the window.
- New icon and wordmark: a silver disc with a carved sound wave, which flattens while paused.
- Play / pause on a light disc; buttons light up as soft circles; toggles that are on show a glass disc.
- Smooth mode changes: content dissolves, the window morphs, the new layout fades in; the play queue opens as a drawer.
- The progress bar glides continuously; click the duration to show the remaining time.
- Track changes cross-fade the title, artist and artwork.
- Play queue: drag to reorder, total length, the list follows the playing track, tags for titles, exact durations.
- Artist shown under the title (tags or "Artist - Title" file names), never the folder name.
- New shortcuts: `+` add, `-` remove, `Ctrl+A` select all (then clear), `↑` / `↓` volume.
- Media keys and system media panels on all three systems (Windows media flyout, macOS Now Playing, Linux MPRIS).
- Settings and play queue imported from Listen 1.x on first launch.
- Not yet in Listen 2: WMA files, File Explorer integration, taskbar preview buttons, single window when opening files while running.

## 1.1.0

- Smooth, flicker-free transitions between the full player, the mini player and the play queue.
- Expanding the mini player near a screen edge opens upward or sideways so the window stays on screen; collapsing returns to the same spot.
- Resizable mini player, down to just its five buttons.
- Adaptive window: shrinking the player moves continuously through artwork, compact and mini layouts.
- The play queue opens as a drawer below the player instead of squeezing it.
- Previous on the first track and Next on the last track wrap around the queue.
- Keyboard media keys, headset buttons and the Windows media panel control Listen.
- Lower memory use after window transitions.

## 1.0.0 — Initial public release

- Minimal native audio player for Windows 10/11.
- Borderless full player and compact mini player.
- Play/pause, previous/next, seek, volume mute/unmute and repeat modes.
- Persistent play queue with multi-file add, drag & drop, durations and a custom scrollbar.
- Always-on-top support for both full and mini modes.
- Windows taskbar thumbnail controls for Previous, Play/Pause, Next and Repeat.
- Windows File Explorer integration for Open with, Play with Listen and Add to Listen play queue.
- Portable ZIP and per-user Setup build outputs.
- Local-only preferences and queue storage; no account, telemetry or network requirement for playback.
- MIT licensed.
