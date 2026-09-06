// The live button: the one thing on this page that opens a connection, and it opens none until it
// is pressed.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT ARRIVES
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Server-rendered HTML — the same partials that drew the page on the first paint, re-rendered by the
// same C# that decided where a dash goes and how far a figure had faded. Nothing here assembles a
// cell, formats a number or decides an age from JSON. This file places fragments and says what state
// the connection is in; that is the whole job, and it is the reason there is one set of rules on this
// surface rather than a server's and a browser's.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// WHAT A FAILURE MUST NOT LOOK LIKE
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// A market that went quiet. On a page whose subject is how old each figure is, a stream that dies
// silently is indistinguishable from a market where nothing has happened — and the page would be
// making the exact claim it exists to refuse to make. So every failure is said in words beside the
// button, and the page falls back to what it does without a stream at all: ages that keep counting
// against instants that are still true.
//
// That fallback needs no code here. arena-ages.js has never depended on this file.
(() => {
  'use strict';

  const row = document.querySelector('.a-liverow[data-live-url]');
  if (!row) return;

  const button = row.querySelector('#a-live');
  const note = row.querySelector('#a-live-note');
  const url = row.dataset.liveUrl;
  if (!button || !note || !url) return;

  // No EventSource, no button. Same argument as the INK control in the header: with the button
  // hidden the page is the static page, which is a complete and correct page; with the button shown
  // and inert it would be a promise the surface cannot keep.
  if (typeof EventSource === 'undefined') return;

  // The stream is dropped when the tab goes to the background, and this remembers that the reader
  // had asked for it, so it comes back with them. A hidden tab is not a viewer: it holds a slot and
  // is sent renders nobody is looking at, and the first thing a reopened stream does is send the
  // current state — so nothing is missed by having been away.
  let wanted = false;
  let source = null;
  let attempts = 0;

  // Five failed attempts before the page stops trying. EventSource on its own retries forever,
  // which on a server that is down means a page quietly knocking every three seconds for as long
  // as the tab is open — and a reader who is told nothing. Five is enough to ride out a restart or
  // a proxy hiccup and few enough that a real outage is reported rather than hidden.
  const MAX_ATTEMPTS = 5;

  const SAY = {
    opening: 'opening the live connection',
    live: 'live — the table is replaced as each collector pass lands; the candles are not',
    signalDown: 'connected, but the database signal behind it is down — nothing will arrive until it '
      + 'returns. The figures below are unchanged and their ages are still true.',
    signalStalled: 'connected, and the page cannot be rebuilt — the last attempt to read the market '
      + 'failed. The figures below are unchanged and their ages are still true.',
    dropped: 'the live connection dropped — retrying. Nothing below is being replaced, and its ages '
      + 'keep counting.',
    gaveUp: 'the live connection could not be kept open — this page is static again, and its ages '
      + 'keep counting.',
    full: 'the live feed is at capacity right now — this page stays static, and its ages keep counting.',
    gone: 'this pair is no longer listed here. Reload the page.',
    off: '',
  };

  const say = (text) => { note.textContent = text; };

  const setPressed = (on) => {
    button.setAttribute('aria-pressed', on ? 'true' : 'false');
  };

  function open() {
    if (source) return;
    attempts = 0;
    say(SAY.opening);
    connect();
  }

  function connect() {
    source = new EventSource(url);

    source.addEventListener('open', () => {
      attempts = 0;
      say(SAY.live);
    });

    // Fired for every failed attempt as well as for a drop. readyState says which: CONNECTING means
    // the browser is retrying on its own, CLOSED means it has stopped and only this file can decide
    // what happens next.
    source.addEventListener('error', () => {
      attempts += 1;
      if (attempts >= MAX_ATTEMPTS || (source && source.readyState === EventSource.CLOSED)) {
        stop(SAY.gaveUp);
        return;
      }
      say(SAY.dropped);
    });

    // One fragment, placed in the region it names. Generic on purpose: adding a region to the page
    // is a line in the server's list and nothing here.
    source.addEventListener('panel', (ev) => {
      const region = document.querySelector('[data-live-region="' + ev.lastEventId + '"]');
      if (!region) return;
      const next = new DOMParser().parseFromString(ev.data, 'text/html').body.firstElementChild;
      if (next) morph(region, next);
    });

    // The server's instant, sent after the fragments it belongs to. Handing it to arena-ages.js is
    // what stops the two from disagreeing: the fragments carry ages computed at that instant, and
    // the script that advances them re-anchors on the same one. While a stream is open the stream
    // is also the page's clock correction — every push re-anchors, so a browser whose monotonic
    // clock has drifted is put right without the page asking anyone what time it is.
    source.addEventListener('clock', (ev) => {
      const at = Number(ev.data);
      document.dispatchEvent(new CustomEvent('csx-arena-live', {
        detail: { serverNow: Number.isFinite(at) ? at : null },
      }));
    });

    // What state the stream is really in. An open socket is not evidence of a live feed, and this is
    // the difference between "nothing is happening" and "we have stopped being told" — the one
    // distinction this whole surface is built to keep. Two ways to stop being told, and they are
    // separate sentences because they are separate faults: 'down' is nothing being announced,
    // 'stalled' is the announcement arriving and the page failing to be rebuilt.
    source.addEventListener('signal', (ev) => {
      if (ev.data === 'up') say(SAY.live);
      else if (ev.data === 'stalled') say(SAY.signalStalled);
      else say(SAY.signalDown);
    });

    // The server declining, in words, before closing. Retrying would be arguing with an answer we
    // were given.
    source.addEventListener('notice', (ev) => {
      stop(ev.data === 'gone' ? SAY.gone : SAY.full);
    });
  }

  // Closes the stream and leaves the reason on screen. `wanted` is cleared, so the button reads as
  // off and the tab returning to the foreground does not reopen a stream the server just refused.
  function stop(reason) {
    wanted = false;
    close();
    setPressed(false);
    say(reason);
  }

  function close() {
    if (!source) return;
    source.close();
    source = null;
  }

  button.addEventListener('click', () => {
    wanted = !wanted;
    setPressed(wanted);
    if (wanted) {
      open();
    } else {
      close();
      say(SAY.off);
    }
  });

  document.addEventListener('visibilitychange', () => {
    if (!wanted) return;
    if (document.hidden) {
      close();
    } else {
      open();
    }
  });

  button.hidden = false;

  // ── Patching ──
  // Ported from the admin console's live.js, which has run this shape for a while. It patches nodes
  // in place instead of replacing the region, and on this page that is not an optimisation: the
  // table scrolls sideways, and the scroll position belongs to a node inside the fragment. Swapping
  // the element would send a reader who had scrolled to the depth bands back to bid, every few
  // seconds, for as long as they left the stream open. Positional matching by index and tag is all
  // that is needed — nothing on this page reorders under the reader.
  function morph(from, to) {
    if (from.nodeType !== to.nodeType || from.nodeName !== to.nodeName) {
      from.replaceWith(to);
      return;
    }
    if (from.nodeType !== Node.ELEMENT_NODE) {
      if (from.nodeValue !== to.nodeValue) from.nodeValue = to.nodeValue;
      return;
    }
    for (const a of Array.from(from.attributes)) {
      if (!to.hasAttribute(a.name)) from.removeAttribute(a.name);
    }
    for (const a of Array.from(to.attributes)) {
      if (from.getAttribute(a.name) !== a.value) from.setAttribute(a.name, a.value);
    }
    morphChildren(from, to);
  }

  function morphChildren(from, to) {
    const a = Array.from(from.childNodes);
    const b = Array.from(to.childNodes);
    const n = Math.min(a.length, b.length);
    for (let i = 0; i < n; i++) morph(a[i], b[i]);
    for (let i = a.length - 1; i >= n; i--) a[i].remove();
    for (let i = n; i < b.length; i++) from.appendChild(b[i]);
  }
})();
