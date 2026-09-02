// Quiet live refresh: refetch the current page and MORPH <main> in place.
// No SignalR, no reload jerk. The old version replaced every child of <main>,
// which recreated the SVG charts (flicker), reset every inner scroll, and killed
// any open dialog on the next tick. Now the fresh tree is diffed against the live
// one: only changed text and attributes are written, identical nodes stay put.
// Guards: hidden tab — skip; user typed into any form — stop until they save or
// leave; focus inside a field — skip this tick; a modal is open — skip this tick.
// Subtrees marked data-live-skip (forms, dialogs) are never touched by a tick:
// that is the user's territory, and live data does not live there.
(() => {
  'use strict';
  let dirty = false;
  document.addEventListener('input', (e) => { if (e.target.closest('form')) dirty = true; });

  const INTERVAL = 10000;
  setInterval(async () => {
    if (document.hidden || dirty || document.body.classList.contains('has-modal')) return;
    const ae = document.activeElement;
    if (ae && ae.matches('input, select, textarea')) return;
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
  }, INTERVAL);

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
