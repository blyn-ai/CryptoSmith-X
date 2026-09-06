// Candle panels: one Lightweight Charts instance per venue, self-hosted from wwwroot/vendor/.
//
// Price is the only true OHLC series the collector stores, so it is the only thing on this surface
// drawn as a candle. Everything else the page shows is a figure with an age; a candle is a claim
// about a whole hour and we have that claim for exactly one column.
//
// Why a library at all, on a page that draws its own sparklines in server SVG: the split is recorded
// in plans/notes-charts-and-tradingview.md. An 11-pixel line has no axis, no crosshair and no
// panning, so a library for it would be 198 KB of nothing; a 212-pixel financial chart has all
// three, and hand-rolling them is how a page ends up with a time axis that is subtly wrong.
//
// ── HOLLOW ABOVE THE OPEN, FILLED BELOW ──
// The direction of a candle is carried by the FILL, not by the colour. Rule 5: this surface states
// no P&L, so nothing on it is allowed to mean profit or loss, and a green body would say "up is
// good" on a page that does not know which side the reader is on. upColor is fully transparent and
// the border carries the shape.
//
// ── TWO THINGS THE HEADERS CLAIM, SO TWO THINGS THIS HAS TO DO ──
// The panels say "one scale per quote" and "time axes tied", and both are load-bearing:
//
//   * The PRICE scale is shared across every panel quoting in the same asset, and across no others.
//     Without sharing, each panel autoscales to itself and a venue that moved two cents looks
//     exactly as volatile as one that moved forty. Sharing it ACROSS quotes would be the mistake the
//     verdict scope exists to prevent — one number line drawn through two currencies — which is why
//     the server hands each panel its group's range rather than one page-wide pair of numbers.
//
//   * The TIME axes are tied across all of them, quote or no quote. Time is not denominated in
//     anything, and the whole point of stacking the panels is that the same hour sits under the same
//     place on every one.
(() => {
  'use strict';

  const els = [...document.querySelectorAll('.a-chart[data-candles]')];
  if (!els.length || !window.LightweightCharts) return;

  const v = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  const chartOptions = () => ({
    autoSize: true,
    layout: {
      background: { color: 'transparent' },
      textColor: v('--text-faint'),
      fontFamily: v('--font-mono'),
      fontSize: 10,
      attributionLogo: false,
    },
    grid: { vertLines: { color: v('--border-hairline') }, horzLines: { color: v('--border-hairline') } },
    rightPriceScale: { borderColor: v('--border-hairline'), scaleMargins: { top: 0.06, bottom: 0.06 } },
    timeScale: {
      borderColor: v('--border-hairline'),
      timeVisible: true,
      secondsVisible: false,
      rightOffset: 2,
      barSpacing: 12,
      minBarSpacing: 4,
    },
    crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
  });

  const candleOptions = () => ({
    // Transparent up body, coloured border and wick: the shape says the direction, the colour says
    // only which of the two candle inks it is.
    upColor: 'rgba(0,0,0,0)',
    downColor: v('--candle-down'),
    borderUpColor: v('--candle-up'),
    borderDownColor: v('--candle-down'),
    wickUpColor: v('--candle-up'),
    wickDownColor: v('--candle-down'),
    borderVisible: true,
    priceLineVisible: false,
    lastValueVisible: false,
  });

  const made = els.map((el) => {
    const rows = JSON.parse(el.dataset.candles);
    const lo = Number(el.dataset.scaleLo);
    const hi = Number(el.dataset.scaleHi);
    const decimals = Number(el.dataset.decimals);

    const chart = LightweightCharts.createChart(el, {
      ...chartOptions(),
      // Printed to the venue's own tick, like every other price on the page. A chart axis that
      // rounds to two decimals on a four-decimal instrument is inventing a precision downward,
      // which is the same class of error as inventing one upward.
      localization: {
        priceFormatter: (p) => p.toFixed(Number.isFinite(decimals) ? decimals : 4),
      },
    });

    const series = chart.addSeries(LightweightCharts.CandlestickSeries, candleOptions());

    // An hour with no bar goes in as a whitespace point — time only, no OHLC. That reserves its slot
    // on the axis so the gap stays a gap. Dropping it would slide the surrounding candles together
    // and a venue that went dark for six hours would draw as one that never stopped.
    series.setData(rows.map((r) => (r.o === null || r.o === undefined
      ? { time: r.time }
      : { time: r.time, open: r.o, high: r.h, low: r.l, close: r.c })));

    if (Number.isFinite(lo) && Number.isFinite(hi) && hi > lo) {
      const pad = (hi - lo) * 0.08;
      const range = { minValue: lo - pad, maxValue: hi + pad };
      series.applyOptions({ autoscaleInfoProvider: () => ({ priceRange: range }) });
    }

    return { chart, series };
  });

  // Tied only once every instance exists, or the early panels get dragged around by instances that
  // have no data yet.
  let syncing = false;
  made.forEach(({ chart }) => {
    chart.timeScale().fitContent();
    chart.timeScale().subscribeVisibleLogicalRangeChange((range) => {
      if (syncing || !range) return;
      syncing = true;
      made.forEach((other) => {
        if (other.chart !== chart) other.chart.timeScale().setVisibleLogicalRange(range);
      });
      syncing = false;
    });
  });

  // The library reads the custom properties once, at creation, and keeps its own copy — so the
  // register flip has to reach in and push them back. arena-ages.js fires this after it moves
  // data-theme; a MutationObserver would work too and would also fire for every other attribute
  // anyone ever adds to <html>.
  const repaint = () => made.forEach(({ chart, series }) => {
    chart.applyOptions(chartOptions());
    series.applyOptions(candleOptions());
  });

  document.addEventListener('csx-arena-theme', repaint);
})();
