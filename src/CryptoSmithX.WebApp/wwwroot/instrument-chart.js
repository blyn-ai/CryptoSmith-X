// Price panel: TradingView Lightweight Charts (self-hosted, wwwroot/vendor/ —
// see the README there for version/licence). Instruments/Details.cshtml only.
//
// The container carries data-live-skip, the same attribute live.js already
// honours for feed dialogs and forms: a tick's morph never recurses into it.
// A canvas-driven chart needs that guarantee — the old live.js replaced every
// child of <main> on each 10 s tick, which would tear the chart's DOM node out
// from under it while the library's own RAF loop and listeners kept running
// against a detached canvas, painting nowhere and never freed. Skipping the
// subtree is simpler and cheaper than destroying and recreating the chart
// every tick, and it is consistent: the whole panel (chart plus its OHLC
// summary line) goes stale together rather than one half updating without
// the other. A real navigation — clicking a timeframe chip, reloading — gives
// a fresh document and re-runs this script from scratch, so staleness never
// outlives the page it appeared on.
(() => {
  'use strict';
  const el = document.querySelector('.chart-lwc[data-candles]');
  if (!el || !window.LightweightCharts) return;

  const candles = JSON.parse(el.dataset.candles);
  const theme = () => getComputedStyle(document.documentElement);
  const v = (name) => theme().getPropertyValue(name).trim();

  const chartOptions = () => ({
    autoSize: true,
    layout: { background: { color: 'transparent' }, textColor: v('--text-data'), fontFamily: v('--font-mono'), fontSize: 11 },
    grid: { vertLines: { color: v('--border-hairline') }, horzLines: { color: v('--border-hairline') } },
    rightPriceScale: { borderColor: v('--border-hairline') },
    timeScale: { borderColor: v('--border-hairline'), timeVisible: true, secondsVisible: false },
    crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
  });

  const seriesOptions = () => {
    const up = v('--up-500'), down = v('--down-500');
    return { upColor: up, downColor: down, borderUpColor: up, borderDownColor: down, wickUpColor: up, wickDownColor: down };
  };

  const chart = LightweightCharts.createChart(el, chartOptions());
  const series = chart.addSeries(LightweightCharts.CandlestickSeries, seriesOptions());
  series.setData(candles);
  chart.timeScale().fitContent();

  // theme-toggle.js flips document.documentElement.dataset.theme instantly, no
  // reload — the chart only reads these custom properties at creation time,
  // so it needs its own nudge to follow the switch.
  new MutationObserver(() => {
    chart.applyOptions(chartOptions());
    series.applyOptions(seriesOptions());
  }).observe(document.documentElement, { attributes: true, attributeFilter: ['data-theme'] });
})();
