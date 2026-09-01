// Quiet live refresh: refetch the current page and swap <main> in place.
// No SignalR, no reload jerk: scroll survives, the shell never re-renders.
// Guards: hidden tab — skip; user typed into any form — stop until they save
// or leave; focus inside a field — skip this tick.
(() => {
  'use strict';
  let dirty = false;
  document.addEventListener('input', (e) => { if (e.target.closest('form')) dirty = true; });

  const INTERVAL = 10000;
  setInterval(async () => {
    if (document.hidden || dirty) return;
    const ae = document.activeElement;
    if (ae && ae.matches('input, select, textarea')) return;
    try {
      const r = await fetch(location.href, { headers: { 'X-Live': '1' } });
      if (!r.ok) return;
      const doc = new DOMParser().parseFromString(await r.text(), 'text/html');
      const next = doc.querySelector('main.main');
      const cur = document.querySelector('main.main');
      if (next && cur) cur.replaceChildren(...next.childNodes);
      const nav = doc.querySelector('.nav'), curNav = document.querySelector('.nav');
      if (nav && curNav) curNav.replaceChildren(...nav.childNodes); // badges stay honest
    } catch { /* transient network blip — next tick retries */ }
  }, INTERVAL);
})();
