# CryptoSmith X — Design System

**CryptoSmith X** is an algorithmic trading bot for **Kraken, Binance, WEEX and Hyperliquid** (spot + perps), built by **MB „BlynAI"** (blynai.eu) — a two-member Lithuanian finance lab that runs its software on its own money and publishes every decision, including the wrong ones. CryptoSmith X is the productised, **multi-user** version of that software: login, strategies, settings, charts, positions — plus a public about page.

This system is a sibling of the BlynAI Capital brand, not a copy: it inherits the gold/violet axes and the three typefaces, but lives **dark-only** ("Forge" direction), leads with violet, and — unlike BlynAI — allows green/red, strictly for market data.

## Sources
- Logo set + lockup recipe: user-supplied (`uploads/`, mirrored to `assets/`)
- Parent brand: https://github.com/blyn-ai/web (`ds/tokens/*`, fonts, brand rules in CLAUDE.md) — explore it for deeper BlynAI context
- Product repo (referenced, not read): https://github.com/blyn-ai/CryptoSmith-X

## Content fundamentals
- **Language:** English UI (parent site is Lithuanian-only; this product is not).
- **Tone:** terse, factual, lowercase-calm. Numbers speak, prose explains. No exclamation marks, no hype, no promises — "every result published, including the wrong ones."
- **Casing:** sentence case for headings, buttons and prose. MONO CAPS only for eyebrows, nav, badges and venue names.
- **Voice:** "you/your" toward the user; the bot is "it". Direct verbs on actions: *Sign in*, *Save changes*, *Stop strategy*.
- **Numbers:** always mono, always precise (`98,102.50`, `120 s`, `+0.66%`). A missing value is an em dash `—`, never an invented number.
- **No emoji. Ever.** Compliance lines stay verbatim: NOT INVESTMENT ADVICE · OWN FUNDS ONLY.

## Visual foundations
- **Dark only.** Page `#07060B`, cards `#0D0B14`, raised `#12101C` — violet-tinted blacks, never pure gray-black. Paper/light surfaces exist only in the logo's `mark-paper.svg`.
- **Two axes + market colours:** violet (`#A18AFF`/`#6B4EDB`) = AI, actions, links, borders; gold (`#F5B84F`) = brand signal — the mark, LIVE dots, active-nav underline, key stat accents. Green `#41C98F` / red `#EF5D6F` exist **only** for PnL, side and order status; never decorative.
- **The gradient device:** gold→violet appears as the 2px page-top rule (`.csx-rule`), one gradient text clause per page (`.csx-grad`), and the equity curve stroke. Not on buttons, not on backgrounds.
- **Washes:** large faint radial fields, always paired — gold NW + violet SE (`--wash-gold`, `--wash-violet`) — on auth and marketing surfaces only; console pages stay flat ink.
- **Type:** Space Grotesk (display, headings, buttons, stat values), IBM Plex Sans (prose only), IBM Plex Mono (every eyebrow, nav label, badge, number, table cell). Eyebrows 10px/.18em, nav 11px/.13em, data 12.5px.
- **Borders:** violet hairlines everywhere — `rgba(161,138,255,.11–.13)`; `.28` for strong/inputs; gold border `.30` reserved for the telemetry/logo plate.
- **Radii:** 4 badges/tabs · 6 buttons/inputs · 8 cards (default) · 12 modals/plates. No pills except the Switch.
- **Shadows:** near-none; ink barely casts. The only glows: LIVE dot (`--shadow-live`) and the brand plate (`--shadow-mark`).
- **Motion:** 120–280ms, `cubic-bezier(.2,.6,.2,1)`, colour transitions only. Nothing bounces, nothing moves for decoration.
- **Hover:** buttons darken (primary) or brighten border (ghost); links lighten toward lilac. Press states: none beyond colour.
- **Layout:** page gutter 30px, `--page-max` 1440 console / 1160 prose, KPI strips as 5-col grids, `1fr 340px` console split. Spacing scale has odd values (9, 14, 18, 22, 34) — intentional, don't snap to 4/8px.
- **Imagery:** none. No photos, no illustrations. Charts and the logo set are the only graphics.

## Iconography
There is **no icon set** — inherited BlynAI rule. Meaning is carried by: colour-coded 6–7px dots (status), unicode arrows `↑ ↓ ▾ →` and `●` in mono, mono-caps tags, and the logo set. Never import an icon font or draw ad-hoc SVG icons. The only SVGs are the brand marks (`assets/`) and data charts (EquityCurve).

## Index
- `styles.css` → `tokens/` — fonts, colors, typography, spacing, effects, base (197 tokens)
- `assets/` — cryptosmith-mark / -mono / -paper / coin / favicon SVGs; equity-curve-full/band.svg (from parent repo)
- `fonts/` — self-hosted woff2: Space Grotesk 300–700 var, IBM Plex Sans 300–600 (+italic), IBM Plex Mono 400–600
- `guidelines/` — 13 specimen cards (Colors / Type / Spacing / Brand)
- `explorations/` — the three original direction candidates (V1 Forge chosen)

### Components
- `components/core/` — **Button, Tag, SideBadge, Tabs, Card, KpiTile**
- `components/forms/` — **Input, Select, Switch, Checkbox**
- `components/trading/` — **EquityCurve, PositionsTable, StrategyCard, VenueStatus**
- `components/navigation/` — **TopNav, Wordmark**
- `components/feedback/` — **Dialog, Toast**

Intentional additions (no product source existed): the whole set above is authored from the chosen direction; trading primitives (EquityCurve, PositionsTable, StrategyCard, VenueStatus, KpiTile, SideBadge) exist because the product is a trading console.

### UI kits
- `ui_kits/console/` — the product app: login → Overview / Strategies / Settings (interactive)
- `ui_kits/site/` — public about page (static)

## Logo rules
Never redraw the mark. `mark.svg` on any dark surface, `mark-paper.svg` on light, `mark-mono.svg` takes currentColor for print, `coin.svg` for avatars. The wordmark is **always live text**: Space Grotesk 600, "CryptoSmith" in heading colour, "X" violet (`#6B4EDB` on paper, `--violet-400` on ink); descriptor "PERPS & CRYPTO TRADE BOT" in Plex Mono at 0.4× title size, .1em tracking, space-between across the title width. Use the `Wordmark` component.
