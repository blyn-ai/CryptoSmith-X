// Market data console. Plain DOM, no framework, no build. It is an ordinary client of the public
// API: if something is missing here, the gap is in /v1, not in a private endpoint.
(() => {
  'use strict';

  const DASH = '—';
  const REFRESH_MS = 10_000;
  const $ = (id) => document.getElementById(id);

  // ── formatting ───────────────────────────────────────────────────────────
  const has = (v) => v !== null && v !== undefined && v !== '';

  function num(v, digits = 2) {
    if (!has(v) || Number.isNaN(Number(v))) return DASH;
    return Number(v).toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
  }

  function int(v) {
    if (!has(v) || Number.isNaN(Number(v))) return DASH;
    return Number(v).toLocaleString('en-US');
  }

  function price(v) {
    if (!has(v) || Number.isNaN(Number(v))) return DASH;
    const n = Number(v);
    const digits = Math.abs(n) >= 1000 ? 2 : Math.abs(n) >= 1 ? 4 : 8;
    return n.toLocaleString('en-US', { minimumFractionDigits: digits, maximumFractionDigits: digits });
  }

  function compact(v) {
    if (!has(v) || Number.isNaN(Number(v))) return DASH;
    const n = Number(v);
    const abs = Math.abs(n);
    if (abs >= 1e9) return (n / 1e9).toFixed(2) + 'B';
    if (abs >= 1e6) return (n / 1e6).toFixed(2) + 'M';
    if (abs >= 1e3) return (n / 1e3).toFixed(1) + 'k';
    return n.toFixed(2);
  }

  function age(seconds) {
    if (!has(seconds) || Number.isNaN(Number(seconds))) return DASH;
    const s = Math.max(0, Math.round(Number(seconds)));
    if (s < 60) return s + ' s';
    if (s < 3600) return Math.floor(s / 60) + ' m';
    if (s < 86400) return Math.floor(s / 3600) + ' h';
    return Math.floor(s / 86400) + ' d';
  }

  function stamp(iso) {
    if (!has(iso)) return DASH;
    const d = new Date(iso);
    return Number.isNaN(d.getTime()) ? DASH : d.toISOString().replace('T', ' ').slice(0, 19) + 'Z';
  }

  // ── table building ───────────────────────────────────────────────────────
  function table(el, columns, rows, emptyText) {
    el.textContent = '';
    if (!rows || rows.length === 0) {
      const wrap = el.closest('.card') || el.parentElement;
      let empty = wrap.querySelector('.empty');
      if (!empty) {
        empty = document.createElement('p');
        empty.className = 'empty';
        wrap.append(empty);
      }
      empty.textContent = emptyText;
      empty.hidden = false;
      return;
    }

    const wrap = el.closest('.card') || el.parentElement;
    const empty = wrap.querySelector('.empty');
    if (empty) empty.hidden = true;

    const thead = document.createElement('thead');
    const hr = document.createElement('tr');
    for (const c of columns) {
      const th = document.createElement('th');
      th.textContent = c.label;
      if (c.align === 'right') th.style.textAlign = 'right';
      hr.append(th);
    }
    thead.append(hr);

    const tbody = document.createElement('tbody');
    for (const row of rows) {
      const tr = document.createElement('tr');
      for (const c of columns) {
        const td = document.createElement('td');
        const cell = c.cell(row);
        if (cell && typeof cell === 'object') {
          td.textContent = cell.text;
          if (cell.className) td.className = cell.className;
        } else {
          td.textContent = cell;
        }
        if (c.align === 'right') td.classList.add('num');
        if (c.name) td.classList.add('name');
        tr.append(td);
      }
      tbody.append(tr);
    }

    el.append(thead, tbody);
  }

  function kpi(label, value, note) {
    const box = document.createElement('div');
    box.className = 'kpi';
    const l = document.createElement('p'); l.className = 'kpi__label'; l.textContent = label;
    const v = document.createElement('p'); v.className = 'kpi__value'; v.textContent = value;
    box.append(l, v);
    if (note) {
      const n = document.createElement('p'); n.className = 'kpi__note'; n.textContent = note;
      box.append(n);
    }
    return box;
  }

  async function getJSON(path) {
    try {
      const r = await fetch(path, { headers: { accept: 'application/json' } });
      if (!r.ok) return null;
      const ct = r.headers.get('content-type') || '';
      if (!ct.includes('application/json')) return null;
      return await r.json();
    } catch (_) {
      return null;
    }
  }

  // ── tabs ─────────────────────────────────────────────────────────────────
  let active = 'health';
  $('tabs').addEventListener('click', (e) => {
    const btn = e.target.closest('.tab');
    if (!btn) return;
    active = btn.dataset.tab;
    for (const t of document.querySelectorAll('.tab')) t.classList.toggle('is-active', t === btn);
    for (const p of document.querySelectorAll('.panel')) {
      p.classList.toggle('is-active', p.id === 'panel-' + active);
    }
    refresh();
  });

  // ── panels ───────────────────────────────────────────────────────────────
  async function loadHealth() {
    const [health, exchanges] = await Promise.all([getJSON('/v1/health'), getJSON('/v1/exchanges')]);

    const collectors = (health && health.collectors) || [];
    const stale = (health && health.staleInstruments) || [];
    const venues = exchanges || [];

    const up = venues.filter((x) => x.isEnabled).length;
    const trading = venues.reduce((sum, x) => sum + Number(x.tradingInstruments || 0), 0);
    const oldest = collectors.reduce(
      (max, c) => (has(c.lastSuccessAgeSeconds) && c.lastSuccessAgeSeconds > max ? c.lastSuccessAgeSeconds : max), 0);

    const kpis = $('kpis');
    kpis.textContent = '';
    kpis.append(
      kpi('Exchanges enabled', int(up), venues.length ? venues.length + ' known' : null),
      kpi('Instruments trading', int(trading)),
      kpi('Stale instruments', int(stale.length), 'older than 3 snapshot intervals'),
      kpi('Oldest collector success', age(oldest)));

    $('healthState').textContent = health ? health.status : DASH;

    table($('collectors'), [
      { label: 'Exchange', name: true, cell: (r) => r.exchangeCode },
      { label: 'Collector', cell: (r) => r.collector },
      {
        label: 'State',
        cell: (r) => {
          if (!has(r.lastSuccessAt)) return { text: 'no success yet', className: 'state state--down' };
          if (r.consecutiveFailures > 0) return { text: 'failing', className: 'state state--warn' };
          return { text: 'ok', className: 'state state--ok' };
        },
      },
      { label: 'Last success', align: 'right', cell: (r) => age(r.lastSuccessAgeSeconds) + ' ago' },
      { label: 'Failures in a row', align: 'right', cell: (r) => int(r.consecutiveFailures) },
      { label: 'Instruments', align: 'right', cell: (r) => int(r.instrumentsExpected) },
      {
        // last_error is the last one ever seen, not the current state — without its age next to a
        // healthy row it reads as a live problem.
        label: 'Last error',
        cell: (r) => {
          if (!has(r.lastError)) return DASH;
          const when = has(r.lastErrorAgeSeconds) ? ' · ' + age(r.lastErrorAgeSeconds) + ' ago' : '';
          const text = (r.lastError.length > 90 ? r.lastError.slice(0, 90) + '…' : r.lastError) + when;
          return { text, className: r.consecutiveFailures > 0 ? '' : 'muted' };
        },
      },
    ], collectors, 'No collector has reported yet.');

    $('staleCard').hidden = stale.length === 0;
    $('staleCount').textContent = stale.length ? int(stale.length) : DASH;
    if (stale.length) {
      table($('stale'), [
        { label: 'Exchange', name: true, cell: (r) => r.exchangeCode },
        { label: 'Symbol', name: true, cell: (r) => r.symbol },
        { label: 'Last snapshot', align: 'right', cell: (r) => stamp(r.receivedAt) },
        { label: 'Age', align: 'right', cell: (r) => age(r.ageSeconds) },
      ], stale, 'Nothing stale.');
    }

    return health ? health.asOf : null;
  }

  async function loadInstruments() {
    const rows = await getJSON('/v1/instruments');
    const list = rows || [];
    $('instrumentCount').textContent = int(list.length);

    table($('instruments'), [
      { label: 'Exchange', name: true, cell: (r) => r.exchangeCode },
      { label: 'Symbol', name: true, cell: (r) => r.symbol },
      { label: 'Base', cell: (r) => r.baseAsset },
      { label: 'Quote', cell: (r) => r.quoteAsset },
      {
        label: 'Status',
        cell: (r) => ({
          text: r.status,
          className: r.status === 'trading' ? 'state state--ok'
            : r.status === 'delisted' ? 'state state--off' : 'state state--warn',
        }),
      },
      { label: 'Price step', align: 'right', cell: (r) => (has(r.priceStep) ? String(r.priceStep) : DASH) },
      { label: 'Qty step', align: 'right', cell: (r) => (has(r.qtyStep) ? String(r.qtyStep) : DASH) },
      { label: 'Min qty', align: 'right', cell: (r) => (has(r.minQty) ? String(r.minQty) : DASH) },
      { label: 'Min notional', align: 'right', cell: (r) => (has(r.minNotional) ? String(r.minNotional) : DASH) },
      { label: 'Mult', align: 'right', cell: (r) => (has(r.contractMultiplier) ? String(r.contractMultiplier) : DASH) },
      { label: 'Funding', align: 'right', cell: (r) => (has(r.fundingIntervalHours) ? r.fundingIntervalHours + ' h' : DASH) },
      { label: 'Last seen', align: 'right', cell: (r) => stamp(r.lastSeenAt) },
    ], list, 'Discovery has not stored an instrument yet.');

    return null;
  }

  async function loadSnapshot() {
    const data = await getJSON('/v1/snapshot');
    const rows = (data && data.tickers) || [];
    $('snapshotCount').textContent = int(rows.length);

    table($('snapshot'), [
      { label: 'Symbol', name: true, cell: (r) => r.symbol },
      { label: 'Last', align: 'right', cell: (r) => price(r.lastPrice) },
      { label: 'Bid', align: 'right', cell: (r) => price(r.bidPrice) },
      { label: 'Ask', align: 'right', cell: (r) => price(r.askPrice) },
      { label: 'Spread bps', align: 'right', cell: (r) => num(r.spreadBps, 1) },
      { label: 'Mark', align: 'right', cell: (r) => price(r.markPrice) },
      { label: 'Index', align: 'right', cell: (r) => price(r.indexPrice) },
      {
        label: 'Funding',
        align: 'right',
        cell: (r) => {
          if (!has(r.fundingRate)) return DASH;
          const pct = Number(r.fundingRate) * 100;
          return { text: (pct > 0 ? '+' : '') + pct.toFixed(4) + '%', className: pct >= 0 ? 'up' : 'down' };
        },
      },
      { label: 'OI', align: 'right', cell: (r) => compact(r.openInterest) },
      { label: 'OI notional', align: 'right', cell: (r) => compact(r.openInterestNotional) },
      { label: 'Turnover 24h', align: 'right', cell: (r) => compact(r.turnover24h) },
      { label: 'Depth 25 bps', align: 'right', cell: (r) => compact(r.depthBid25Bps) },
      { label: 'Age', align: 'right', cell: (r) => age(r.ageSeconds) },
    ], rows, 'No snapshot has been stored yet.');

    return data ? data.asOf : null;
  }

  async function refresh() {
    const asOf = active === 'health' ? await loadHealth()
      : active === 'instruments' ? await loadInstruments()
        : await loadSnapshot();
    $('asOf').textContent = has(asOf) ? 'As of ' + stamp(asOf) : DASH;
  }

  refresh();
  setInterval(refresh, REFRESH_MS);
})();
