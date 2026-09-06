repo: blyn-ai/CryptoSmith-X
branch: main
path: src/CryptoSmithX.WebApp/Areas/Admin/Views/Pairs

## Last sync

date: 2026-09-06T12:05:00Z

### Updated in this project

- Public Pairs monitor in the shape of the source page: one row per platform, all 20 fields, sticky PLATFORM/SYMBOL, desktop layout at 1920.
- Candles use the repo's own self-hosted TradingView Lightweight Charts 5.2.1 (copied into `vendor/`), three per row with tied time scales, following `wwwroot/pair-charts.js`.
- Per-column freshness: live age per cell, cell fades across one 30 s window, `△` and `degraded` past it; PLATFORM carries the row's freshest/oldest and a tick per call.
- Colour drawn from the sibling repo `LKSPec/luko` (meetluko.eu): apricot paper, magenta ticker band, acid-green system ink, pink reserved for a late call.

### Design system

`csx-arena` (this project's root: `styles.css`, `tokens/`, `components/`, `guidelines/`,
`ui_kits/pairs-monitor/`, `SKILL.md`) — authored from `Areas/Admin/Views/Pairs/At.cshtml`
and the apricot register of `LKSPec/luko`. Space Grotesk woff2 copied from the CSX Bot
system; Anton and DM Mono still on the CDN.

## Screen map

| Screen | Built from |
|---|---|
| `Pairs Monitor - Apricot acid.dc.html` (chosen) | `Areas/Admin/Views/Pairs/At.cshtml`, `wwwroot/pair-charts.js`, `wwwroot/vendor/lightweight-charts/*`, `LKSPec/luko` `website/styles.css` + `website/market.html` + `assets/brand-guide.md`, `uploads/pairs-page-brief.md` |
| `Pairs Monitor - Luko gold.dc.html` | same, luko site chrome palette |
| `Pairs Monitor - Acid dark.dc.html` | same, apricot-on-black palette |
| `Pairs Monitor v2.dc.html` | same source, monochrome design-system baseline |
| `Pairs Monitor.dc.html` | v1, two-line row exploration |
