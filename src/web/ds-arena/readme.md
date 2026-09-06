# CSX Arena — design system

The design system for the **market surface** of CryptoSmith X: the public pages that compare
venues. Not the bot dashboard (that surface has its own system), not the operator admin
console (it has its own too). Arena is what a visitor sees before there is an account —
seven exchanges quoting the same asset, and how stale each of those quotes is.

CryptoSmith X is algorithmic perpetual-futures trading software by **MB „BlynAI"**
(blynai.eu), a two-member Lithuanian research partnership that trades only its own funds and
publishes every result, including the failures. This system was authored from the product's
own admin pairs page and the palette of its sibling product, meetluko.eu.

## Who is on the other side of the screen

Someone deciding whether this outfit knows what it is doing. Not a customer yet. He is
comparing venues, but more than that he is reading the instrument itself: does this page know
when it last looked? Audience skews young — 18–25 first, 25–35 second — so the surface reads
as a research instrument and a workshop, not as a fintech dashboard and not as a crypto
casino.

**Desired feeling:** trading machine × open research lab × retro computer terminal.

## The rules, in one screen's worth of words

1. **Every figure carries the age of the call that wrote it.** Not the row's age — the
   call's. Price, open interest and the depth sweep are three separate calls with three
   clocks, so one row can hold a two-second price and a two-minute depth sweep, and it says
   so. A figure with no age is a claim with no date.

2. **Staleness is a fade, not a badge.** A figure is at full strength when its call lands and
   fades across one window (30 s), front-loaded: `1 − 0.85·(age/window)^0.4`, floored at
   0.15. Past the window nothing is graded further — 31 seconds and 30 days are the same
   verdict — so the weight stops falling and a `△` appears beside the age. Past twelve
   windows the count is dropped for the word `degraded`.

3. **The age line never fades.** A ghosted figure still has to be able to say when it died.

4. **Colour means which call, and it is rare.**
   | Meaning | Fill | Ink |
   |---|---|---|
   | Ticker call · accent · brand | `#B0007F` | `#B0007F` |
   | Open-interest call | `#2F5A00` | `#2F5A00` |
   | Depth sweep | `#8A5A12` · ask side `#C9A86A` | `#8A5A12` |
   | A call is past its window | — | `#FF4BD8` |
   | Best / worst in a column | `rgba(139,255,107,.5)` / `rgba(176,0,127,.14)` | `#2A4E00` / `#8E0066` |
   | Data | `#1E1408` | `#1E1408` |
   | Acid, the loud note | `#8BFF6B` | never text |

5. **Green and bronze are not money colours here.** This surface states no P&L, so nothing
   on it means profit or loss. Green means the open-interest call. That is the whole meaning.

6. **Acid green is a fill and it stays rare.** Three places on a whole page: the rule above
   the header, the BEST chip's wash, the brand mark. At 1.6:1 on apricot it can never be
   text, and a page that is acid everywhere has no loud note left.

7. **Both ends of a column, or neither.** BEST marks the winner and WORST the loser, computed
   across the venues shown, direction per column — highest bid, *lowest* ask, narrowest
   spread, largest size / turnover / open interest / depth. A table that only ever praises
   tells you half of what it knows. The chips are washes with dark ink: a saturated ground
   eats the word at 9px. Verdicts do not fade with the row — they are computed across
   venues, not read off the call this row is waiting on.

8. **A dash is not a zero.** `—` means not measured (`--text-unmeasured`); `0` is an
   observation (`--text-zero`). Spot venues have no mark price, no index, no funding and no
   open interest, so those cells are dashes on five of seven rows, and that is information.

9. **A number is never framed.** Borders mean state or control. Categorical values — SPOT,
   PERP, TIGHT, OBSERVED — are mono caps, `.17em` tracking, coloured, unframed. BEST and
   WORST are the one exception, because they mark a rank rather than describe a value.

10. **Nothing glows and nothing moves.** No shadow, no glass, no radius anywhere. One
    gradient exists in the system and it is data: the freshness scale in the venue cell,
    green where a call just landed to magenta where it is spent. The only thing that changes
    on its own is an age, because an age is a clock.

11. **A line where there is history, a bar where there is scale, both where a cell holds
    both, and neither where a figure has no second dimension.** Seven columns carry a
    sparkline on a perpetual row — bid, ask, last, spread, funding, open interest, depth
    25bps — because the price series feeds three of them and not one. Sizes, turnover and
    depth 10 / 50bps carry a log-scaled bar against the largest venue on screen; depth 25bps
    carries both, the mirrored bar for the two sides and the line for the hour. Mark and
    index carry neither: they are quoted rather than accumulated, and a bar against other
    venues would rank numbers that are not competing. Log on the bars, because linear
    flattens a 60-unit book against a 3,200-unit one into nothing.

    Those hourly series come from two stores, not one. `market_metric_hour` holds spread,
    funding, open interest and depth 25bps. It has no price column, so the price line is read
    from `market_candle.close`.

12. **Seventeen columns on one screen, no horizontal scroll at 1920.** Three of identity —
    platform, symbol, state — and fourteen of market data. That is the density constraint the
    whole type ladder exists to satisfy: 13px platform name, 12px figures, 9px labels,
    8.5px ages. The sheet is 1920 wide and the table is fixed at 1836px of columns, so the
    promise is kept at that width and not below it; narrower, venue and symbol stay sticky
    and the rest scrolls.

## Content fundamentals

- **English UI.** Terse, factual, no reassurance. The screen states; it does not comfort.
- **Casing:** display caps for the statement line; MONO CAPS for eyebrows, states, labels and
  API enums; sentence case for the two or three sentences of prose.
- **Voice:** "you/your" toward the reader; the system is "it" — *it decided*, *it is behind*.
  Never "we".
- **Numbers:** always mono, always tabular, always precise — `98,102.50`, `+0.0031%`,
  `12 s ago`, `× 10`.
- **Nothing invented.** There is no decision feed in the API, so the page shows ages and
  counters rather than a fake stream. If a field is not in the API, it is not on the screen.
- **No emoji, ever.** Compliance line verbatim: NOT INVESTMENT ADVICE · OWN FUNDS ONLY.

Example, in house voice: **SEVEN VENUES QUOTE AR/USD. ONE BOOK IS NINETY SECONDS OLD.**

## Visual foundations

- **Surfaces.** Apricot paper `#FFDFAF` under a 4px dot raster at 6%; cards are opaque
  `#FFF3DE` and the raster never shows through one; the column eyebrow strip is sunken
  `#FFE7C4`. Night is the same ladder inverted — `#0D0B08` / `#17130D`.
- **Type.** Anton (display, condensed caps) · Space Grotesk (the little prose, self-hosted in
  `fonts/`) · DM Mono (every figure and label). **Anton and DM Mono are still on the CDN** —
  see the note at the top of `tokens/fonts.css`; downloading them is the one open task.
- **Borders.** Hairlines at 14% ink between rows, 26% around a card. Zero radius everywhere.
- **Shadows, gradients, blur, glass:** none. One gradient, and it is the freshness scale.
- **Hover.** Colour only, 120ms, linear. Cells carry `title` attributes for absolute UTC
  clocks; nothing moves or grows.
- **Vertical group washes.** Open-interest columns at 3.5% green, depth columns at 5% bronze,
  so the three calls read as three vertical bands down the whole table.
- **Layout.** One 1920-wide sheet, table fixed at 1836px of columns, venue and symbol columns
  sticky. Never a fixed height on anything holding text.
- **Imagery.** None. There is no illustration in this system and no photography; the data is
  the picture.

## Iconography

**No icons.** Meaning is carried by colour, mono-caps labels, dots, bars and unicode marks:
`—` (not measured), `△` (past its window), `↓` in a legend, `·` as a separator, `/` between
the two sides of a depth band. The feed state is a 9px dot — filled for observed, a hollow
ring for nothing observed — and the word is in the `title`. No icon font, no SVG icon set,
no emoji. A first icon would need the same argument the bot system's sun/moon had to make.

**One exception, and it is identity, not decoration.** A venue's own mark and an asset's own
mark may appear in the identity columns — beside the platform name, beside the pair on the
list, once in a pair page's header — and nowhere else. The mark answers the question the
`platform` and `symbol` columns already answer in type, only faster; it carries no meaning
the row does not already state in words, which is the test decoration fails. It is layered
onto a name that is always there and never replaces it, because most rows have no mark: 83
of 177 collected assets have a usable one-ink file, and of the eight venue codes artwork was
found for, six do. Everything
the rule already said still holds — no icon font, no icon set, no emoji, no glyph for
anything the page merely *does*. The five terms of the exception:

1. **One ink, always.** A mark is drawn in `--text-data`, the ink of the name beside it —
   never in the brand's own colours. Colour on this surface means which call wrote a figure
   (rules 4 and 6), and a brand's hue is somebody else's decision about nothing we measured.
   One ink is also the only variant that survives both grounds: 26 of the 98 full-colour
   files fall under 1.6:1 against apricot or against night, and 45 of 98 under 3:1 on the
   day card.
2. **A mark does not fade with its row.** It joins the age line (rule 3), not the figures.
   The fade says *this evidence is old*; a venue's identity is not evidence and does not
   age. And at the 0.15 floor a faded mark is indistinguishable from a slot that has none —
   the dash-vs-zero confusion, in graphic form.
3. **Two sizes and no others.** `--slot-ident` (16px) in a table row, `--slot-ident-page`
   (28px) in a page header, square, `--gap-ident` before the name.
4. **Below 16px there is no mark.** Under it a brand mark is a smear, so the slot holds the
   monogram instead: the first two characters of the code, mono caps at `--track-label`, in
   `--text-unmeasured` — the ink of the em dash. Same fallback when no file exists. No tile,
   no disc, no ring: a shape around a letter *is* a logo shape, and inventing one is the
   graphic form of printing `0` where nothing was measured.
5. **A glyph, not a lockup.** A file that spells the name is not admissible: in a square
   16px slot it draws 3–4px of ink and repeats a word the row has already said.

**No logo was provided.** The wordmark is set in Anton as plain type; nothing here is drawn
to stand in for a mark. The bot system's assets were not copied in — they belong to that
surface.

## Index

- `styles.css` — the single entry point consumers link (`@import` lines only)
- `tokens/` — `fonts.css`, `colors.css`, `typography.css`, `spacing.css`, `effects.css`,
  `base.css`
- `fonts/` — Space Grotesk 300–700 variable, latin + latin-ext woff2
- `guidelines/` — 13 specimen cards (Colors · Type · Spacing · Brand)
- `components/core/` — **Num**, **Tag**, **StateDot**, **AgeLine**, **CompareBar**,
  **MirrorBar**, **Sparkline**
- `components/market/` — **CallBands**, **FreshnessStrip**, **VenueCell**, **MetricCell**,
  **CandlePanel**, **IdentityMark**
- `ui_kits/pairs-monitor/` — the public venue comparison, day and night
- `vendor/lightweight-charts/` — the charting library the candle panel draws with (vendored
  from the product repo, Apache-2.0)
- `thumbnail.html` — the homepage tile: wordmark in Anton on magenta, call-colour swatch strip
- `SKILL.md` · `github.md` (source repo association)

### Intentional additions

The market surface had no components to inherit — the product's admin pairs page is a raw
Razor table with no design system behind it — so this inventory is authored from that page's
data contract rather than lifted from a component library:

- **Num** exists because the dash-vs-zero rule is a house rule and therefore a component.
- **AgeLine** exists because "every figure carries its own age" is the surface's whole idea.
- **CompareBar / MirrorBar** exist because half the columns keep no hourly history, and an
  empty cell says nothing about scale.
- **MetricCell** exists to fix the vertical order inside a cell — mark slot, figure, history,
  age — so figures sit on one line across a row.
- **IdentityMark** exists because the identity marks are an exception taken to a house rule,
  and an exception needs exactly one place that implements it — including the case that is
  the majority, where there is no file and the slot holds a monogram. The files themselves
  are not in this system: they are `marks/` at the repository root, keyed by `exchange.code`
  and `asset.code` verbatim, with provenance in `marks/MANIFEST.json` and `docs/logos.md`.

## Sources

- **Product repo:** https://github.com/blyn-ai/CryptoSmith-X — `Areas/Admin/Views/Pairs/At.cshtml`
  (the field list and the schema this system reads), `wwwroot/pair-charts.js` and
  `wwwroot/vendor/lightweight-charts/` (the candles), `brand/rules/*`.
- **Sibling product:** https://github.com/LKSPec/luko (meetluko.eu) — `website/styles.css`
  and `website/market.html`: the apricot register, the 4px dot raster, the panel/label/accent
  composition, and the two-register colour thinking this palette comes from.
- **Sibling design system:** the CSX Bot system — the ten rules this one extends, the
  dash-vs-zero rule, "a border means state or control", and the Space Grotesk woff2 copied
  into `fonts/`.
- **Live API (not reachable from the build environment):** `blynai.meetluko.eu`. Sample data
  throughout is illustrative.

Read those before doing serious design work against this brand.

## Corrections

Rules 11 and 12 were rewritten on 2026-09-06 to match the page they describe. Both were
checked against the rendered `pairs-monitor` and against the product schema, not read.

- **Rule 12** said twenty fields. The page carries seventeen columns and says so in its own
  header (`17 FIELDS`): platform, symbol, state, then bid, ask, spread, bid size, ask size,
  last, mark, index, funding, turnover, open interest and depth at three bands. It also
  promised "no horizontal scroll" absolutely, where the sheet is 1920 wide and the table
  1836px — a promise that holds at that width and nowhere below it.

- **Rule 11** said five hourly series get lines and everything else gets a bar. Measured on
  the page, a perpetual row carries seven sparklines, because the price series feeds bid, ask
  and last rather than one column; depth 25bps carries a line *and* a mirrored bar; and mark
  and index carry neither, which the old dichotomy had no room for. The rule also named one
  source for all five series, and there are two — `market_metric_hour` has no price column.

The page was treated as the authority in both cases. A rule that disagrees with the surface
it governs is the thing that is wrong.

**Iconography took an exception on 2026-09-06.** The section said "No icons … no icon font,
no SVG icon set, no emoji. A first icon would need the same argument the bot system's
sun/moon had to make." It now says that, and then names one class of object it does not
cover: a venue's or an asset's own mark, in the identity columns only. This is not a
correction of a rule that disagreed with the page — the page had no marks — it is an
exception being taken, so it is written down rather than absorbed. The argument the old
sentence demanded is made in the section itself and at length in `RULE-CHANGES.md` items
5–9; the short form is that a mark states the same fact the identity column already states
in type, which is the test decoration fails.

Rules 4 and 6 are untouched, and that is the reason the mark is drawn in one ink. A
full-colour mark would have put a hue on this surface that means nothing about our data, and
rule 4's table — colour means *which call* — would have needed a sixth row saying "and
sometimes it means nothing." Measurement pointed the same way before taste did: 26 of the 98
full-colour files are under 1.6:1 on one of the two card grounds. The palette survives the
feature intact.