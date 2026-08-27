# Social cards

The image a link turns into when it is pasted into X, Telegram, Slack, LinkedIn
or a chat preview. Masters and specification live here; the exported PNG has to
be deployed, so it lands in `src/web/` — see *Wiring* below.

## Current state — there is no card

Nothing in `src/web/` sets `og:image` or `twitter:image`. Two consequences, one
of them a live bug:

- `src/web/zurnalas.html` declares `twitter:card` = `summary_large_image` **and
  supplies no image**. A large-image card with no image degrades to a bare text
  card, or to nothing at all depending on the client. Either fix the image or
  drop the declaration; shipping the claim without the asset is the one option
  that is simply wrong.
- `src/web/index.html` has `og:title`, `og:description`, `og:type`, `og:locale`
  and no image, so every share of the front page is text-only.

## Sizes

| Use | Pixels | Ratio |
|---|---|---|
| `og:image` / `twitter:image` (large card) | **1200 × 630** | 1.91 : 1 |
| Square fallback (some chat clients crop to it) | 1200 × 1200 | 1 : 1 |

Export PNG. Keep each file under ~1 MB; several clients refuse to fetch more and
silently fall back to no card at all.

## Layout

- **Safe area:** keep everything meaningful inside a 60 px margin. Telegram and
  LinkedIn crop the 1.91:1 card differently, and X rounds the corners.
- **Dark only.** The card is a brand surface, not a product surface — Ink, not
  Paper. `--surface-page` ground, the 2 px gold→violet rule along the top edge.
- **The mark** at 1.5–2× its normal size, top-left, with the wordmark as live
  text: "CryptoSmith" in `--text-heading`, "X" in `--violet-400`. Never redraw
  the mark; use `src/web/ds/assets/cryptosmith-mark.svg`.
- **One headline**, sentence case, Space Grotesk, ≤ 8 words. At most one clause
  may carry the gold→violet gradient — the same one-clause-per-page rule the
  design system applies everywhere else.
- **No screenshots of numbers** unless the number is real, dated on the card,
  and reproducible from the published journal. See *Claims*.

## Claims — the part that is not a design question

The company publishes research data, not offers. A social card is the most
context-free surface the product has: it arrives with no page around it, so a
number on it is read as a promise.

- Compliance line verbatim, mono caps, no substitutions:
  `NOT INVESTMENT ADVICE · OWN FUNDS ONLY`
- No projected, annualised, extrapolated or best-case figures. Ever.
- A performance figure needs its period on the same card, and must match the
  published journal for that period.
- Failed experiments are published the same way successful ones are. A card that
  only ever shows green is a lie told by selection.

## Wiring

`og:image` must be an **absolute** URL — relative paths are ignored by most
scrapers. The site is served at `https://cryptosmithx.blynai.eu`.

```html
<meta property="og:image" content="https://cryptosmithx.blynai.eu/social/card.png">
<meta property="og:image:width" content="1200">
<meta property="og:image:height" content="630">
<meta property="og:image:alt" content="CryptoSmith X — perps and crypto trade bot">
<meta name="twitter:card" content="summary_large_image">
<meta name="twitter:image" content="https://cryptosmithx.blynai.eu/social/card.png">
```

Put the exported PNG at `src/web/social/card.png` so Pages serves it at that URL.
Scrapers cache aggressively: change the **filename** when the art changes, or the
old card will keep appearing for days.

## Per-page cards

`zurnalas` is the page most worth a card of its own, and the only one whose card
could be generated rather than drawn — the journal already computes its figures
in one place for exactly this reason (see the note at the top of the script in
`src/web/zurnalas.html`, which anticipates an og:image renderer importing the
same module so the card and the page cannot disagree). If that renderer is ever
built, its output belongs here as a template, and the *Claims* section above
applies to it unchanged.
