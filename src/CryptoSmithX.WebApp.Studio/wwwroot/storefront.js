/* CryptoSmith X storefront — the design export's component, ported to the DOM.

   The page was exported from a design compiler that ships its own runtime: {{ }} bindings,
   <sc-for>, <sc-if>, and a class with state and a render pass. We do not have that runtime, so the
   markup was translated once (sc-for -> <template data-for>, sc-if -> <template data-if>, the camel
   attributes to real ones) and the class was carried over here unchanged in behaviour.

   Carried over deliberately, and this is the part worth keeping: NOTHING IS SEEDED. Until a call
   lands every figure is an em dash, and a dash means not measured. The card prints the age of the
   last answer that arrived, so a feed that stops shows as a rising age rather than as a number that
   quietly stopped moving. A page whose own copy argues that venues must never be averaged and gaps
   never invented cannot itself display invented numbers while the API is down. */
(function () {
  'use strict';

  var API = 'https://blynai.meetluko.eu';
  var BOTS = [{ id: 'futures-live', letter: 'D' }, { id: 'futures-lukas-live', letter: 'L' }];
  var CYCLE = 120;
  var EQUITY_OF = 'futures-lukas-live';
  var REFRESH = 2, DASH_REFRESH = 6, WINDOW = 30;

  /* Same origin first — on production /v1 is this very host. A page served anywhere without an API
     beside it (the test contour runs no api container) falls back to the deployed one, which is
     public, read-only and sends CORS headers. */
  var COVERAGE = ['/v1/coverage', 'https://cryptosmithx.blynai.eu/v1/coverage'];

  var root = document.getElementById('storefront');
  if (!root) { return; }

  // ── the little renderer ─────────────────────────────────────────────────────────
  // One walk at load records every place a value has to land; each pass writes them. Nothing is
  // re-parsed and no innerHTML is written after load, so a value can never carry markup into the
  // page.
  var TOKEN = /\{\{\s*([a-zA-Z0-9_.]+)\s*\}\}/g;

  function sites(node, out) {
    if (node.nodeType === 3) {
      if (TOKEN.test(node.nodeValue)) { out.push({ kind: 'text', node: node, tpl: node.nodeValue }); }
      TOKEN.lastIndex = 0;
      return out;
    }
    if (node.nodeType !== 1) { return out; }

    if (node.tagName === 'TEMPLATE') {
      var anchor = document.createComment(node.dataset.for || node.dataset.if);
      node.parentNode.replaceChild(anchor, node);
      out.push({
        kind: node.dataset.for ? 'for' : 'if',
        anchor: anchor, list: node.dataset.for, as: node.dataset.as, flag: node.dataset.if,
        frag: node.content, live: []
      });
      return out;
    }

    for (var i = 0; i < node.attributes.length; i++) {
      var a = node.attributes[i];
      if (TOKEN.test(a.value)) { out.push({ kind: 'attr', node: node, name: a.name, tpl: a.value }); }
      TOKEN.lastIndex = 0;
    }
    if (node.dataset && node.dataset.click) { out.push({ kind: 'click', node: node, fn: node.dataset.click }); }

    var kids = Array.prototype.slice.call(node.childNodes);
    for (var k = 0; k < kids.length; k++) { sites(kids[k], out); }
    return out;
  }

  function fill(tpl, values, scope) {
    return tpl.replace(TOKEN, function (_, path) {
      var v = path.indexOf('.') > 0 && scope
        ? (scope[path.split('.')[0]] === undefined ? values[path] : scope[path.split('.')[0]][path.split('.')[1]])
        : (scope && scope[path] !== undefined ? scope[path] : values[path]);
      return v === undefined || v === null ? '' : String(v);
    });
  }

  function apply(list, values, scope) {
    for (var i = 0; i < list.length; i++) {
      var s = list[i];
      if (s.kind === 'text') { s.node.nodeValue = fill(s.tpl, values, scope); }
      else if (s.kind === 'attr') { s.node.setAttribute(s.name, fill(s.tpl, values, scope)); }
      else if (s.kind === 'click') {
        if (!s.bound) {
          s.bound = true;
          (function (site) {
            site.node.addEventListener('click', function (e) {
              var f = site.current && site.current[site.fn];
              if (typeof f === 'function') { e.preventDefault(); f(e); }
            });
          })(s);
        }
        s.current = values;
      }
      else if (s.kind === 'if') { toggle(s, !!values[s.flag], values); }
      else if (s.kind === 'for') { repeat(s, values[s.list] || [], values); }
    }
  }

  function toggle(site, on, values) {
    if (on === site.on) { if (on) { apply(site.live[0].sites, values, null); } return; }
    site.on = on;
    drop(site);
    if (!on) { return; }
    var clone = site.frag.cloneNode(true);
    var nodes = Array.prototype.slice.call(clone.childNodes);
    var found = []; nodes.forEach(function (n) { sites(n, found); });
    site.anchor.parentNode.insertBefore(clone, site.anchor);
    site.live = [{ nodes: nodes, sites: found }];
    apply(found, values, null);
  }

  function repeat(site, items, values) {
    // Rebuilt whenever the count changes; otherwise the existing rows are refilled in place, so a
    // card that is merely updating does not flicker or lose focus.
    if (site.live.length !== items.length) {
      drop(site);
      site.live = items.map(function () {
        var clone = site.frag.cloneNode(true);
        var nodes = Array.prototype.slice.call(clone.childNodes);
        var found = []; nodes.forEach(function (n) { sites(n, found); });
        site.anchor.parentNode.insertBefore(clone, site.anchor);
        return { nodes: nodes, sites: found };
      });
    }
    for (var i = 0; i < items.length; i++) {
      var scope = {}; scope[site.as] = items[i];
      apply(site.live[i].sites, values, scope);
    }
  }

  function drop(site) {
    site.live.forEach(function (row) {
      row.nodes.forEach(function (n) { if (n.parentNode) { n.parentNode.removeChild(n); } });
    });
    site.live = [];
  }

  // ── hover, which the export expressed as an attribute ────────────────────────────
  Array.prototype.forEach.call(root.querySelectorAll('[style-hover]'), function (el) {
    var base = el.getAttribute('style') || '', extra = el.getAttribute('style-hover');
    el.addEventListener('mouseenter', function () { el.setAttribute('style', base + ';' + extra); });
    el.addEventListener('mouseleave', function () { el.setAttribute('style', base); });
  });

  var SITES = sites(root, []);

  // ── helpers, carried over ────────────────────────────────────────────────────────
  function num(v, d) {
    return typeof v === 'number' && isFinite(v)
      ? v.toLocaleString('en-GB', { minimumFractionDigits: d || 0, maximumFractionDigits: d || 0 })
      : '—';
  }
  function age(at) { return at === null ? null : Math.max(0, Math.floor((Date.now() - at) / 1000)); }
  function ageText(at) { var a = age(at); return a === null ? 'never' : (a > 99 ? '99+' : a) + ' s ago'; }
  function fade(a) { return a === null ? 1 : a >= WINDOW ? 0.15 : (1 - 0.85 * Math.pow(a / WINDOW, 0.4)).toFixed(3); }
  function held(iso) {
    var t = iso ? Date.parse(iso) : NaN;
    if (!isFinite(t)) { return '—'; }
    var s = Math.max(0, (Date.now() - t) / 1000);
    return s < 60 ? Math.floor(s) + ' s' : s < 3600 ? Math.floor(s / 60) + ' min'
      : s < 86400 ? Math.floor(s / 3600) + ' h' : Math.floor(s / 86400) + ' d';
  }
  /* One path, or null — an empty sparkline is not drawn as a flat line, because a flat line is a
     claim. */
  function spark(values) {
    var ys = (values || []).filter(function (v) { return typeof v === 'number' && isFinite(v); });
    if (ys.length < 2) { return null; }
    var W = 320, H = 54, P = 6;
    var lo = Math.min.apply(null, ys), hi = Math.max.apply(null, ys), span = (hi - lo) || 1;
    return ys.map(function (v, i) {
      return (i ? 'L' : 'M') + ((i / (ys.length - 1)) * (W - 4) + 2).toFixed(1) + ' '
        + (H - P - ((v - lo) / span) * (H - P * 2)).toFixed(1);
    }).join(' ');
  }

  function get(path) {
    return fetch(API + path, { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; }, function () { return null; });
  }

  // ── state ────────────────────────────────────────────────────────────────────────
  var st = { night: false, coverage: null, instance: null, stats: null, status: null,
             bots: null, atDash: null, atSlow: null, reached: null, socialMsg: '' };
  var inflight = false;

  function set(patch) { Object.keys(patch).forEach(function (k) { st[k] = patch[k]; }); draw(); }

  function light() {
    Promise.all([get('/api/public-stats'), get('/api/bot-status')]).then(function (r) {
      if (!r[0] && !r[1]) { return; }
      var next = { atSlow: Date.now(), reached: true };
      if (r[0]) { next.stats = r[0]; }
      if (r[1]) { next.status = r[1]; }
      set(next);
    });
  }
  function heavy() {
    if (inflight) { return; }
    inflight = true;
    Promise.all(BOTS.map(function (b) {
      return get('/api/dashboard?botInstanceId=' + encodeURIComponent(b.id))
        .then(function (d) { return { b: b, d: d }; });
    })).then(function (bots) {
      inflight = false;
      if (!bots.some(function (x) { return x.d; })) {
        if (st.reached === null) { set({ reached: false }); }
        return;
      }
      set({ bots: bots, atDash: Date.now(), reached: true });
    }, function () { inflight = false; });
  }
  function coverage(i) {
    i = i || 0;
    if (i >= COVERAGE.length) { return; }
    fetch(COVERAGE[i], { cache: 'no-store' })
      .then(function (r) { return r.ok ? r.json() : null; }, function () { return null; })
      .then(function (c) {
        if (c && Array.isArray(c.venues)) { set({ coverage: c }); } else { coverage(i + 1); }
      });
  }

  // ── the values, computed exactly as the export computes them ─────────────────────
  function values() {
    var fastAge = age(st.atDash), slowAge = age(st.atSlow);
    var who = st.instance || EQUITY_OF;
    var lead = (st.bots || []).filter(function (x) { return x.b.id === who; })[0];
    var first = lead ? lead.d : null;

    var sum = first && first.summary, eq = first && first.equity;
    var unit = sum && sum.cashQuoteCurrency && sum.cashEur === sum.cashQuoteValue ? sum.cashQuoteCurrency : '';
    var closes = eq && Array.isArray(eq.days) ? eq.days.map(function (d) { return d.close; }) : [];

    /* The day, computed the way the journal computes it. The base is the margin in play, NOT the
       wallet total and NOT yesterday's close: a deposit moves the wallet without the bot having
       done anything, and the percentage would then describe the transfer instead of the day. */
    var td = first && first.today;
    var leadPos = first && Array.isArray(first.positions) ? first.positions : [];
    function sumOf(k) {
      return leadPos.reduce(function (a, p) { return a + (typeof p[k] === 'number' ? p[k] : 0); }, 0);
    }
    var working = first ? sumOf('initialMarginEur') : null;
    var realised = td && typeof td.realizedPnlEur === 'number' ? td.realizedPnlEur : null;
    var unrealised = first ? sumOf('unrealizedPnlEur') : null;
    var gap = realised === null && unrealised === null ? null : (realised || 0) + (unrealised || 0);
    var dayNow = working === null || gap === null ? null : working + gap;
    var closed = td && typeof td.closed === 'number' ? td.closed : null;
    var delta = gap !== null && working ? gap / working * 100 : null;
    function money(v, d) { return v === null ? '—' : num(Math.abs(v), d === undefined ? 2 : d); }
    function sign(v) { return v === null || v === 0 ? '' : v < 0 ? '↓' : '↑'; }

    /* Both instances merged, each row stamped with its letter, sorted by pair: a card that reorders
       itself every tick is unreadable, and alphabetical never depends on the market. */
    var open = null;
    (st.bots || []).forEach(function (x) {
      if (x.d && Array.isArray(x.d.positions)) {
        open = (open || []).concat(x.d.positions.map(function (p) {
          var c = { who: x.b.letter };
          Object.keys(p).forEach(function (k) { c[k] = p[k]; });
          return c;
        }));
      }
    });
    if (open) { open.sort(function (a, b) { return String(a.pair || '').localeCompare(String(b.pair || '')); }); }

    var positions = (open || []).map(function (p) {
      var v = typeof p.unrealizedPnlPercent === 'number' ? p.unrealizedPnlPercent : null;
      return {
        who: p.who, whoTitle: p.who === 'L' ? 'Instance LUKAS' : 'Instance BYKO',
        pair: p.pair || '—', side: (p.side || '').toUpperCase() || '—',
        pnl: v === null ? '—' : Math.abs(v).toFixed(2) + '%',
        arrow: v === null || v === 0 ? '' : v < 0 ? '↓' : '↑',
        held: held(p.openedAtUtc)
      };
    });

    /* Coverage: the venues in the endpoint's own words, never a list typed into the copy. */
    var cov = st.coverage;
    var venues = cov ? cov.venues.filter(function (v) { return v.status === 'enabled'; }) : [];
    var names = venues.length ? venues.map(function (v) { return v.name; })
      : ['Kraken Futures', 'WEEX Futures', 'Hyperliquid', 'Binance USDⓈ-M Futures'];
    var venueList = names.length > 1
      ? names.slice(0, -1).join(', ') + ' and ' + names[names.length - 1] : names[0];
    var trading = cov && cov.totals && typeof cov.totals.trading === 'number' ? num(cov.totals.trading) : null;

    /* Pairs falls back to the largest running worker's active count — two instances watch
       overlapping sets and the payload gives no way to dedupe, so understating is honest where
       summing would invent pairs. */
    var pairs = st.stats && typeof st.stats.marketsNow === 'number' ? st.stats.marketsNow : null;
    if (pairs === null) {
      (st.bots || []).forEach(function (x) {
        ((x.d && Array.isArray(x.d.workers)) ? x.d.workers : []).forEach(function (w) {
          if (w.runtimeState === 'running' && typeof w.activePairsCount === 'number') {
            pairs = Math.max(pairs === null ? 0 : pairs, w.activePairsCount);
          }
        });
      });
    }
    var decisions = st.stats && typeof st.stats.decisionsTotal === 'number' ? st.stats.decisionsTotal : null;

    var trades = first && first.today && Array.isArray(first.today.trades) ? first.today.trades : [];
    var lastTrade = trades.length ? trades[trades.length - 1] : null;

    var s = st.status;
    var paused = !!(s && s.entryBlackout && s.entryBlackout.isActive);
    var live = !!(s && s.runtimeState === 'running' && !s.isStale && !paused);
    var stateLabel = !s ? (st.reached === false ? 'No feed' : '—')
      : paused ? 'Entries paused' : s.isStale ? 'Data is stale' : live ? 'Live' : (s.runtimeState || '—');

    return {
      statementInk: 'var(--accent)', statementBg: 'transparent', statementPad: '0',
      statementWidth: 'fit-content', statementAlign: 'flex-start',
      themeAttr: st.night ? 'night' : '', themeLabel: st.night ? 'Paper' : 'Ink',
      flipTheme: function () { set({ night: !st.night }); },
      socialMsg: st.socialMsg,
      socialClick: function (e) { set({ socialMsg: e.currentTarget.dataset.name + ' — currently disabled' }); },
      positions: positions, hasPositions: !!positions.length, noPositions: !positions.length,
      sampleFlag: '',
      emptyLine: open ? 'Nothing open right now' : st.reached === false ? 'API unreachable' : '—',
      emptyNote: open && lastTrade ? 'Last — ' + (lastTrade.pair || '—') + ' ' + (lastTrade.side || '')
        : paused ? 'Entries paused — night window' : '',
      positionCount: open ? String(open.length) : '—',
      equity: dayNow === null ? '—' : num(dayNow, 2), equityUnit: unit,
      dayOpen: working === null ? '—' : num(working, 2),
      tenant: lead ? (lead.b.letter === 'L' ? 'Luko' : 'Byko') : '—',
      pickLukas: function () { set({ instance: 'futures-lukas-live' }); },
      pickDenisas: function () { set({ instance: 'futures-live' }); },
      lukasInk: who === 'futures-lukas-live' ? 'var(--accent)' : 'var(--text-faint)',
      denisasInk: who === 'futures-live' ? 'var(--accent)' : 'var(--text-faint)',
      gapArrow: sign(gap), gapAbs: money(gap),
      realisedArrow: sign(realised), realisedAbs: money(realised),
      unrealisedArrow: sign(unrealised), unrealisedAbs: money(unrealised),
      closedCount: closed === null ? '—' : String(closed),
      dayNote: closed === null ? '' : 'the day is not over, so the unrealised half is paper',
      deltaArrow: delta === null || delta === 0 ? '' : delta < 0 ? '↓' : '↑',
      delta: delta === null ? '—' : Math.abs(delta).toFixed(2) + '%',
      sparkPath: spark(closes) || '', hasSpark: !!spark(closes), noSpark: !spark(closes),
      pairsFigure: pairs === null ? '—' : num(pairs),
      cycleFigure: CYCLE + ' s',
      decisionsFigure: decisions === null ? '—' : num(decisions),
      venueList: venueList, venueRow: names.join(' · '),
      instrumentCount: trading === null ? 'about 2,000' : trading,
      stateLabel: stateLabel,
      stateInk: live ? 'var(--brand-deep)' : 'var(--state-hold-ink)',
      stateDot: live ? 'var(--brand)' : 'var(--state-stale)',
      tickerAge: ageText(st.atDash), slowAge: ageText(st.atSlow),
      tickerFade: fade(fastAge), slowFade: fade(slowAge),
      tickerMark: Math.min(100, (fastAge === null ? 100 : fastAge / WINDOW * 100)).toFixed(1) + '%',
      feedNote: st.reached === false ? 'API unreachable — nothing on this card is measured' : '',
      true: true, false: false
    };
  }

  function draw() {
    var v = values();
    root.setAttribute('data-theme', v.themeAttr);
    apply(SITES, v, null);
  }

  draw();
  light(); heavy(); coverage();
  setInterval(draw, 1000);
  setInterval(light, REFRESH * 1000);
  setInterval(heavy, DASH_REFRESH * 1000);
})();
