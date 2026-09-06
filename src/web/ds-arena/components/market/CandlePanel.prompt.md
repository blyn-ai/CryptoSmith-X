Wraps one Lightweight Charts instance per venue.

```jsx
<CandlePanel platform="Bybit" symbol="AR-PERP" range="6.3143 – 6.4300 · shared scale"
  onMount={el => {
    const chart = LightweightCharts.createChart(el, { width: el.clientWidth, height: el.clientHeight, /* … */ });
    const s = chart.addSeries(LightweightCharts.CandlestickSeries, {
      upColor: 'rgba(0,0,0,0)', borderUpColor: v('--candle-up'), downColor: v('--candle-down')
    });
    s.setData(bars);
    return () => chart.remove();
  }} />
```

Read the colours from the tokens at creation AND re-read them after the stylesheets land —
the library caches them, so a chart built too early paints black. Hollow closed above its
open, filled below: direction is not a result and takes no money colour.
