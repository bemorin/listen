# Listen, cross-platform

A port of Listen to **Windows, macOS and Linux**, written in Rust with a [Slint](https://slint.dev) UI.
It keeps the look and behaviour of the Windows app (`../src`, WPF), and refines the interaction and motion design.

The WPF app stays the reference implementation and is untouched. This folder is self-contained.

## Stack decision

The goal was the lightest possible footprint (binary size and memory) on three operating systems, with a fully custom UI.

| Option | Why not, or why |
| --- | --- |
| Tauri, Electron, Wails | A webview process tree: 30–100 MB of RAM before any UI. |
| Flutter, Avalonia, .NET MAUI | A runtime plus a GPU context: 40–80 MB at idle, 20–60 MB binaries. |
| egui, iced (wgpu/glow) | A GPU context, so the same ~60 MB of driver memory as below, plus continuous repaints. |
| Native toolkits per OS (WinUI, AppKit, GTK) | Three UIs to write and keep identical. |
| **Rust + Slint, software renderer** | **Chosen:** native code, custom UI, no GPU context, no runtime. |

The deciding measurement was a hello window (`bench/hello`, same size and colours as Listen) built with Slint's two desktop renderers. It ran on Windows 11 x64 and was measured as *private working set*, the "Memory" column of Task Manager (`bench/measure.ps1`):

| Hello window | Binary | Memory at idle |
| --- | --- | --- |
| Slint, OpenGL renderer (femtovg) | 6.9 MB | **62.6 MB** |
| Slint, software renderer | 6.9 MB | **4.0 MB** |

The OpenGL driver alone costs about 58 MB. Listen's window is small (500×410 logical px) and mostly static, so drawing it on the CPU is cheap. The software renderer is therefore the right trade-off.

The rest of the stack:

- **Decoding:** [symphonia](https://github.com/pdeljanov/Symphonia), in pure Rust, through [rodio](https://github.com/RustAudio/rodio) (cpal output). The same decoders run on every OS, with no system codecs.
- **Media keys and OS media panel:** [souvlaki](https://github.com/Sinono3/souvlaki). It uses System Media Transport Controls on Windows, MPRIS (D-Bus) on Linux, and Now Playing on macOS.
- **File dialog:** [rfd](https://github.com/PolyMeilex/rfd) on Windows and macOS (native dialogs). On Linux it shells out to `zenity` or `kdialog`, so Listen links no GUI toolkit.

## Measured footprint (Windows 11 x64, release build)

All numbers are private working set (Task Manager → Memory), measured with `bench/measure.ps1`.

| | Windows app (WPF, .NET Framework) | Cross-platform app (this folder) |
| --- | --- | --- |
| Binary | ~100–130 KB (needs .NET Framework, which ships with Windows) | 9.2 MB, self-contained (no runtime) |
| Memory at start | ~35 MB | **5.7 MB** |
| Memory while playing | – | **6.4 MB** |
| Memory after use (queue, mini/full switches) | ~55 MB | **6.5 MB** |
| CPU while playing | – | ~1–2 % of one core, including decoding |
| CPU while paused | – | ~0.3 % of one core; the audio device is released after 20 s |

The binary is larger because nothing is borrowed from the OS. The UI toolkit, font shaping, the decoders and image decoding are all inside the executable.

## Build

Requires a stable Rust toolchain (1.85 or newer).

```sh
cd cross
cargo build --release
```

The binary is `target/release/listen` (`listen.exe` on Windows).

- **Windows:** nothing else needed (MSVC toolchain).
- **macOS:** Xcode command-line tools. CI also wraps the binary in `Listen.app` (see `.github/workflows/cross.yml`).
- **Linux (Debian/Ubuntu):** install these packages first.

  ```sh
  sudo apt install pkg-config libasound2-dev libdbus-1-dev libfontconfig1-dev libxkbcommon-dev libwayland-dev
  ```

  At runtime, the "Add files" dialog uses `zenity` (GNOME) or `kdialog` (KDE).

Useful during development:

```sh
cargo test                      # unit tests (names, placement, persistence)
LISTEN_DATA_DIR=/tmp/listen cargo run -- song.flac other.mp3
```

`LISTEN_DATA_DIR` keeps test state away from your real settings.

GitHub Actions (`.github/workflows/cross.yml`) builds and tests Windows x64, macOS arm64, macOS x64 and Linux x64. It uploads one artifact per platform.

## Usage

The command line is the same as the Windows app's: `listen a.mp3 b.flac` replaces the queue and plays the first file. `listen --enqueue a.mp3` adds files to the queue.

| Shortcut | Action |
| --- | --- |
| `Space` | Play / pause |
| `←` / `→` | Seek −5 s / +5 s |
| `↑` / `↓` | Volume ±5 % |
| `Ctrl+O` | Add audio files |
| `Ctrl+L` | Show / hide play queue |
| `Ctrl+M` | Mini player |
| `Ctrl+T` | Always on top |
| `Ctrl+R` | Cycle repeat mode |
| `Ctrl+Q` / `Ctrl+W` | Quit |
| `Delete` / `Enter` | Remove / play the selected queue item |
| `Esc` | Leave the mini player |

On macOS, `Ctrl` is `⌘`.

State is stored in `%APPDATA%\Listen` (Windows), `~/Library/Application Support/Listen` (macOS) or `~/.config/Listen` (Linux). The files are `cross-settings.ini` and `cross-queue.txt`, in the same formats as the Windows app. On first launch, the Windows app's `settings.ini` and `queue.txt` are imported; they are never written to.

## What carries over from the Windows app

- The same tokens: colours, sizes and spacing from `UiTokens.cs`, and the same glyph geometry.
- The same layouts: full player, play queue drawer, mini player (title, five buttons, thin progress bar), and the "more" menu.
- **The adaptive window.** Every layout change is a function of the height. Shrinking the window shrinks and fades the artwork, then fades the centred title out before the title-bar title fades in. Below the compact height, the window becomes the mini player. Pulled taller from the mini player, it becomes the full player again.
- **The mini player** is resizable on every edge, down to just its five buttons. Its title hides once it no longer fits.
- **Playback:** play/pause, seek, previous/next with wrap-around on manual skips, repeat off/queue/track, and volume with mute that restores the last level.
- **The persisted queue and settings:** volume, always on top, mini mode, queue visibility, mini width and repeat mode.
- **Always on top.** The mini player is always on top, as in the Windows app.
- **Drag and drop** of audio files onto the window.
- **Media keys,** headset buttons and the OS media panel, with title, artist, album, artwork, duration and position.
- **Window placement rules** (`platform::place`, ported from `WindowMotion.Place`). A morph keeps the top-left corner by default. An edge docked to the screen edge stays docked. Near the bottom or right edge, expanding grows upward or leftward. Collapsing returns to exactly where the window came from.

## Smooth transitions

The Windows app's switches between mini, full and queue are animated here too, without flicker.

Because every layout is a function of the window height, a mode switch is just the native window bounds animating. They follow the same critically damped spring as the Windows app (`WindowMorph.Ease`, 420 ms for a mode change, 340 ms for the queue). A timer steps the bounds about 140 times per second. Slint lays the window out again on each step, so the artwork, titles and controls play their transition continuously, exactly as they do during a manual resize. The queue opens as a drawer: the player stays still while the panel is revealed below it.

On Windows, each step is a single `SetWindowPos` (move and resize in one call). No black or stretched frames showed up in frame-by-frame captures. On macOS and Linux, winit moves and resizes in two calls. A morph that grows upward near the bottom of the screen may wobble by a pixel there. On Wayland, windows cannot position themselves, so they grow from their top-left corner.

## Design refinements in this version

Apple-grade polish, without new weight:

- **Artwork.** Embedded cover art is read from MP3, FLAC and M4A tags, or from `cover.jpg` / `folder.jpg` next to the file. It sits on a soft halo tinted with the artwork's own colour, and rests 10 % smaller while paused, with a spring.
- **Transport.** The play and pause glyphs cross-fade with a slight scale. Skip glyphs nudge in their direction when pressed. Hover and press states fade in about 120 ms.
- **Seek bar.** It thickens under the pointer, and its thumb grows. A time bubble follows the pointer. Scrubbing previews the time, and the seek happens on release.
- **Volume.** The icon reflects the level (muted, low, high). The mouse wheel works over the volume slider, and `↑`/`↓` change it. The slider uses a perceptual curve, so it sounds even across its travel.
- **Queue.** Titles come from tags, and durations are computed in the background (exactly, even for VBR MP3s without a header). Animated bars mark the playing item. Hovering a row reveals a remove button. Remove and Clear are disabled when they would do nothing.
- **Names.** "03 - Song.flac" is no longer shown with "03" as the artist; tags win when present.
- **Resting heights.** Released between two layouts after a resize, the window settles on the nearer one (mini, compact, title only, artwork), so it never rests on a half-faded state.
- **Menu.** The "more" menu shows keyboard shortcuts. Its rows compress to fit a short window.
- **Tooltips** use one shared, delayed tooltip with a fade. They flip below the control when there is no room above.
- **Lightness by design.** The audio device opens on Play and closes after 20 s of pause. Redraws happen only when something visibly changes: the playing clock repaints once per second, or when the progress bar moves a whole pixel. Nothing loops while idle.

## Feature gaps and differences

- **WMA is not supported.** No open-source WMA decoder exists in pure Rust (symphonia has none). Supporting WMA would mean Windows-only Media Foundation code, or bundling FFmpeg (tens of MB). `.wma` files are ignored when added.
- **Windows shell integration is not ported:** "Open with", Default Apps registration, "Play with Listen" and "Add to Listen play queue" verbs, and the installer and portable packaging. The command line accepts the same files and `--enqueue`.
- **Single instance:** opening files while Listen runs starts a second window. The Windows app forwards the files to the running instance.
- **Windows taskbar thumbnail buttons** (Previous, Play/Pause, Next, Repeat) are not ported. The OS media panel covers the same controls.
- **macOS:** opening files from Finder ("Open with") arrives as an Apple Event, which winit 0.30 does not expose. Drag and drop and the command line work. The window uses a hidden native title bar, so it keeps macOS's rounded corners and shadow.
- **Linux:** the work area is the whole monitor (no panel detection), and window corners are square.
- **Drop zones:** the Windows app tells the queue and the artwork apart under the pointer. Here a drop anywhere adds to the queue, and plays immediately when nothing was loaded. Not every platform reports the pointer position during an OS drag.
- **Menus and tooltips** are drawn inside the window, as Slint popups. The Windows app's tooltips could extend past its edges.
- **Accessibility:** Slint's screen-reader bridge (AccessKit) is off to keep memory down. Build with `--features accessibility` to turn it on.
- **Testing:** this version was run and tested on Windows 11. macOS and Linux are built and unit-tested in CI, but not yet exercised by hand.

## Layout of this folder

```text
cross/
  Cargo.toml, build.rs   crate and build script (Slint compile, Windows icon/manifest)
  ui/theme.slint         tokens (UiTokens.cs), motion curves, language, tooltip state
  ui/controls.slint      glyphs, buttons, sliders
  ui/app.slint           the window: full player, queue drawer, mini player, menu, tooltip
  src/main.rs            app controller: queue, playback, window modes and morphs
  src/audio.rs           playback engine (rodio/symphonia), lazy output device
  src/meta.rs            tags, durations, artwork, names
  src/state.rs           persisted settings and queue
  src/platform.rs        work area, atomic move+resize, placement rules, Windows corners
  src/media.rs           media keys and OS media panel (souvlaki)
  src/dialogs.rs         "Add audio files" dialog
  bench/                 hello-window comparison and the memory measurement script
```
