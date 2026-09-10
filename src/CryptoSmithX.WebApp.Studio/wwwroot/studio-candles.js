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
// ── BOTH BODIES ARE FILLED, AND THAT IS THE ONE EXCEPTION ──
// This header said the opposite of the file under it for two commits: "hollow above the open,
// filled below — upColor is fully transparent". RULE-CHANGES entry 10 filled both bodies, the code
// sixty lines down was changed and this paragraph was not, so the first thing anyone opening the
// file read was a description of a chart it does not draw. The argument is in candleOptions() and
// in readme rule 5; the boundary of the exception — the body of a candle and nothing else, not a
// bar, not a chip, not a mark and not text — is in RULE-CHANGES entry 10 and pinned by
// DesignSystemTests at both ends.
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

  const v = (name) => getComputedStyle(document.documentElement).getPropertyValue(name).trim();

  // P0-2 (UX audit): the axis used to print "12:00" and nothing else, on a panel that can span a
  // day or more — two ticks twelve hours apart printed identical text. tickMarkType is the
  // library's OWN classification of what kind of boundary a tick sits on; DayOfMonth/Month/Year
  // are the ones it draws when a tick crosses midnight, so those get the date. firstTime (this
  // chart's own leftmost bar) gets it too, for a short panel that never crosses one.
  const MONTHS = ['Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun', 'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec'];
  const tickMarkFormatter = (firstTime) => (time, tickMarkType) => {
    const d = new Date(time * 1000);
    const hhmm = d.toISOString().slice(11, 16);
    const boundary = tickMarkType === LightweightCharts.TickMarkType.DayOfMonth
      || tickMarkType === LightweightCharts.TickMarkType.Month
      || tickMarkType === LightweightCharts.TickMarkType.Year;
    if (boundary || time === firstTime) {
      return String(d.getUTCDate()).padStart(2, '0') + ' ' + MONTHS[d.getUTCMonth()] + ' ' + hhmm;
    }
    return hhmm;
  };

  const chartOptions = (firstTime) => ({
    autoSize: true,
    layout: {
      background: { color: 'transparent' },
      textColor: v('--text-faint'),
      fontFamily: v('--font-mono'),
      fontSize: 10,
      // Off HERE because the attribution is met ONCE in the footer — see _Layout.cshtml, where
      // the link and the reasoning live. This is not "we removed the logo"; it is "the licence
      // asks for a link on the page, and the page has one". Turning this back on is fine and
      // costs nothing but five brand marks; removing the footer line without turning this on is
      // a licence breach, and DesignSystemTests fails the build if that happens.
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
      tickMarkFormatter: tickMarkFormatter(firstTime),
    },
    crosshair: { mode: LightweightCharts.CrosshairMode.Normal },
  });

  const candleOptions = () => ({
    // BOTH BODIES ARE FILLED. This is the one place on the surface where a body is painted
    // solid to say direction, and it is an exception taken deliberately rather than a rule
    // being satisfied — see readme rule 5 and RULE-CHANGES entry 10.
    //
    // The hollow-above-open form it replaces was correct about the principle and wrong about
    // the object. A candle is not a figure in a column: it has no age line, no mark slot, no
    // neighbour to be ranked against. Its two inks are a shape's own vocabulary, read the way
    // every candle chart in the world is read, and asking the reader to decode a house
    // convention there buys nothing the palette needed protecting from.
    upColor: v('--candle-up'),
    downColor: v('--candle-down'),
    borderUpColor: v('--candle-up'),
    borderDownColor: v('--candle-down'),
    wickUpColor: v('--candle-up'),
    wickDownColor: v('--candle-down'),
    borderVisible: true,
    priceLineVisible: false,
    lastValueVisible: false,
  });

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // THE OHLC LINE
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // A candle is four figures and a claim about an hour, and until now this page drew the shape and
  // printed none of the numbers. The line above each panel is where they go — the arrangement
  // Kraken, TradingView and this library's own documented legend example all use — and it is
  // SERVER-RENDERED on the last closed bar before this file is reached, so a reader with no script,
  // no pointer or a printed page still has the four figures and the hour they belong to. All this
  // does is move them to whatever bar the crosshair is over, and put them back when it leaves.
  //
  // REJECTED: a tooltip. It appears over the candles it describes, so the reader cannot see the
  // numbers and the shape they came from at once; it exists only while a pointer is on the chart,
  // so there is nothing to read without one; and it would have been the only thing on this surface
  // that states a figure and cannot be reached with scripts off.
  //
  // ── ONE CROSSHAIR, EVERY LINE ──
  // Pointing at a bar on ONE panel moves the line on ALL of them, and that is the whole reason this
  // is a page of stacked panels rather than five charts that happen to be adjacent. The time axes
  // are tied (below), so the same hour already sits at the same x on every panel; the panels share
  // one price scale per quote; the header calls them a comparison. If only the hovered panel
  // followed the pointer, the other four would go on stating their own last closed hour in a line
  // that looks identical to the one that moved — and a reader comparing Binance at 03:00 against
  // the line above the next chart would be reading 03:00 against 12:00 with nothing to say so. That
  // is the exact defect the comparison page was fixed for once already. Every line carries its own
  // hour, so the five lines changing together are five lines saying which hour they now mean.
  //
  // The hovered panel takes the SAME path as its neighbours — look the hour up in this panel's own
  // rows — rather than reading `param.seriesData`. Two paths would be two chances for the panel
  // under the pointer to disagree with the four beside it about which bar it is showing.
  //
  // REJECTED: painting a crosshair on the other four with setCrosshairPosition. It needs a price to
  // put the horizontal line at, which would be a y-position invented for a pointer that is not
  // there, and it feeds straight back into this subscription. There is one pointer; the tie is
  // stated in the hour, which every line now prints.
  //
  // Every value goes into a counted slot (see --ohlc-n and --ohlc-chg-n in studio.css), so this
  // writes text into fixed fields and never changes the line's shape. And it writes only what
  // changed, for the same reason studio-ages.js does: crosshair moves arrive per pointer event,
  // many of them inside one bar, and rewriting five lines of identical strings per event is layout
  // work for nothing.
  const setText = (el, text) => {
    if (el && el.textContent !== text) el.textContent = text;
  };

  // Format.Num's N-decimals, invariant, so the figures in this line are the same strings the same
  // prices take in the table above it.
  const num = (value, decimals) => (value === null || value === undefined || !Number.isFinite(value)
    ? '—'
    : value.toLocaleString('en-US', { minimumFractionDigits: decimals, maximumFractionDigits: decimals }));

  // Format.SignedPercent at OhlcLine.ChangeDecimals. The sign is always shown: on a move the
  // direction IS the reading, and a bare number hides it — and here it is the only thing carrying
  // direction, because the change takes no ink (rule 5; see .a-ohlc-chg).
  const signedPercent = (fraction) => (fraction === null || !Number.isFinite(fraction)
    ? '—'
    : (fraction > 0 ? '+' : '')
      + (fraction * 100).toLocaleString('en-US', { minimumFractionDigits: 2, maximumFractionDigits: 2 }) + '%');

  // The hour the bar covers, in UTC, spelled the way the server spelled it. Deliberately not the
  // reader's local time: the axis under it, the stamps in the header and every instant this page
  // holds are UTC, and one line in a second zone would be the only figure on the surface that means
  // something different from its neighbours.
  // "dd HH:mm UTC", matching Models/OhlcLine.Hour character for character — the server renders the
  // resting label and this renders every other one, and two renderers printing one field must not
  // disagree about its shape. The day is load-bearing, not ornament: a panel holds twenty-five
  // hourly windows, so the first and the last are the same clock hour a day apart and "HH:mm UTC"
  // names both of them identically.
  const hourText = (time) => {
    const iso = new Date(time * 1000).toISOString();
    return iso.slice(8, 10) + ' ' + iso.slice(11, 16) + ' UTC';
  };

  const legendOf = (panel) => {
    const line = panel.querySelector('[data-ohlc]');
    return line === null ? null : {
      when: line.querySelector('[data-ohlc-when]'),
      o: line.querySelector('[data-ohlc-o]'),
      h: line.querySelector('[data-ohlc-h]'),
      l: line.querySelector('[data-ohlc-l]'),
      c: line.querySelector('[data-ohlc-c]'),
      chg: line.querySelector('[data-ohlc-chg]'),
    };
  };

  const showBar = (legend, bar, decimals) => {
    // An hour with no bar is four em dashes and no per-cent, never four zeros: rule 8, and the
    // whole reason the gap is on the axis at all. A zero would say this venue traded at nothing.
    const missing = !bar || bar.o === null || bar.o === undefined;
    setText(legend.when, bar ? hourText(bar.time) : '—');
    setText(legend.o, missing ? '—' : num(bar.o, decimals));
    setText(legend.h, missing ? '—' : num(bar.h, decimals));
    setText(legend.l, missing ? '—' : num(bar.l, decimals));
    setText(legend.c, missing ? '—' : num(bar.c, decimals));

    // Close against that same bar's own open — OhlcLine.ChangeOf, and no guard for an open of zero
    // for the same reason it has none: the division produces an infinity, and signedPercent above
    // answers that with the dash. One rule for "not measured", in one place, on both sides.
    setText(legend.chg, signedPercent(missing ? null : (bar.c - bar.o) / bar.o));
  };

  // ── RE-INITIALISABLE, FOR THE ONE THING ON THIS PAGE THAT SWAPS ITS OWN CHART ELEMENTS ──
  // B3's timeframe/series selector replaces .v2-cuts's markup with a fresh set of .a-chart
  // elements rather than navigating — see studio-v2-band3.js — and this file only ran once, at
  // script load, on the elements present then. A chart library does not notice new DOM on its
  // own; nothing here had ever needed it to before.
  //
  // Disposing the PREVIOUS set before building the new one is not optional: a chart instance
  // whose host element was just removed from the DOM (by that same innerHTML swap) still holds a
  // ResizeObserver on it, and re-running this function without disposing first would pile one more
  // orphaned observer and one more `csx-studio-theme` listener onto the page per switch.
  let activeCharts = [];
  let activeRepaint = null;

  function initCandlePanels() {
    activeCharts.forEach((c) => {
      try { c.remove(); } catch (e) { /* host already gone; nothing to clean up */ }
    });
    activeCharts = [];

    if (activeRepaint) {
      document.removeEventListener('csx-studio-theme', activeRepaint);
      activeRepaint = null;
    }

    const els = [...document.querySelectorAll('.a-chart[data-candles]')];
    if (!els.length || !window.LightweightCharts) return;

  // One entry per panel that has a line above it: the line's six fields, this panel's own bars by
  // hour, the hour it rests on, and the tick it prints to.
  const panels = [];

  const made = els.map((el) => {
    const rows = JSON.parse(el.dataset.candles);
    const lo = Number(el.dataset.scaleLo);
    const hi = Number(el.dataset.scaleHi);
    const decimals = Number(el.dataset.decimals);

    const chart = LightweightCharts.createChart(el, {
      ...chartOptions(rows.length > 0 ? rows[0].time : undefined),
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

    // The line above this panel, and the bar it falls back to. `resting` is the last CLOSED bar the
    // server printed — recomputed here from the same rows rather than read back out of the DOM, so
    // the two halves cannot disagree about which bar that is (OhlcLine.Build walks the same list
    // from the same end).
    const legend = legendOf(el.closest('.a-chart-panel') || el.parentElement);
    if (legend !== null) {
      let resting = null;
      for (let i = rows.length - 1; i >= 0; i--) {
        if (rows[i].o !== null && rows[i].o !== undefined) { resting = rows[i]; break; }
      }

      // Keyed by hour because that is what the crosshair reports and what every panel is asked for.
      // A panel with no row at that hour answers with dashes, which is the honest answer: this
      // venue has no bar there, and the gap is already drawn on its axis.
      panels.push({ legend, decimals, resting, byHour: new Map(rows.map((r) => [r.time, r])) });
    }

    return { chart, series };
  });

  // ── THE HOUR EVERY LINE IS SHOWING ──
  // `null` means "each line is on its own resting bar", which is the state the server rendered, so
  // this starts there and a leave event before any move is correctly a no-op. Held as one value for
  // the page rather than one per panel because there is one pointer and one answer.
  let reported = null;

  const report = (hour) => {
    if (hour === reported) return;
    reported = hour;
    for (const p of panels) {
      showBar(p.legend, hour === null ? p.resting : (p.byHour.get(hour) || { time: hour, o: null }),
        p.decimals);
    }
  };

  // The crosshair carries the hour under it; leaving the chart hands back an event with no time,
  // which is what puts the resting bars back. Not `mouseleave` on the element: the library already
  // reports the pointer leaving its own plot area, and a second source would disagree with it at
  // the axis margins.
  if (panels.length > 0) {
    made.forEach(({ chart }) => {
      chart.subscribeCrosshairMove((param) => {
        report(!param || param.time === undefined || param.time === null ? null : param.time);
      });
    });
  }

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
  // register flip has to reach in and push them back. studio-ages.js fires this after it moves
  // data-theme; a MutationObserver would work too and would also fire for every other attribute
  // anyone ever adds to <html>.
    const repaint = () => made.forEach(({ chart, series }) => {
      chart.applyOptions(chartOptions());
      series.applyOptions(candleOptions());
    });

    activeCharts = made.map(({ chart }) => chart);
    activeRepaint = repaint;
    document.addEventListener('csx-studio-theme', repaint);
  }

  initCandlePanels();

  // Re-invoked from studio-v2-band3.js after it swaps .v2-cuts's markup for a new timeframe or
  // series. Not a MutationObserver: that would fire for every attribute change anywhere on the
  // page, for a need that has exactly one caller and one moment it ever fires.
  window.CSXInitCandles = initCandlePanels;

  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // HOW MANY PANELS SIT SIDE BY SIDE
  // ─────────────────────────────────────────────────────────────────────────────────────────────
  // The panels share one price scale per quote asset, and a shared scale is only legible when the
  // panels are STACKED: one column puts the same price at the same height on the page, and that is
  // the comparison the shared scale exists to make. Side by side, the reader compares two shapes at
  // two different heights and does the arithmetic themselves. So the control is not a density
  // preference, it is the switch between "look at five markets" and "compare two of them".
  //
  // It lives here rather than in a file of its own because it arranges these instances and nothing
  // else, and because it inherits this file's one precondition for free: no library, no panels
  // drawn, nothing to arrange. Reflowing five empty boxes is not a feature.
  //
  // The CHART instances need nothing from it. Lightweight Charts is created with autoSize, which is
  // a ResizeObserver on the container, so a column change resizes every panel and the tie between
  // their time axes is re-asserted by the same subscription that ties them when a reader pans one.
  // fitContent is deliberately NOT called: a reader who has zoomed into four hours and then stacks
  // the panels to compare them asked for a layout, not for their zoom to be thrown away.
  const grid = document.querySelector('.a-charts');
  const cols = document.querySelector('.a-cols');

  if (grid && cols) {
    const buttons = [...cols.querySelectorAll('.a-cols-btn[data-cols]')];
    const KEY = 'csx-studio-cols';

    // data-cols is a CEILING; studio.css keeps a readable minimum panel width in the same
    // declaration, so this never has to know how wide the window is and there is no resize listener
    // here to disagree with the stylesheet about it.
    const apply = (n) => {
      grid.dataset.cols = String(n);
      for (const b of buttons) {
        b.setAttribute('aria-pressed', b.dataset.cols === String(n) ? 'true' : 'false');
      }
    };

    let chosen = null;

    try {
      const stored = localStorage.getItem(KEY);
      if (buttons.some((b) => b.dataset.cols === stored)) chosen = Number(stored);
    } catch (e) { /* Private mode refuses storage. The measurement below is a fine answer. */ }

    // No stored choice: the control adopts the layout the page is ALREADY drawing, by counting the
    // tracks the stylesheet's auto-fit landed on. Picking a number instead — three, say — would
    // reflow the page on load for every reader who has never touched the control, and starting with
    // no button pressed would be a control that shows no state on a page whose subject is state.
    if (chosen === null) {
      const tracks = getComputedStyle(grid).gridTemplateColumns.split(' ').filter(Boolean).length;
      chosen = Math.min(Math.max(tracks, 1), 3);
    }

    apply(chosen);
    cols.hidden = false;

    for (const b of buttons) {
      b.addEventListener('click', () => {
        apply(Number(b.dataset.cols));
        try {
          // Written only because the visitor pressed the button, like the register flip. Nothing is
          // stored for a reader who never asks for anything, and what is stored is one small number
          // about this browser's own window — not about them.
          localStorage.setItem(KEY, b.dataset.cols);
        } catch (e) { /* Private mode. The choice holds for this page and is not remembered. */ }
      });
    }
  }
})();
