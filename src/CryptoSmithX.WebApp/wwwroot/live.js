// Live updates: push where the page opts in (Server-Sent Events, one server-rendered
// fragment per [data-live-region]), the 10 s refetch-and-morph poll everywhere else and
// as the fallback when push is not open. Two update paths share one rule: never touch
// what the user is doing. Guards, unchanged from the poll-only version this replaced:
//   * a dirty form (the user typed into one) stops ALL updates until saved or left;
//   * focus inside a field skips this tick/fragment;
//   * a hidden tab updates nothing;
//   * a modal (body.has-modal) pauses updates, same reason as a dirty form.
// Fragments and subtrees marked data-live-skip (forms, dialogs, the Lifecycle panel) are
// never touched by either path — that is the user's or a library's territory.
(() => {
  'use strict';
  let dirty = false;
  document.addEventListener('input', (e) => { if (e.target.closest('form')) dirty = true; });

  function blocked() {
    if (document.hidden || dirty || document.body.classList.contains('has-modal')) return true;
    const ae = document.activeElement;
    return !!(ae && ae.matches('input, select, textarea'));
  }

  const INTERVAL = 10000;
  let pushOpen = false;

  setInterval(() => {
    if (pushOpen || blocked()) return;
    refresh();
  }, INTERVAL);

  async function refresh() {
    try {
      const r = await fetch(location.href, { headers: { 'X-Live': '1' } });
      if (!r.ok) return;
      const doc = new DOMParser().parseFromString(await r.text(), 'text/html');
      const next = doc.querySelector('main.main');
      const cur = document.querySelector('main.main');
      if (next && cur) morphChildren(cur, next);
      const nav = doc.querySelector('.nav'), curNav = document.querySelector('.nav');
      if (nav && curNav) morphChildren(curNav, nav); // badges stay honest
    } catch { /* transient network blip — next tick retries */ }
  }

  // Push: only on pages that opted in by rendering at least one [data-live-region] — today
  // that is the exchange overview tab. The exchange code comes straight out of the URL
  // rather than a data attribute: it is already the one honest source (works after live.js
  // itself is cached and the page is reused across two exchanges via back/forward).
  const detailsMatch = location.pathname.match(/^\/Admin\/Exchanges\/Details\/([^/]+)/);
  if (detailsMatch && document.querySelector('[data-live-region]')) {
    connectLive(detailsMatch[1]);
  }

  function connectLive(exchangeCode) {
    const es = new EventSource('/Admin/Exchanges/Live/' + encodeURIComponent(exchangeCode));

    es.addEventListener('open', () => { pushOpen = true; });
    // EventSource retries on its own with backoff; while it is down the 10 s poll above covers
    // the page exactly as it did before push existed — a stalled stream must never look live.
    es.addEventListener('error', () => { pushOpen = false; });

    es.addEventListener('panel', (ev) => {
      if (blocked()) return; // the fragment is simply skipped this round, not queued
      const region = document.querySelector('[data-live-region="' + ev.lastEventId + '"]');
      if (!region) return; // e.g. the settings tab, where none of these regions exist
      const next = new DOMParser().parseFromString(ev.data, 'text/html').body.firstElementChild;
      if (next) { morph(region, next); }
    });
  }

  // Patch `from` until it looks like `to`. Positional: children are matched by index
  // and tag, which is all this console needs — nothing here reorders under the user.
  function morph(from, to) {
    if (from.nodeType !== to.nodeType || from.nodeName !== to.nodeName) {
      from.replaceWith(to);
      return;
    }
    if (from.nodeType !== Node.ELEMENT_NODE) {
      if (from.nodeValue !== to.nodeValue) from.nodeValue = to.nodeValue;
      return;
    }
    if (from.hasAttribute('data-live-skip')) return;

    for (const a of Array.from(from.attributes)) {
      if (!to.hasAttribute(a.name)) from.removeAttribute(a.name);
    }
    for (const a of Array.from(to.attributes)) {
      if (from.getAttribute(a.name) !== a.value) from.setAttribute(a.name, a.value);
    }
    // .dot.fresh is the product's one deliberate animation: a single fade that used
    // to replay because the node was recreated each tick. Keep the node, restart the fade.
    if (from.classList.contains('fresh')) {
      from.classList.remove('fresh');
      void from.offsetWidth;
      from.classList.add('fresh');
    }
    morphChildren(from, to);
  }

  function morphChildren(from, to) {
    const a = Array.from(from.childNodes), b = Array.from(to.childNodes);
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) morph(a[i], b[i]);
    for (let i = a.length - 1; i >= n; i--) a[i].remove();
    for (let i = n; i < b.length; i++) from.appendChild(b[i]);
  }
})();
