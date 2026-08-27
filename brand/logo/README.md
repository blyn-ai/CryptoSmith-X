# CryptoSmith X — logo SVGs

Six files: three lockups × two surfaces. The design system is dark-only by charter,
so **ink is the primary set**; the `-paper` files exist for light surfaces exactly the
way `mark-paper.svg` already does in the design system.

| File | Size | Use |
|---|---|---|
| `cryptosmith-mark.svg` · `-paper` | 40×40 | mark alone — favicon, avatar, app icon |
| `cryptosmith-lockup.svg` · `-paper` | 243×40 | mark + wordmark — nav, header, sign-in |
| `cryptosmith-lockup-descriptor.svg` · `-paper` | 267×48 | mark + wordmark + descriptor — about page, OG card, deck cover, footer |

`preview.html` shows all six on both surfaces, at four sizes, with a rule on the
right edge of the "X" so you can check the descriptor still lands on it.

## Why these files are bigger than a logo usually is (~45 KB)

Space Grotesk and IBM Plex Mono are **embedded** in each lockup as base64 woff2.
The wordmark is live text (design-system rule: *the wordmark is always live text*),
and without the font a viewer substitutes Helvetica — which was the bug in the first
cut: a substituted font is a different width, so the descriptor no longer matched the
title. Embedding removes the dependency: the file renders identically in a browser,
an `<img>`, a README, an e-mail client, anywhere.

The mark-only files carry no font and stay at ~600 bytes.

## Belt and braces: the geometry is pinned

Every run carries `textLength`, so even in a tool that ignores embedded fonts
(Figma, Illustrator, some print pipelines) the lockup keeps its proportions — the
substituted glyphs are tracked to the authored width instead of running long.
With the real font, the pinned width *is* the natural width, so nothing distorts.

Type properties are **inline styles**, not classes or presentation attributes. That
matters when you paste the SVG into a page: an inherited `text-transform` or
`letter-spacing` from the host stylesheet would otherwise reach the text and — with
the width pinned — crush the glyphs into each other. Inline style outranks
inheritance, and no two inlined lockups can fight over a shared class name.

## Geometry (don't re-derive by eye)

- Wordmark: Space Grotesk 600, `letter-spacing: -.01em`. "CryptoSmith" in heading
  colour, "X" in violet — `#A18AFF` on ink, `#6B4EDB` on paper, per the logo rule.
- Descriptor: IBM Plex Mono 500 at **0.4×** the title size, `letter-spacing: .1em`,
  split `PERPS & CRYPTO` / `TRADE BOT` and justified so the pair spans **exactly** the
  title width — left piece start-anchored, right piece end-anchored on the "X".
- The descriptor's cap-top clears the `y` descender of "Crypto" by 3.4 px at the
  authored size. They never touch, at any scale — the whole lockup scales as one.
- The wordmark baseline centres the **cap height** on the mark's axis, not the em box;
  centring the em box drags the word visually low.

## Minimum sizes

Full lockup with descriptor: **≥ 40 px tall** — the descriptor is 11.2 px at the
authored 48 px and turns to mush below that. Below 40 px use the plain lockup;
below 24 px use the mark alone.

## Don't

Redraw the mark, re-space the descriptor, stack the mark above the text, recolour the
"X", add a tagline, or put an ink file on a light surface (or the reverse).

---

## Added after the handoff (not part of the designer's export)

- **`cryptosmith-favicon.svg`** — the browser/app icon: a rounded-square plate with
  the mark fitted by width to 82% and centred. The design system's `coin.svg` is
  round, which reads as a token rather than an app; this is the square equivalent.
  The mark geometry is copied from `cryptosmith-mark.svg`, never redrawn.
- **`make-favicons.py`** — regenerates `favicon.ico` (16/32/48) for both the site and
  the WebApp, plus `apple-touch-icon.png`, from the geometry above. Run it after any
  change to the icon:

  ```bash
  python3 brand/logo/make-favicons.py
  ```

  It redraws rather than rasterises, because the only SVG rasteriser on a stock
  macOS is Quick Look and it mattes transparency onto white — which would put a
  white box behind the rounded corners in every dark browser tab.

At 16 px the mark keeps its own proportions, so the star sits in a middle band with
air above and below; that is the mark being wide and short, not a sizing mistake. If
16 px ever needs to read harder, the fix is a dots-free variant for that frame alone
— which is a change to the mark and therefore a decision, not a tweak.
