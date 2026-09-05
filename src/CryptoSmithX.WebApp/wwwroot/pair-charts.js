// One Lightweight Charts instance per platform on the pair page, with their time
// scales tied together. Self-hosted from wwwroot/vendor/ — see the README there
// for version and licence, and plans/notes-charts-and-tradingview.md for why
// candles get a real library while the 18–96px operational strips stay server SVG.
//
// The tying is the whole point of the page. Comparing platforms means looking at
// the same minutes on each, so panning or zooming any chart moves every other one
// to the same range; without that they drift apart on the first scroll and the
// stacking stops meaning anything.
//
// Containers carry data-live-skip, the attribute live.js already honours: a tick's
// morph must never recurse into a canvas chart's subtree, or the library keeps a
// RAF loop running against a detached node that paints nowhere and is never freed.
(() => {
  'use strict';
  const els = [...document.querySelectorAll('.chart-lwc[data-candles]')];
  if (!els.length || !window.LightweightCharts) return;

  const v = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  const chartOptions = () => ({
    autoSize: true,
    layout: { background: { color: 'transparent' }, textColor: v('--text-data'), fontFamily: v('--font-mono'), fontSize: 11 },
    grid: { vertLines: { color: v('--border-hairline') }, horzLines: { color: v('--border-hairline') } },
    rightPriceScale: { borderColor: v('--border-hairline') },
    timeScale: { borderColor: v('--border-hairline'), timeVisible: true, secondsVisible: false },
    crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
  });

  const candleOptions = () => {
    const up = v('--up-500'), down = v('--down-500');
    return { upColor: up, downColor: down, borderUpColor: up, borderDownColor: down, wickUpColor: up, wickDownColor: down };
  };

  const made = els.map((el) => {
    const rows = JSON.parse(el.dataset.candles);
    const chart = LightweightCharts.createChart(el, chartOptions());
    const candles = chart.addSeries(LightweightCharts.CandlestickSeries, candleOptions());

    // A window with no bar is passed as a whitespace point — time only, no OHLC.
    // That reserves its slot on the axis so the gap stays a gap. Dropping it
    // instead would slide the surrounding candles together, and a platform that
    // went dark for ten minutes would look like it never stopped.
    candles.setData(rows.map((r) => (r.o === null ? { time: r.time } : {
      time: r.time, open: r.o, high: r.h, low: r.l, close: r.c,
    })));

    // Volume on its own scale pinned to the bottom: volumes differ across venues
    // by orders of magnitude, so one shared scale would flatten every smaller
    // platform to nothing.
    const volume = chart.addSeries(LightweightCharts.HistogramSeries, {
      priceScaleId: 'vol', priceFormat: { type: 'volume' }, lastValueVisible: false, priceLineVisible: false,
    });
    chart.priceScale('vol').applyOptions({ scaleMargins: { top: 0.82, bottom: 0 } });
    volume.setData(rows.filter((r) => r.o !== null).map((r) => ({
      time: r.time, value: r.v, color: r.c >= r.o ? v('--up-500') : v('--down-500'),
    })));

    chart.timeScale().fitContent();
    return { chart, candles, volume };
  });

  // Tie the time scales together. The guard stops the echo: applying a range
  // fires the subscription again on the chart we just moved.
  let syncing = false;
  made.forEach(({ chart }) => {
    chart.timeScale().subscribeVisibleLogicalRangeChange((range) => {
      if (syncing || !range) return;
      syncing = true;
      made.forEach((other) => {
        if (other.chart !== chart) other.chart.timeScale().setVisibleLogicalRange(range);
      });
      syncing = false;
    });
  });

  // theme-toggle.js flips data-theme with no reload; the charts read these custom
  // properties only at creation, so they need their own nudge to follow.
  new MutationObserver(() => made.forEach(({ chart, candles }) => {
    chart.applyOptions(chartOptions());
    candles.applyOptions(candleOptions());
  })).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
})();
