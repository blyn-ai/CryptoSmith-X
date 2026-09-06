// The only thing on this surface that moves, and it moves because it is a clock.
//
// The page arrives complete: every fade, every △, every verdict and every age is in the HTML the
// server sent, computed against the instant of the request. This file does one job — it keeps that
// instant current — and if it never runs, the page is still correct. It is just correct as of when
// it was drawn, which the header says.
//
// Everything below follows from that one job. The ages are the visible half of it. The statement
// line is the same clock said in words. And the comparative claims — the chips and the bars — are
// the half that has to be able to STOP being made, because a rank is withheld from a figure whose
// call has gone degraded and a call can cross that line with the tab still open; see THE VERDICTS
// below. Nothing here computes a new fact. It advances one and retracts what that leaves false.
//
// "The ages" includes the STATEMENT LINE, the sentence in the largest type on the page, and that is
// not a second thing that moves. It is the same clock said in words: it is derived from the same
// instants and the same windows as the ages under it, it changes only when one of those ages
// crosses a window boundary, and between crossings it is character-for-character unchanged. Left
// out of this loop — which it was — the loudest sentence on the surface read "Every call is inside
// its window." for three minutes over cells that had all flipped to "degraded", because it was
// computed once on the server and only the opt-in live stream ever redrew it. Rule 10 forbids
// decoration that moves; a page whose freshness claim stands still while its own freshness figures
// tick is the thing rule 10 exists to prevent, not an instance of it.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// THE CLOCK
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Ages are never computed against Date.now(). The sheet carries one absolute server instant and
// this anchors it to performance.now(), which is monotonic and independent of the visitor's wall
// clock. A reader whose laptop is forty seconds fast would otherwise open this page and see every
// figure faded to the floor, every age wearing △, and the words "live data degraded" under a
// perfectly healthy collector — the page accusing us of an outage that is in the reader's clock.
//
// performance.now() also does not tick while the machine is asleep, so a laptop closed for an hour
// wakes showing hour-old ages as though a minute had passed. That is why the anchor is re-taken on
// visibilitychange: on the way back the page asks the server what time it is rather than trusting
// arithmetic done across a suspend.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// THE CONSTANTS
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// Floor, exponent and the degraded multiple come from the server as data attributes. They are not
// re-typed here. This project has already shipped the other version of that decision — a 30 s
// threshold written as a literal in one place and disagreed with somewhere else — and the test file
// named after the incident is still in the repository.
//
// The amplitude is derived as 1 − floor on both sides. It is 0.85 today, it is written down nowhere,
// and it stops being 0.85 correctly the moment anyone moves the floor.
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// THE VERDICTS, AND WHY THIS FILE CAN ONLY EVER TAKE ONE AWAY
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// A rank is withheld from a figure whose call has gone `degraded` (Data/Verdicts.cs). That judgement
// is a subtraction against a clock, so it is made once, against the instant of the request — and
// this file is what moves the clock afterwards. Left as it was, the page reached out loud the state
// it used to hold in silence: the statement line, re-derived here every second, announcing "One feed
// has stopped meaning anything." while the acid-green BEST chip the server printed still sat on that
// very feed's cells. A page that states its contradiction in its own largest type is worse than one
// that merely contains it.
//
// Three designs close it and only one is admissible.
//
// (a) THE CHIPS TICK TOO — re-run the ranking here, from the ages this loop already walks. It is the
//     complete fix, and it puts rule 7 in two languages. The precedent offered for that is the
//     statement line, which IS said twice on purpose; but that sentence is five constants and four
//     yes/no questions, and what holds the two copies together is a test that greps this file for
//     the server's exact words. A ranking is not that shape: direction per column, two scopes,
//     grouping by quote asset, every figure rounded to that row's own price_step or qty_step,
//     contract multipliers, a depth band summed from two separately rounded sides, ties marked at
//     both ends, "both ends or neither", "fewer than two candidates is not a comparison". No grep
//     can assert that a Math.max here agrees with a candidates.Max there about a tie. And the inputs
//     are not in the DOM at all — the cells carry PRINTED text, so this file would first have to
//     parse "64,500.0" back into a number and then be trusted to have parsed it exactly as the
//     server formatted it. That is blueprint §1's client island, arrived at one element at a time.
//
// (b) VERDICTS STOP DEPENDING ON THE CLOCK — rank every row that has a figure, as before, and let
//     the age line and the strip carry the freshness judgement alone. Nothing can drift, because
//     nothing is duplicated. It is also the original defect: an acid-green BEST on a row whose every
//     age line reads `degraded`, which Verdicts.cs's own doc-comment calls inadmissible in as many
//     words.
//
// (c) WITHDRAWAL — this. The client is not taught to rank and cannot learn: it never reads a figure.
//     It is given the GROUPING instead — `data-rank`, straight from Verdicts.RankGroup — and exactly
//     one verb. When the call under a cell that carries a comparative claim goes degraded, every
//     claim in that cell's group comes off: both chips, and the bars, which are the same comparison
//     made quietly (Data/Scales.cs). A claim can be retracted here and never granted.
//
// Withdrawal is SOUND because ages only grow. `degraded` is monotone in age, so the candidate list
// the server ranked can only shrink while the tab is open, and a claim on this page can only become
// false — never become true. It is deliberately COARSE: a full recomputation would promote the
// runner-up when a BEST dies and keep the WORST where it was, and this drops the column instead.
// That is under-claiming, which is the direction this surface is allowed to be wrong in — the page
// shows less than it knows, and the notebar says the rule out loud. And dropping the whole group
// rather than the one dead chip is not extra caution, it is rule 7: pulling the dead BEST alone
// would leave the column marked at one end, and a table that only ever blames tells the reader half
// of what it knows exactly as a table that only ever praises does.
//
// The server is deliberately NOT symmetrical with this. It can rank, so it ranks the living — the
// degraded rows are dropped before the count — and the first paint is exact rather than withdrawn.
// The only thing the two halves must agree on is what `degraded` means, and that is one predicate
// over three numbers this file is handed rather than told.
(() => {
  'use strict';

  const sheet = document.querySelector('.a-sheet[data-now]');
  if (!sheet) return;

  const FLOOR = Number(sheet.dataset.fadeFloor);
  const EXPONENT = Number(sheet.dataset.fadeExponent);
  const DEGRADED_WINDOWS = Number(sheet.dataset.degradedWindows);
  if (!Number.isFinite(FLOOR) || !Number.isFinite(EXPONENT) || !Number.isFinite(DEGRADED_WINDOWS)) return;

  const AMPLITUDE = 1 - FLOOR;

  let serverNow = Number(sheet.dataset.now);
  let anchor = performance.now();
  if (!Number.isFinite(serverNow)) return;

  const nowMs = () => serverNow + (performance.now() - anchor);

  // ── rule 2, as arithmetic. The same three lines as Freshness.Weight, and deliberately no more:
  //    clamped at BOTH ends. Below zero because received_at is the venue's own clock on some
  //    adapters and a clock running ahead of ours is not evidence of anything; above one because
  //    past the window nothing is graded further and 31 seconds and 30 days are the same verdict.
  const weight = (ageS, winS) => {
    if (ageS === null || winS === null || !(winS > 0)) return 1;
    const spent = Math.min(Math.max(ageS / winS, 0), 1);
    return Math.max(FLOOR, 1 - AMPLITUDE * Math.pow(spent, EXPONENT));
  };

  const pastWindow = (ageS, winS) => ageS !== null && winS !== null && winS > 0 && ageS >= winS;
  const degraded = (ageS, winS) => ageS !== null && winS !== null && winS > 0 && ageS >= winS * DEGRADED_WINDOWS;

  // Word for word what Format.Age produces, including the 99+ cap that keeps the slot from changing
  // width as the count runs.
  const ageText = (ageS, winS) => {
    if (ageS === null) return '—';
    if (degraded(ageS, winS)) return 'degraded';
    const whole = Math.round(Math.max(ageS, 0));
    return whole > 99 ? '99+ s ago' : whole + ' s ago';
  };

  const shortAge = (ageS) => {
    if (ageS === null) return '—';
    const whole = Math.round(Math.max(ageS, 0));
    return whole > 99 ? '99+ s' : whole + ' s';
  };

  // ── the strip's two end labels ──
  // Word for word what Models/Strip.cs produces, and held to it by the same kind of test that holds
  // the statement line: the ends NAME the call at each end of the gradient, and the gradient's scale
  // is each call's age as a share of its OWN window. They used to be the smallest and largest raw
  // age on the row, which is a different question the moment the three calls have three cadences —
  // a price call 23 s into a 10 s window is at the spent end of a scale a depth sweep 38 s into a
  // 300 s pass is an eighth of the way along, and the labels named each other's end.
  const endText = (end) => (end === null ? '' : end.label + ' ' + shortAge(end.age));

  const endTitle = (end, which) =>
    end === null
      ? 'No call on this row states how often it looks, so the scale has no ends'
      : which + ': ' + end.label.toLowerCase() + ', ' + shortAge(end.age)
        + ' into its ' + Math.round(end.win) + ' s window';

  const num = (el, name) => {
    const raw = el.dataset[name];
    if (raw === undefined || raw === '') return null;
    const n = Number(raw);
    return Number.isFinite(n) ? n : null;
  };

  const ageOf = (el) => {
    const at = num(el, 'at');
    return at === null ? null : (nowMs() - at) / 1000;
  };

  // △ then the text, rebuilt rather than patched: the element holds two nodes in one order and
  // writing both is shorter than working out which of the four transitions just happened. It writes
  // NODES, never innerHTML — everything here came from the DOM the server sent, but a page that
  // takes numbers from one place and text from another should not have a string-to-markup path in
  // it at all.
  const mark = (el, spent, text) => {
    el.textContent = '';
    if (spent) {
      const tri = document.createElement('i');
      tri.className = 'a-tri';
      tri.textContent = '△';
      el.appendChild(tri);
    }
    el.appendChild(document.createTextNode(text));
  };

  // The cell's age line, which also carries the two state classes. The strip's end label uses mark()
  // directly, because it wears a different class for the same condition.
  const writeAge = (el, ageS, winS) => {
    const spent = pastWindow(ageS, winS);
    el.classList.toggle('a-age--missing', ageS === null);
    el.classList.toggle('a-age--spent', ageS !== null && spent);
    mark(el, ageS !== null && spent, ageText(ageS, winS));
  };

  // Gathered rather than queried every tick: the DOM does not change shape between ticks, only its
  // numbers do. It CAN change shape between updates — the live stream patches a re-rendered table
  // in, and a venue can appear or disappear in one — so this is a function and not a one-off, and
  // the live path calls it back below. With no live stream it runs exactly once, as before.
  let cells = [];
  let strips = [];
  let statement = null;
  let ranks = [];

  const collect = () => {
    // The accent half of the statement line. One element, re-derived from the strips below rather
    // than patched — it is a whole sentence, and there is no partial update of a sentence.
    statement = document.querySelector('[data-statement-verdict]');

    cells = [...document.querySelectorAll('.a-cell[data-at]')].map((cell) => ({
      cell,
      age: cell.querySelector('.a-age'),
      win: num(cell, 'win'),
    }));

    // The comparative claims, gathered by the group they are made across. Read from `data-rank`
    // and never assembled here: the key is the column and — where the column ranks per quote asset
    // — the quote, and which of those two it is is decided in Verdicts and travels with the cell.
    //
    // A cell is listed whether or not it carries a claim today, because the group is the set of
    // rows the claim was made ACROSS: the dead row is the one that revokes it and the live rows
    // are the ones that lose their chips. `.a-cell[data-rank]` and not the list above, which is
    // keyed on data-at — a cell whose call has never landed has no instant, and it is still a
    // member of its column's group.
    const byKey = new Map();
    for (const cell of document.querySelectorAll('.a-cell[data-rank]')) {
      const key = cell.dataset.rank;
      if (!key) continue;
      const member = {
        cell,
        win: num(cell, 'win'),
        // The chip, the bar and the mirrored bar. Named one by one rather than taken as "everything
        // in the mark slot": the funding note lives in that slot too, and it is not a rank — it is
        // the venue's own interval and a normalised figure, and it stays true however old the call
        // gets. (Funding carries no data-rank, so it is not here anyway; naming the three classes
        // is what keeps that still being true if it ever does.)
        claims: [...cell.querySelectorAll(
          '.a-mark > .a-tag--best, .a-mark > .a-tag--worst, .a-mark > .a-tag--tight,'
          + ' .a-hist > .a-bar, .a-hist > .a-mirror')],
      };
      if (byKey.has(key)) byKey.get(key).push(member);
      else byKey.set(key, [member]);
    }
    ranks = [...byKey.values()];

    // Each venue's freshness strip: the ticks, the span between the freshest and the oldest call,
    // the two end labels, and the named ages under them.
    strips = [...document.querySelectorAll('.a-venue')].map((venue) => ({
      ticks: [...venue.querySelectorAll('.a-strip-tick[data-at]')].map((t) => ({ el: t, win: num(t, 'win') })),
      span: venue.querySelector('.a-strip-span'),
      fresh: venue.querySelector('[data-fresh]'),
      old: venue.querySelector('[data-old]'),
      calls: [...venue.querySelectorAll('.a-strip-calls > span[data-at]')].map((c) => ({
        el: c, win: num(c, 'win'), label: c.dataset.label || '',
      })),
    }));
  };

  // Word for word what Models/Statement.cs produces, and the two are held together by a test that
  // reads this file. Same order of questions, so the two can only ever disagree about a boundary
  // they are both standing on: a degraded feed outranks a late call, a late call outranks silence,
  // and "nothing has been observed" is a different sentence from "nothing says how often it looks".
  const statementText = (degradedFeeds, oldestLate, landed, windowed) => {
    if (degradedFeeds > 0) {
      return degradedFeeds === 1
        ? 'One feed has stopped meaning anything.'
        : degradedFeeds + ' feeds have stopped meaning anything.';
    }
    if (oldestLate !== null) {
      return 'The oldest is ' + oldestLate.label.toLowerCase() + ', '
        + Math.round(oldestLate.age) + ' seconds behind.';
    }
    if (landed === 0) return 'Nothing here has been observed yet.';
    if (windowed === 0) return 'None of them states how often it looks.';
    return 'Every call is inside its window.';
  };

  const tickAll = () => {
    // The four facts the statement line is made of, gathered from the same calls the strips below
    // are drawn from. Never from a second source: the sentence and the ages under it have to be
    // reading one clock or the page contradicts itself in its own largest type.
    let degradedFeeds = 0;
    let landed = 0;
    let windowed = 0;
    let oldestLate = null;

    for (const c of cells) {
      const ageS = ageOf(c.cell);
      c.cell.style.setProperty('--w', weight(ageS, c.win).toFixed(3));
      if (c.age) writeAge(c.age, ageS, c.win);
    }

    for (const s of strips) {
      let lo = null;
      let hi = null;
      let anyDegraded = false;

      for (const t of s.ticks) {
        const ageS = ageOf(t.el);
        if (ageS === null || t.win === null || !(t.win > 0)) continue;
        const x = Math.min(Math.max(ageS / t.win, 0), 1);
        t.el.style.left = (x * 100).toFixed(1) + '%';
        // A tick sitting on the spent end of the gradient is drawn in the card's own colour: an ink
        // mark there would read as a measurement rather than as the edge it is.
        t.el.classList.toggle('a-strip-tick--spent', x >= 1);
        lo = lo === null ? x : Math.min(lo, x);
        hi = hi === null ? x : Math.max(hi, x);
      }

      if (s.span && lo !== null) {
        s.span.style.left = (lo * 100).toFixed(1) + '%';
        // Floored at a hair so three calls that landed together still draw a mark. They are at one
        // point, and one point is a fact.
        s.span.style.width = (Math.max(0.009, hi - lo) * 100).toFixed(1) + '%';
      }

      let least = null;
      let most = null;

      for (const call of s.calls) {
        const ageS = ageOf(call.el);
        if (ageS === null) continue;
        const spent = pastWindow(ageS, call.win);
        anyDegraded = anyDegraded || degraded(ageS, call.win);
        call.el.classList.toggle('a-spent', spent);
        call.el.textContent = call.label + ' ' + shortAge(ageS);

        // A call that landed. `windowed` counts the ones that also state a cadence — a call we have
        // observed but cannot judge is not the same silence as a call we have never seen.
        landed += 1;
        if (call.win !== null) windowed += 1;
        if (spent && (oldestLate === null || ageS > oldestLate.age)) {
          oldestLate = { label: call.label, age: ageS };
        }

        // The two ends, by share of this call's own window and UNCLAMPED — two calls both past
        // their windows are ordered by which is further past, not by which came first in the row.
        // A call with no stated cadence is skipped: it has no tick on the scale for that reason, so
        // it cannot be at either end of one, and its age is in this same list where it belongs.
        if (call.win === null || !(call.win > 0)) continue;
        const share = ageS / call.win;
        const end = { label: call.label, age: ageS, win: call.win, share, spent };
        if (least === null || share < least.share) least = end;
        if (most === null || share > most.share) most = end;
      }

      // "Degraded" is a per-call verdict against that call's own window, not a flat multiple of a
      // flat number — a fifty-minute-old depth sweep is degraded on a venue that sweeps in six
      // minutes and perfectly normal on one that sweeps daily.
      if (s.fresh) {
        s.fresh.textContent = anyDegraded ? '' : endText(least);
        s.fresh.title = endTitle(least, 'Least spent');
      }
      if (s.old) {
        // The △ and the hold ink belong to the call at THIS end, not to any call on the row: the
        // mark said "this has stopped being graded" beside a depth sweep at an eighth of its window
        // because some other call on the row was late.
        const late = most !== null && most.spent;
        s.old.classList.toggle('a-spent', late);
        s.old.title = endTitle(most, 'Most spent');
        mark(s.old, late, anyDegraded ? 'live data degraded' : endText(most));
      }

      // Per ROW, matching the server: a feed has stopped meaning anything when any one of its three
      // calls has, not once per dead call.
      if (anyDegraded) degradedFeeds += 1;
    }

    if (statement) {
      statement.textContent = statementText(degradedFeeds, oldestLate, landed, windowed);
    }

    // ── The withdrawal ──
    // One question per group, and it is not "who is best now" — it is "has a row this claim was
    // made ON stopped meaning anything". The trigger is a cell that CARRIES a claim, because a cell
    // carrying none supplied neither end of the ranking nor the bar's maximum, so its dying leaves
    // every claim in the group exactly as defensible as it was. The header above argues why the
    // answer is then to retract the whole group rather than re-rank it, and why retracting one end
    // alone would be a worse page than retracting both.
    for (const members of ranks) {
      if (!members.some((m) => m.claims.length > 0 && degraded(ageOf(m.cell), m.win))) continue;
      for (const m of members) {
        for (const el of m.claims) el.remove();
        // Emptied rather than left holding detached nodes: this runs every second for the life of
        // the tab, and a withdrawn claim is withdrawn once. It also stops the group re-triggering
        // on itself, since a group with no claims left can no longer answer the question above.
        m.claims.length = 0;
      }
    }
  };

  let timer = null;

  const start = () => {
    if (timer !== null) return;
    // One second, because the ages are printed in whole seconds and anything faster would repaint
    // the same characters. Nothing on this page changes on its own except a clock: the ages, the
    // fades they drive, the statement line, which is those same ages in a sentence, and the chips
    // and bars a crossed window has just made indefensible.
    timer = window.setInterval(tickAll, 1000);
  };

  const stop = () => {
    if (timer === null) return;
    window.clearInterval(timer);
    timer = null;
  };

  // Coming back from a hidden tab, the arithmetic above may have run across a suspend, so the
  // anchor is re-taken against the server rather than trusted. A HEAD is enough — the Date header
  // is the answer — and it is one request per return to the tab, not a poll.
  const resync = async () => {
    try {
      const res = await fetch(window.location.href, { method: 'HEAD', cache: 'no-store' });
      const date = res.headers.get('date');
      if (!date) return;
      const t = Date.parse(date);
      if (!Number.isFinite(t)) return;
      serverNow = t;
      anchor = performance.now();
    } catch (e) {
      // Offline, or the request was refused. The existing anchor is stale but monotonic, so the
      // ages keep climbing from where they were — which is the truthful direction to be wrong in.
    }
  };

  document.addEventListener('visibilitychange', () => {
    if (document.hidden) {
      stop();
    } else {
      resync().then(tickAll);
      start();
    }
  });

  // ── The live stream, when the reader has asked for one ──
  // arena-live.js patches a freshly rendered table into the page and then hands over the instant the
  // server rendered it at. Two things follow from that and both are here rather than there: the
  // anchor moves to the server's instant — the fragment's ages were computed against it, and the
  // count must go on from where the server left it rather than from a browser clock that has been
  // drifting since page load — and the node lists are rebuilt, because that patch is the only thing
  // on this page that can add or remove a venue.
  //
  // The listener costs nothing when no stream is ever opened, which is the common case. Its own
  // event never fires and this file behaves exactly as it did before the button existed.
  document.addEventListener('csx-arena-live', (ev) => {
    const at = ev.detail && ev.detail.serverNow;
    if (Number.isFinite(at)) {
      serverNow = at;
      anchor = performance.now();
    }
    collect();
    tickAll();
  });

  collect();
  tickAll();
  start();

  // ── The night register ──
  // The button starts hidden in the markup and is revealed here, because with scripts off it would
  // do nothing, and a control that does nothing is a small lie. The flip changes luminance, never
  // identity: the same token set, magenta accented in both.
  const ink = document.getElementById('a-ink');
  if (ink) {
    const label = () => {
      const night = document.documentElement.getAttribute('data-theme') === 'night';
      ink.textContent = night ? 'Paper' : 'Ink';
    };
    ink.hidden = false;
    label();
    ink.addEventListener('click', () => {
      const night = document.documentElement.getAttribute('data-theme') === 'night';
      document.documentElement.setAttribute('data-theme', night ? 'paper' : 'night');
      try {
        // Written only because the visitor pressed the button. Nothing is stored for a reader who
        // never asks for anything.
        localStorage.setItem('csx-arena-theme', night ? 'paper' : 'night');
      } catch (e) { /* Private mode. The choice holds for this page and is not remembered. */ }
      label();
      document.dispatchEvent(new CustomEvent('csx-arena-theme'));
    });
  }
})();
