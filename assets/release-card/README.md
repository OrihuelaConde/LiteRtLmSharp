# Release announcement card

The image posted with a release announcement: a 1600×900 PNG, the 16:9 size X shows in the timeline.
`1.3.0.png` is the one published for 1.3.0.

| File | What it is |
|---|---|
| `card-list.html` | Headline, two highlight cards and a feature list. Used for 1.3.0. |
| `card-code.html` | Variant with a code snippet instead of the list. |
| `base.css` | Shared look, taken from `docs/images/social-preview.png`: dark background, purple bar, lavender accents. |
| `render.sh` | Renders a card on Linux with Playwright's Chromium and Pillow. |

## Make the card for a new release

1. Edit the text in `card-list.html`: the version in the kicker, the headline, the subtitle, the two cards
   and the bullets. Use only facts from that version's section of `CHANGELOG.md`.
2. Render it.

   Windows, with Edge:

   ```
   msedge --headless --disable-gpu --hide-scrollbars --force-device-scale-factor=1 --window-size=1600,900 --screenshot=<output.png> file:///<absolute path>/card-list.html
   ```

   Linux:

   ```
   ./render.sh card-list
   ```

3. Open the PNG and check it: 1600×900, no clipped or overflowing text, still readable at a third of its
   size.

## Rules for the text

- English, short, no hashtags, no long dashes.
- No logos: the project has none, and third-party logos (Google, Gemma, NuGet, .NET) stay out. Naming
  them in text is fine.
- Keep the line "Community project, not affiliated with Google."

The fonts are Segoe UI on Windows and the next available fallback elsewhere, so a render on Linux looks
slightly different from one on Windows.
