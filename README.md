<div align="center">

# Prompt

**A free, minimalist teleprompter that keeps the script — not the interface — at the center of the experience.**

No account. No backend. No build step.

[**Open Prompt →**](https://prompt.bemorin.com)

[Website](https://prompt.bemorin.com) · [Bemorin](https://bemorin.com) · [MIT License](LICENSE)

</div>

![Prompt teleprompter reading view](screenshots/reader.webp)

## Why Prompt?

Most teleprompters add controls, chrome, setup, or distractions around the one thing that matters: the words you need to read.

Prompt is designed to disappear while you use it.

- **Smooth playback** with adjustable speed
- **Manual positioning** with mouse wheel, trackpad, or `↑` / `↓`
- **Visual Markdown editor** with portable Markdown source underneath
- **Bold, italic, underline, headings, quotes, lists, separators, and text colors**
- **Search** with `Ctrl+F` / `Cmd+F`
- **Click-to-edit** from the teleprompter directly to the corresponding script line
- **Dark and Light modes**
- **Mirror mode** for beam-splitter teleprompter glass
- **Optional reading guide**
- **Optional auto-hide while playing**
- **Fullscreen mode**
- **Local persistence** for your script and preferences
- **Multilingual interface**
- **No framework or runtime dependency**

## Try it

The easiest way to use Prompt is the hosted version:

### [prompt.bemorin.com →](https://prompt.bemorin.com)

No installation or account is required.

Prompt can also run entirely from the repository. Download or clone it and open `index.html` in a modern browser.

## Screenshots

| Visual Markdown editor | Settings |
| --- | --- |
| ![Prompt visual Markdown editor with formatting and text colors](screenshots/markdown-editor.webp) | ![Prompt Settings with theme, font and reading controls](screenshots/settings.webp) |

| Search | Light mode |
| --- | --- |
| ![Prompt in-app script search](screenshots/search.webp) | ![Prompt in Light mode](screenshots/light-mode.webp) |

## Quick start

### Use Prompt online

Open [prompt.bemorin.com](https://prompt.bemorin.com), add your script, and press **Play**.

### Run locally

No build step is required.

1. Download or clone this repository.
2. Open `index.html` in a modern browser.
3. Open **Script**.
4. Paste or write your text.
5. Press **Done**.
6. Press **Space** or the Play button.

That's it.

### Optional local server

Opening `index.html` directly is enough for normal use and development.

If you prefer a localhost environment, use any static server you already have. For example:

**Windows**

```bash
py -m http.server 8080
```

**macOS / Linux**

```bash
python3 -m http.server 8080
```

Then open `http://localhost:8080`.

Python is optional and is not required by Prompt.

## Markdown

Prompt stores scripts as Markdown while presenting them through a lightweight visual editor.

Markdown markers stay out of the way during normal editing, while the underlying script remains portable plain text.

Supported formatting includes:

- `#`, `##`, `###` headings
- **bold**
- *italic*
- underline
- quotes
- unordered lists
- numbered lists
- strikethrough
- existing Markdown code
- existing Markdown links
- horizontal separators
- restrained semantic text colors

Combined formatting is supported without exposing stray Markdown markers in the visual editor.

Bold, Italic, and Underline behave as deterministic toggles, so repeated formatting changes do not accumulate extra `*` or `+` markers.

A standalone `***` stays editable while you type, allowing `***bold italic***`. Pressing **Enter** while the line contains only `***` commits it as a separator.

Numbered lists continue naturally from `1.` to `2.`, `3.`, and so on. Pressing Enter on an empty numbered item exits the list.

Editing or formatting a script preserves the current teleprompter position instead of jumping back to the beginning.

### Text colors

Prompt includes six restrained choices:

**Default · Copper · Sage · Sky · Rose · Violet**

Colors adapt between Dark and Light modes for legibility.

Applying a color to a larger selection replaces older colors inside that selection rather than nesting them. **Default** removes text color while preserving other formatting.

## Privacy

Prompt is local-first.

Your script and interface preferences are stored in the browser's `localStorage` so they can persist between sessions in the same browser profile.

Your script is **not sent to a Prompt/Bemorin backend**.

When internet access is available, Prompt may request webfont files for Inter, Outfit, and Playfair Display from Google Fonts. Those requests do not contain your teleprompter script.

System, Times New Roman, and Mono rely on locally available fonts.

Clearing browser or site storage may remove saved scripts and preferences.

## Languages

Prompt automatically detects the browser language.

Currently supported:

- English
- Français
- Español
- 日本語
- 简体中文
- Deutsch
- Italiano
- Português
- 한국어

Unsupported languages fall back to English.

User scripts are never automatically translated.

## Keyboard shortcuts

| Shortcut | Action |
| --- | --- |
| `Space` | Play / pause |
| `↑` | Move one line up |
| `↓` | Move one line down |
| `←` | Slower |
| `→` | Faster |
| `+` / `-` | Increase / decrease text size |
| `Home` | Restart |
| `E` | Open Script |
| `Ctrl+F` / `Cmd+F` | Search script |
| `F` | Toggle fullscreen |
| `M` | Toggle mirror mode |
| `Esc` | Close open controls |

## Contributing

Contributions are welcome.

Good contributions keep Prompt focused on what makes it useful: a fast, quiet, local-first teleprompter with as little interface as possible.

You can help by:

- reporting bugs
- improving accessibility
- improving browser compatibility
- refining translations
- adding or correcting language support
- improving documentation
- proposing focused UX improvements
- submitting small, well-scoped pull requests

When contributing, please keep these principles in mind:

- **Minimal by default**
- **The script stays visually dominant**
- **No unnecessary dependencies**
- **Fast startup**
- **Local-first behavior**
- **Accessible keyboard and pointer interactions**
- **Focused changes over feature sprawl**

For local development, edit `index.html` and open it directly in your browser. A local HTTP server is optional.

## Support Prompt

Prompt is free and open source.

If it is useful to you, you can help the project by:

- ⭐ starring the repository
- sharing Prompt with people who may find it useful
- contributing fixes, translations, or improvements
- following future releases
- discovering more minimal tools at [Bemorin.com](https://bemorin.com)

A sponsorship option may be added as the project grows.

## About

Prompt is created by **Ben Morin / Bemorin**.

[Prompt](https://prompt.bemorin.com) · [Bemorin.com](https://bemorin.com)

## License

Prompt is released under the [MIT License](LICENSE).

Copyright © 2026 Bemorin.
