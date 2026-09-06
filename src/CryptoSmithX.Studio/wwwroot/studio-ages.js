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
//
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// THE DOCUMENT'S OWN AGE, AND THE DEFECT IT CLOSES
// ─────────────────────────────────────────────────────────────────────────────────────────────────
// A reader opened this page, left the tab and came back to "99+ s ago" on every cell, `degraded` on
// two venues, and "4 feeds have stopped meaning anything." in the largest type on the page. The
// collector was fine — on the host that produced the complaint, 566 of 566 Binance rows were under
// 30 s, 177 of 177 Hyperliquid, 272 of 280 Kraken. Every mark on that screen was true. The page was
// honest and it looked broken, and what the reader actually asked was "why are some so old".
//
// The page was answering a question it had not been asked. "How old is this figure" and "has this
// venue stopped" are different questions, and with no stream open the first one cannot tell them
// apart: nothing is re-read after the render, so every age on the page advances by exactly the same
// amount, and what the reader is looking at is the DOCUMENT's age painted onto five hundred cells.
// A venue that died in that window is indistinguishable from one that did not, because the page has
// not looked at either of them since.
//
// So the distinction is not per cell. It is per document, and it is stated once.
//
// (a) MAKE LIVE THE DEFAULT — every reader gets a stream, the page never sits still and the
//     question never comes up. REJECTED three ways. It spends a held-open connection per visitor on
//     the many who never asked for one, on a feed that already has a sentence prepared for the day
//     it runs out of capacity. It fixes nothing for the readers who cannot have it — scripts off,
//     no EventSource, feed full — and they get the identical page with the defect intact, which
//     means the defect would not be closed, only hidden from the majority. And it converts a
//     document into a monitor without being asked: the argument this whole surface makes is that
//     the reader is told what they are looking at, and a page that starts replacing its own figures
//     on arrival has made that decision on their behalf.
//
// (b) A DIFFERENT MARK ON THE CELLS — draw "this aged along with the page" differently from "this
//     call died". REJECTED as a category error, and worth being precise about, because it is the
//     first idea anyone has and it is the one the brief names. With no stream there is no cell that
//     died: the page holds ONE render, every age advances in lockstep, and a cell that crosses into
//     `degraded` at T+180 crossed because the document is 180 s old. A per-cell mark would draw a
//     difference between cells that does not exist between them. And the only version of it that
//     would quiet the page down is the subtractive one — withhold the △ from a cell that spent its
//     window after the render — which is the page claiming a figure is fresher than it is, the one
//     thing this surface may never do. The idea is right; its level is wrong.
//
// (c) THE DOCUMENT SAYS HOW OLD IT IS — this. The page states its own age, once, in the row under
//     the statement line: it was rendered N ago, nothing below has been re-read since, and what the
//     venues have done in that time is not a thing this page knows. The feeds that were ALREADY
//     degraded at the render are counted separately in the same sentence, because those ARE facts
//     about a venue — they are the half of the reader's question that has a real answer, and the
//     half the old page buried by making every other row look like them.
//
//     AND THE OTHER HALF STAYS OPEN. The first version of this sentence closed it: the feeds that
//     crossed into `degraded` after the render were described as marks that "date the page, not the
//     venue". That is a cause, and this page has no evidence for one — the paragraph above says so
//     itself, three ways. It was also flatly wrong for the calls that were already visibly late when
//     the server rendered them: a depth sweep 800 s into a 300 s window is not degraded yet, so it
//     crosses under the reader, is counted here as freshly degraded, and was exonerated by name
//     while the venue had in fact been silent for two and a half windows before the page existed.
//     A page that resolves its own ambiguity in its own favour is worse than one that states it,
//     and the one failure the reader most needs to see is the one that treatment hid.
//
// Nothing is retracted, softened or un-faded. The cells say exactly what they said. This adds one
// claim, about the document, and it is a claim that can only ever make the page under-state its own
// freshness — the direction this surface is allowed to be wrong in. The property that no figure is
// ever shown as fresher than it is survives untouched, because nothing here touches a figure.
//
// THE TRIGGER IS THE DOCUMENT'S OWN AGE, measured against the page's own windows. Four candidates:
//
//   * A fixed document age — "this page is more than two minutes old". A new constant, disagreeing
//     with every window on the page: two minutes is ancient for a 10 s ticker and nothing at all
//     for a 653 s depth sweep, and this surface has already shipped the bug where one flat number
//     stood in for per-call windows.
//
//   * Any call crossing OUT OF ITS WINDOW since the render. Measured: it fires seven seconds after
//     load on this pair, and it is firing about a mark that is behaving correctly — △ says "past
//     its window, no longer graded", the age beside it is still spelled out to the second, and
//     nothing has been claimed about the venue. A box saying the page is stale, seven seconds in,
//     over a page that is telling the exact truth legibly, is the page interrupting itself.
//
//   * A call going `degraded` since the render. SHIPPED, AND WRONG ON ITS OWN TERMS: it is an event
//     in the FEEDS used to gate a sentence about the DOCUMENT, and the two come apart on exactly
//     the page this row exists for. Where every feed was already degraded when the server built it,
//     nothing can cross, the count is permanently zero, and a tab left open for an hour over a
//     screen of `degraded` never states its age, never shows this box and never offers Reload. The
//     reader who is most misled is the one the gate kept silent.
//
//   * THE DOCUMENT ITSELF PAST `degraded` ON THE QUICKEST WINDOW IT CARRIES — this. The same
//     predicate, asked of the document: once the page's own age has passed DEGRADED_WINDOWS times
//     the shortest window on it, a figure written by the fastest call here would read `degraded`
//     from the document standing still alone, whoever is publishing. It is not a new constant and
//     not a flat one — it is per page, off the windows the server sent, so a page of ten-second
//     tickers admits it in minutes and a page of daily sweeps does not admit it at ten minutes,
//     which is the correct answer in both cases.
//
// A verdict changing under the reader is kept as a SECOND trigger, ORed with the first: that is the
// one boundary where the page's story changes rather than its arithmetic — the count stops being
// spelled out, the comparative claims in that column are withdrawn (see THE VERDICTS above), and
// the statement line starts saying feeds have stopped meaning anything — and it can happen while
// the document is still young. It gates when the sentence is said; it never decides what it says.
//
// No new constant enters the surface either way: `degraded` is the predicate three of the other
// judgements in this file are already made with, over the windows the server sent.
//
// It is also where the LIVE button is finally discoverable. The button worked — the stream connects,
// the table updates — and the reader reported it missing, because 54 by 32 pixels reading "Live"
// says nothing about what the page is doing NOW or what pressing it would change. The sentence
// beside it says both, and at the moment the reader most wants an answer it says which of the two
// controls in that row solves what they are looking at.
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

  // The instant the figures currently on screen were produced, which starts as the render instant
  // and is NOT the same variable as serverNow. serverNow is a clock correction and moves whenever
  // the browser's arithmetic is re-checked against the server (a HEAD on the way back to the tab);
  // this moves only when the fragments themselves are replaced, which is a live push and nothing
  // else. Conflating the two would let a clock correction declare the document fresh.
  let renderedAt = serverNow;

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

  // How old the DOCUMENT is, and deliberately not in the two formats above. A cell's age caps at
  // "99+ s" for two reasons that both stop applying here: the slot must not change width as the
  // count runs, and past its window a call is not graded further so 31 seconds and 30 days are one
  // verdict. The document is graded against nothing and sits in a sentence, so it is written at
  // whatever size it actually is — "99+ s" for a tab left open since lunch would be the page hiding
  // the one figure the sentence exists to state.
  const durationText = (seconds) => {
    const whole = Math.round(Math.max(seconds, 0));
    if (whole < 120) return whole + ' s';
    const minutes = Math.round(whole / 60);
    if (minutes < 120) return minutes + ' min';
    const hours = Math.floor(minutes / 60);
    const rest = minutes % 60;
    return rest === 0 ? hours + ' h' : hours + ' h ' + rest + ' min';
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
  // ── The row under the statement line ──
  // Queried once and never re-queried: it sits outside every [data-live-region] on purpose, so it
  // is the one part of this page a live push cannot replace. On a page with no live control — the
  // pair list — none of these exist and every use below is guarded.
  const liveRow = document.querySelector('.a-liverow');
  const pageState = document.getElementById('a-live-what');
  const reloadButton = document.getElementById('a-live-reload');
  const liveButton = document.getElementById('a-live');

  // location.reload() and not an anchor to the same address. A link would be answered from the
  // browser's own cache as readily as from the server, and handing the reader back a copy of the
  // document they just asked to replace is the exact failure this control exists to fix.
  if (reloadButton) {
    reloadButton.addEventListener('click', () => { window.location.reload(); });
  }

  // Held so the sentence is written only when it changes. It is rebuilt on every tick from figures
  // that mostly do not move, and rewriting identical text once a second for the life of the tab is
  // work for nothing.
  let said = null;

  // See THE DOCUMENT'S OWN AGE at the top of this file. Every number here is gathered in the same
  // walk as the statement line above, so the sentence about the page and the sentence about the
  // feeds can never be reading two different clocks.
  //
  // WHAT THIS SENTENCE MAY SAY, AND WHAT IT MAY NOT.
  //
  // It shipped once saying that the marks a tab's own age produced "date the page, not the venue",
  // and that was a cause asserted where the page has no evidence for one. The header above states
  // the reason in the other direction and it is the whole argument of this row: with no stream
  // open, a venue that died one second after the render and a venue that is still publishing
  // produce EXACTLY the same thing on this screen, because the page has not looked at either since
  // it was built. Picking one of the two and printing it in body ink resolved the reader's question
  // in our favour — and it resolved it wrongest on the page that matters most, the one whose calls
  // were already visibly late when the server rendered them: a call 800 s into a 300 s window is
  // not degraded yet (that is DEGRADED_WINDOWS windows away), so it crosses later, is counted here
  // as freshly degraded, and was then exonerated by name.
  //
  // So the sentence states the document's age, states that nothing has been re-read, and says the
  // ambiguity out loud rather than closing it. The half of the reader's question that HAS an answer
  // — the feeds the server itself found degraded — is still answered, and still counted apart.
  const sayPageState = (docAgeS, freshlyDegraded, alreadyDegraded, fastestWinS) => {
    if (!pageState) return;

    const liveOn = liveButton !== null && liveButton.getAttribute('aria-pressed') === 'true';

    // THE GATE IS THE DOCUMENT'S OWN AGE, because the document's own age is what the sentence is
    // about. It was a feed CROSSING into degraded after the render, which is a different question
    // and left silent the page the complaint describes: every feed already degraded when the server
    // built it, `freshlyDegraded` permanently 0, and a tab open for an hour that never states its
    // age, never shows this box and never reveals Reload — the reader most misled, told least.
    //
    // The threshold is not a new constant, which the header rejects and this surface has already
    // shipped the bug for: it is `degraded` — the predicate three other judgements in this file are
    // made with — asked of the DOCUMENT against the quickest window the page carries. Past it, a
    // figure written by the fastest call on this page would read `degraded` from the document
    // sitting still alone, whatever any venue has done. Below it the page is younger than its own
    // fastest cadence's write-off and has nothing to admit.
    //
    // Kept as a second trigger: a verdict that changed under the reader. That can happen while the
    // document is still young — a call one second short of degraded at the render crosses at T+1 —
    // and it is the moment the page's story changes (the count stops being spelled out, the chips
    // come off, the statement line starts saying feeds have stopped meaning anything), so it is
    // still owed an explanation. The two triggers are ORed; neither claims a cause.
    const aged = fastestWinS !== null && degraded(docAgeS, fastestWinS);
    const stale = aged || freshlyDegraded > 0;
    const marked = freshlyDegraded + alreadyDegraded;

    let text;

    if (stale) {
      text = 'Rendered ' + durationText(docAgeS) + ' ago, and nothing below has been re-read since. ';

      if (freshlyDegraded > 0) {
        // Deliberately the statement line's own unit and the table's own word. The sentence this
        // one sits under says "N feeds have stopped meaning anything", the cells say `degraded`,
        // and a reader who has just read both is owed the arithmetic in the same terms rather than
        // a second vocabulary to reconcile with the first.
        text += (marked === 1
          // One feed on the page and it crossed under the reader: "1 of the 1 feeds" is what a
          // count says when nobody wrote the sentence for the smallest page it can appear on.
          ? 'The one feed reading degraded crossed that line after the render'
          : freshlyDegraded === marked
            ? 'All ' + marked + ' feeds reading degraded crossed that line after the render'
            : freshlyDegraded === 1
              ? 'One of the ' + marked + ' feeds reading degraded crossed that line after the render'
              : freshlyDegraded + ' of the ' + marked + ' feeds reading degraded crossed that line'
                + ' after the render')
          // The unknown, said out loud. This is the sentence the defect was in, and it is now the
          // one place on the page that names the ambiguity instead of resolving it.
          + ', and this page cannot tell you why: a venue that has stopped and a document left open'
          + ' look exactly the same from here.'
          // The other half of the reader's question, and the half that has a real answer: a feed the
          // SERVER found degraded is an observation about a venue, and it stays one however long the
          // tab is left open. Counted apart from the ones above and never merged with them.
          + (alreadyDegraded === 1
            ? ' The other was already degraded at the render, and that is a fact about the venue.'
            : alreadyDegraded > 1
              ? ' The other ' + alreadyDegraded + ' were already degraded at the render, and those'
                + ' are facts about the venues.'
              : '');
      } else if (marked > 0) {
        // Every degraded mark on the page is the server's own observation. Nothing here is the
        // document's doing, so nothing is counted against it — but the document is still old, and
        // what those venues have done since is still not a thing this page knows.
        text += (marked === 1
          ? 'The one feed reading degraded was already degraded at the render, and that is a fact'
            + ' about the venue.'
          : 'All ' + marked + ' feeds reading degraded were already degraded at the render, and'
            + ' those are facts about the venues.')
          + ' What has happened at any venue since is not something this page can know.';
      } else {
        // A stale page with nothing reading degraded, which the gate above cannot actually produce:
        // `aged` means the document has outlived DEGRADED_WINDOWS of the quickest window on it, and
        // the call carrying that window has aged by at least as much, so it is degraded by then.
        // Written anyway, and not as an assertion or a throw: this branch is what the sentence says
        // if that gate is ever loosened, and a page whose stale row can come out undefined is a
        // worse failure than a paragraph that is never printed. It is the same fact as the two
        // above it with the counting removed.
        text += 'What the venues have done in that time is not something this page can know until'
          + ' it is re-read.';
      }

      // Deliberately NOT "the stream is open and nothing has arrived" when the button reads
      // pressed. `aria-pressed` is the reader's INTENT, not the connection: studio-live.js keeps it
      // pressed while a dropped stream retries, and while a backgrounded tab has no stream at all.
      // Reading intent and reporting it as connection health would have this sentence assert an
      // open socket where there is none — and "nothing has arrived" would then read as a fact about
      // the venues, which is the one claim studio-live.js's own header says this page exists to
      // refuse. The connection has a voice already: the note beside this one, written by the file
      // that actually holds the socket.
      text += (liveOn
        ? ' Reload for a fresh read.'
        : ' Reload for a fresh read, or press Live and the table is replaced as each collector'
          + ' pass lands.');
    } else if (liveOn) {
      // A stream is open and studio-live.js is already saying what it is doing, in the note beside
      // this one. Two sentences about one thing is two systems talking over each other.
      text = '';
    } else {
      text = 'This page is a snapshot. The ages below count forward from the render — nothing is'
        + ' re-read until you press Live.';
    }

    if (text !== said) {
      pageState.textContent = text;
      said = text;
    }

    if (liveRow) liveRow.classList.toggle('a-liverow--stale', stale);
    if (reloadButton) reloadButton.hidden = !stale;
  };

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

    // How old the figures on screen are AS A DOCUMENT, and what that age alone has done to the
    // verdicts printed on them. Floored at zero: a clock correction on the way back to the tab can
    // land a hair behind the render instant, and a negative document age is not a fact.
    const docAgeS = Math.max(0, (nowMs() - renderedAt) / 1000);
    let freshlyDegraded = 0;
    let alreadyDegraded = 0;
    // The quickest cadence anywhere on this page, and the only thing the DOCUMENT's own age is
    // judged against. Read off the calls rather than named as a number here: see the gate in
    // sayPageState for why a flat threshold is the one answer this surface may not give.
    let fastestWinS = null;

    for (const c of cells) {
      const ageS = ageOf(c.cell);
      c.cell.style.setProperty('--w', weight(ageS, c.win).toFixed(3));
      if (c.age) writeAge(c.age, ageS, c.win);
    }

    for (const s of strips) {
      let lo = null;
      let hi = null;
      let anyDegraded = false;
      let anyDegradedThen = false;

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
        if (call.win !== null && call.win > 0 && (fastestWinS === null || call.win < fastestWinS)) {
          fastestWinS = call.win;
        }

        // The same predicate, asked of this call's age AT THE RENDER. Nothing else on the page is
        // computed twice against two instants; this is, because the whole question the row below
        // answers is which of these marks the server put there and which the open tab did.
        anyDegradedThen = anyDegradedThen || degraded(ageS - docAgeS, call.win);
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
      if (anyDegraded) {
        degradedFeeds += 1;
        // And the same row, split by WHO SAID SO. The two counts are disjoint by construction and
        // together they are exactly the feeds the statement line above is about, which is what lets
        // the row below say "3 of the 4" without a second walk or a second definition.
        if (anyDegradedThen) alreadyDegraded += 1;
        else freshlyDegraded += 1;
      }
    }

    if (statement) {
      statement.textContent = statementText(degradedFeeds, oldestLate, landed, windowed);
    }

    // Said after the statement line and derived from the same walk: where that sentence says what
    // is true of the FEEDS, this one says what is true of the PAGE, and a reader who has just read
    // "4 feeds have stopped meaning anything" is owed the second one immediately underneath.
    sayPageState(docAgeS, freshlyDegraded, alreadyDegraded, fastestWinS);

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
  // studio-live.js patches a freshly rendered table into the page and then hands over the instant the
  // server rendered it at. Two things follow from that and both are here rather than there: the
  // anchor moves to the server's instant — the fragment's ages were computed against it, and the
  // count must go on from where the server left it rather than from a browser clock that has been
  // drifting since page load — and the node lists are rebuilt, because that patch is the only thing
  // on this page that can add or remove a venue.
  //
  // The listener costs nothing when no stream is ever opened, which is the common case. Its own
  // event never fires and this file behaves exactly as it did before the button existed.
  document.addEventListener('csx-studio-live', (ev) => {
    const at = ev.detail && ev.detail.serverNow;
    if (Number.isFinite(at)) {
      serverNow = at;
      anchor = performance.now();
      // The fragments that just landed were rendered at this instant, so the document is new and
      // its age starts again from zero. This is the only thing on the page that may move it, and it
      // is why a reader who presses Live never sees the stale row: the document stops being old.
      renderedAt = at;
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
        localStorage.setItem('csx-studio-theme', night ? 'paper' : 'night');
      } catch (e) { /* Private mode. The choice holds for this page and is not remembered. */ }
      label();
      document.dispatchEvent(new CustomEvent('csx-studio-theme'));
    });
  }
})();
