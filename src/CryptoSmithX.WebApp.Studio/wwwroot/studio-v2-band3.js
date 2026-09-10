/* B3's timeframe/series switch, swapped in place instead of navigated to.

   The link is real — PairsV2Controller.Asset's own address, with ?tf=&series= — so a reader with
   no script, or a right-click "open in new tab", gets exactly what they expect: the same page,
   freshly rendered, scrolled to band 3. This intercepts the plain click only, fetches that SAME
   URL with an XMLHttpRequest header, and the controller answers with JSON instead of the full page
   when it sees that header (Request.Headers["X-Requested-With"], PairsV2Controller.Asset).

   Why this exists: a timeframe or series switch used to be a full navigation, which reset
   everything else on the page that was not part of the choice — scroll position, the tape's FOLLOW
   state, any open band-5 coverage disclosure. Band 3 is not a live-pushed region (chart panels are
   not something a background pass should redraw under a reader who is looking at one), so this is
   the one piece of the page that is neither "server-rendered once" nor "replaced on every push" —
   it is replaced on the one interaction that actually changes it, and nothing else moves. */
(function () {
  var BAND = document.getElementById('band-time');
  if (!BAND) { return; }

  // This script's own tag names the app's base path (e.g. "/studio/"), the same way every other
  // asset on this page is addressed — read once, synchronously, because document.currentScript is
  // only valid while this script is the one running.
  var SELF_SRC = document.currentScript ? document.currentScript.src : '';
  var BASE = SELF_SRC.replace(/studio-v2-band3\.js.*$/, '');

  function loadScript(src) {
    return new Promise(function (resolve, reject) {
      var s = document.createElement('script');
      s.src = src;
      s.onload = function () { resolve(); };
      s.onerror = function () { reject(new Error('failed to load ' + src)); };
      document.body.appendChild(s);
    });
  }

  // The chart library loads only when band 3 actually has something to draw (see Asset.cshtml's
  // own comment on that gate) — so a reader who switches INTO a series/timeframe that turns out to
  // hold data, having started on one that held none, needs it fetched here instead of assuming
  // it is already on the page.
  var candlesReady = null;
  function ensureCandles() {
    if (window.CSXInitCandles) { return Promise.resolve(); }
    if (candlesReady) { return candlesReady; }

    candlesReady = loadScript(BASE + 'vendor/lightweight-charts/lightweight-charts-5.2.1.standalone.production.js?v=1')
      .then(function () { return loadScript(BASE + 'studio-candles.js?v=6'); });
    return candlesReady;
  }

  BAND.addEventListener('click', function (e) {
    var trigger = e.target.closest('.v2-tf-btn');
    if (!trigger) { return; }

    e.preventDefault();
    var url = trigger.getAttribute('href');

    fetch(url, { headers: { 'X-Requested-With': 'XMLHttpRequest' } })
      .then(function (r) {
        if (!r.ok) { throw new Error('http ' + r.status); }
        return r.json();
      })
      .then(function (data) {
        var head = document.getElementById('band3-head');
        var cuts = document.getElementById('band3-cuts');
        if (head) { head.innerHTML = data.head; }
        if (cuts) { cuts.innerHTML = data.cuts; }

        // The address bar follows the choice — reload, bookmark and share all land on the same
        // timeframe/series — without this being a navigation the browser actually performed.
        if (data.url) { history.pushState(null, '', data.url); }

        return ensureCandles().then(function () {
          if (window.CSXInitCandles) { window.CSXInitCandles(); }
        });
      })
      .catch(function () {
        // A real navigation is still a correct answer to the click; it is only not the fast one.
        window.location.href = url;
      });
  });
})();
