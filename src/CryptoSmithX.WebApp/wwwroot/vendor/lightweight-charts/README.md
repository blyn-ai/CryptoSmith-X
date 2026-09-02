TradingView **Lightweight Charts™** v5.2.1, Apache-2.0. Self-hosted per repo
rule (no CDN scripts in a served page): `lightweight-charts-5.2.1.standalone.production.js`
is the unmodified UMD build from the npm package (`dist/lightweight-charts.standalone.production.js`
in `lightweight-charts@5.2.1`), defining `window.LightweightCharts`.

`LICENSE` and `NOTICE` are the package's own, copied verbatim — Apache-2.0 §4(d)
requires redistributing NOTICE, and the library's own terms additionally ask
for a visible link to tradingview.com on any page that uses it. The chart's
default `attributionLogo: true` satisfies that link requirement on-page; do
not turn it off.

Used only by `Areas/Admin/Views/Instruments/Details.cshtml` (the Price panel —
the one genuinely financial chart with history; everything else stays server
SVG, see `plans/notes-charts-and-tradingview.md`). Loaded with a plain
`<script src>` in that view, not in `_Layout.cshtml`, so no other page pays
for it.

To upgrade: `npm view lightweight-charts dist.tarball`, pull the new
`dist/lightweight-charts.standalone.production.js`, rename with the new
version, update the `<script src>` in Details.cshtml, refresh LICENSE/NOTICE
if TradingView changed them, delete the old versioned file.
