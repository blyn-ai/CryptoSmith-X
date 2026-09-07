Wraps one Lightweight Charts instance per venue.

```jsx
<CandlePanel platform="Bybit" symbol="AR-PERP" range="6.3143 – 6.4300 · shared scale"
  onMount={el => {
    const chart = LightweightCharts.createChart(el, { width: el.clientWidth, height: el.clientHeight, /* … */ });
    const s = chart.addSeries(LightweightCharts.CandlestickSeries, {
      upColor: v('--candle-up'), borderUpColor: v('--candle-up'), downColor: v('--candle-down')
    });
    s.setData(bars);
    return () => chart.remove();
  }} />
```

Read the colours from the tokens at creation AND re-read them after the stylesheets land —
the library caches them, so a chart built too early paints black.

BOTH BODIES ARE FILLED — closed above its open in `--candle-up`, below in `--candle-down`.
This is the single exception to rule 5, it is scoped to candles and to nothing else, and it is
recorded as entry 10 in RULE-CHANGES.md. A candle is a shape with its own vocabulary, not a
figure in a column: no age line, no mark slot, nothing to be ranked against. Everywhere else
on the surface green still means the open-interest call and means only that.
