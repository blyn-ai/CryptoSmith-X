# Handoff: Admin · Data feeds, Edit feed, Collections

## Overview
Three screens implementing the model in `plans/design-brief-data-feeds.md`: a **collection** is a kind
of data from a catalogue (nine of them), and every exchange has a row for **every** collection,
always. The current `Collectors` panel on the exchange page only shows loops that have run at least
once, so "the venue cannot", "we have not written it" and "a human switched it off" all render as the
same emptiness. These screens make those three answers three different rows.

Three axes are kept visually and structurally separate everywhere:

| Axis | Meaning | Source of truth |
| --- | --- | --- |
| **Capability** | what the venue serves and what we implement — a fact, with provenance | `exchange_collection_capability` |
| **Policy** | what a human decided: mode, interval, retention, primary transport | `exchange_collection` |
| **Health** | what we observe on this render | `collector_status` (live schema, not in the proto) |

## About the design files
The files in this bundle are **design references written as HTML** — prototypes of the intended look
and behaviour, not production code to paste in. The target environment exists: **ASP.NET Core MVC
Razor**, hand-written `wwwroot/app.css`, tokens under `wwwroot/ds/tokens/`. Recreate the design in
Razor + `app.css` with the classes and tokens already in the repo. No CSS framework, no component
library, no JS framework — the one interactive screen needs ~40 lines of vanilla JS, described below.

The prototype uses inline styles in several places where the real implementation should add a class
to `app.css`. Every such case is listed in **New CSS** at the end.

## Fidelity
**High fidelity.** Every colour, size and string below is exact and comes from the repo's own tokens.
Data values come from `plans/csx-proto-seed.sql` (Kraken Futures) — the policy rows for
Hyperliquid / Binance / WEEX on the Collections screen are **invented placeholders** and must be
replaced with real ones.

## Target files
| What | Where |
| --- | --- |
| Data feeds panel (replaces `Collectors`) | `src/CryptoSmithX.WebApp/Areas/Admin/Views/Exchanges/Details.cshtml` |
| Edit feed dialog | new partial, suggested `Areas/Admin/Views/Exchanges/_FeedDialog.cshtml` |
| Collections index | new `Areas/Admin/Views/Collections/Index.cshtml` + `CollectionsController` (Area `Admin`) |
| Nav entry | `ViewComponents/SideNavViewComponent.cs` — group **Operations**, after `Exchanges` |
| Styles | `src/CryptoSmithX.WebApp/wwwroot/app.css` |
| Tokens (read-only) | `src/CryptoSmithX.WebApp/wwwroot/ds/tokens/*.css` |
| Schema | `plans/csx-proto.sql`, `plans/csx-proto-seed.sql` |

---

## Screen 1 · Data feeds
`Areas/Admin/Views/Exchanges/Details.cshtml`, overview tab — replaces the `Collectors` panel in
place. The page head, tabs and stat row above it are unchanged except for two strings (below).

### Purpose
Answer, per collection: is it collecting, and if not — **why not**, in words, not as a blank cell.

### Above the panel (changes only)
- Stat row: the `Collecting` stat is new — `.ex-stat` with `<i>Collecting</i><b>6 of 9 feeds</b>`
  (count of rows whose `mode <> 'disabled'` and `we_implement = true`).
- Page head `.code` string becomes `"0 consecutive failures, max across feeds"` (was *collectors*).

### Panel
`.panel.scroll`, `margin-bottom:14px`.
`.panel-h`: `<h2>Data feeds</h2>` + `.meta` = `exchange_collection · 9 of 9 collections`.

**Column header** — `.th`, `min-width:900px`,
`grid-template-columns:164px 184px 188px minmax(190px,1fr) 92px`.
Labels, verbatim: `Feed` · `Capability · fact` · `Policy · decided` · `Health · observed` · (empty).
The axis names are in the header on purpose — they are the whole point of the screen.

**Row** — `.tr` (add `.is-fail` when a collecting feed has `consecutive_failures >= 3`), same grid
and `min-width`, `align-items:start`, `padding:11px 18px`.

1. **Feed** — flex row, `gap:9px`: status dot, then a column (`gap:4px`) of
   `.mono` name (`line-height:1.2`) and `.code` = `{collection.code}` (+ ` · derived` when
   `collection.kind = 'derived'`).
2. **Capability** — column, `gap:5px`:
   - `.code` in `var(--text-muted)`: `venue yes · us yes`. `no` when false, `—` when the capability
     row is absent (never established). Derived collections print `derived · us yes`.
   - `.code` history line: `history {value} · {source}` (e.g. `history none · manual`), or
     `history not established`. Colour: `var(--csx-gold-ink)` when the value is `none`,
     otherwise `var(--text-faint)`. This gold is the visual precondition of the guard in screen 2.
3. **Policy** — column, `gap:6px`, `align-items:flex-start`:
   - mode tag: `.tag.enabled` for `collect`, `.tag.planned` for `on_demand`, `.tag.disabled` for
     `disabled`. Label text: `collect` / `on demand` / `disabled`.
   - `.code` in `var(--text-muted)`: `{transport} · {interval} · {retention}`, e.g. `ws · 10 s · 90 d`.
     Transport prints `internal` for derived collections. Retention prints `never` when the effective
     value is null. Whole line is a single `—` when mode is `disabled`.
4. **Health** — column, `gap:5px`: `.sev.{ok|warn|fail}` or `.sev.none`, then a `.code` line with
   `white-space:normal;text-wrap:pretty`. Four cases, and they must stay four:

   | Case | `.sev` text | Line | Dot |
   | --- | --- | --- | --- |
   | collecting | `ok` / `degraded` / `failing` | `last success 7 s · 0 fails · 96 / 104 ms` (muted; `var(--csx-gold-ink)` when not ok) | `.dot.ok.fresh` / `.dot.warn` / `.dot.fail` |
   | backlog (venue true, us false) | `backlog` | `The venue serves it, we have not written the collector. Nothing is being lost that the venue would not still hold.` | `.dot.paused` |
   | not established (no capability row) | `not established` | `Never probed — we do not know whether the venue serves it. This is an answer, not an error.` | `.dot.none` |
   | off by hand | `off by hand` | `exchange_collection.note`, e.g. `Arrives inside snapshot: no separate loop, and none needed` | `.dot.paused` |

   `.sev.none` (faint) for the three non-collecting cases — a backlog is not a severity, and gold must
   stay reserved for operational weight.
5. **Actions** — flex, `gap:12px`, `justify-content:flex-end`: `runs →` (`.linkbtn`, `font-size:10px`,
   only for collecting feeds) and `edit` (`.linkbtn`, `font-size:10px`, opens screen 2).

**Legend** — `dl.legend` inside the panel, `margin:0;padding:13px 18px`, three `dt/dd` pairs:
`capability` → *what the venue offers and what we implement — a fact, with its source*;
`policy` → *what a human decided: mode, interval, retention, preferred transport*;
`health` → *what we observe right now; never stored on the row*.

**Below the panel** — `.panel-note`, `padding:0;max-width:78ch`:
*Every collection has a row, always — an empty panel used to mean three different things at once: the
venue cannot, we have not written it, or a human switched it off. Those are now three different rows.*

### Row order
`collection.sort_order` (10…90): snapshot, depth, candles, funding, discovery, rollup, trades,
open_interest, liquidations. Do not sort by health — the list must be positionally stable.

---

## Screen 2 · Edit feed dialog
The brief's culmination. Modal over screen 1, one feed at a time.

### Frame
Overlay: `position:fixed;inset:0;z-index:20;background:var(--surface-overlay)`,
`display:flex;align-items:flex-start;justify-content:center;padding:44px 20px;overflow-y:auto`.
Card: `width:min(880px,100%)`, `background:var(--surface-raised)`,
`border:1px solid var(--border-strong)`, `border-radius:var(--radius-lg)`, `overflow:hidden`.

**Header** — `padding:17px 20px`, `border-bottom:1px solid var(--border-hairline)`,
flex `space-between`, `gap:18px`. Left column `gap:7px`: `.eyebrow` `Edit feed · {exchange code}`;
row `gap:11px` with `<h2 style="font:var(--type-h3)">{collection.name}</h2>`, the live mode tag, and
`.tag.dashed` `derived` when applicable; then `collection.description` at
`400 12px/1.55 var(--font-body)`, `var(--text-muted)`, `max-width:62ch`, `text-wrap:pretty`.
Right: `Close` (`.linkbtn`).

**Body** — `display:grid;grid-template-columns:minmax(0,340px) minmax(0,1fr);gap:1px;
background:var(--border-hairline)` — the 1px gap draws the divider. Both columns
`background:var(--surface-raised);padding:16px 20px 20px`.

### Left column — Capability (read-only)
Header row: `.eyebrow` `Capability · fact` and `.code` `read-only`.

Nine rows in `capability_key.sort_order`, each `padding-bottom:10px` with
`border-bottom:1px solid var(--border-hairline)`, two lines:
- line 1, baseline `space-between`: key label (mono `9.5px`, `.1em`, uppercase, `var(--eyebrow)`) and
  the value (`.mono`, `12px`). Labels used: `Venue serves it`, `We implement it`,
  `Transports · venue`, `Transports · us`, `API versions · venue`, `API versions · us`,
  `History depth`, `Auth`, `Rate limit`.
- line 2, flex `gap:8px`: the **provenance tag** and `.code` `{filled_by} · {filled_at}`.

Provenance is the point of this column — a value without a source is an opinion:

| `source` | Tag | Rationale |
| --- | --- | --- |
| `declared` | `.tag` (neutral) | asserted by code at build time |
| `probed` | `.tag.planned` (violet) | measured against the venue |
| `manual` | `.tag.maintenance` (gold) | a human put it there — gold already means "tended by hand" |
| row absent / `value is null` | `.tag.dashed`, text `no source`, meta `never filled` | not established |

Value colours: `var(--text-data)` normally; `var(--text-faint)` when not established;
`var(--csx-gold-ink)` for `history_depth = none` (`capability_key.loss_relevant`).

Footer line, `400 11px/1.5 var(--font-body)`, `var(--text-faint)`:
*A value without a source is an opinion. "Not established" is an honest answer, not a failure.*

### Right column — Policy (editable)
Header row: `.eyebrow` `Policy · decided by a human` and `.code` `last edited by {updated_by} · {updated_at}`.

**1 · Mode** — `.eyebrow` `Mode`, then `.chips` (`margin-bottom:13px`) with three chips:
`disabled` · `on demand` · `collect`. Selected chip gets `.is-on`.
Both `on demand` and `collect` are **disabled** (`opacity:.45;cursor:not-allowed`) when
`we_implement <> true`, with `title="No collector for this feed yet — both collect and on demand need one"`.
Both need the same collector; blocking only one reads as arbitrary.

**2 · The loss guard — at the point of press, not as a footnote.**
Condition: the feed was `collect` **and** the new mode is not `collect` **and** the effective
`history_depth` is `none` or not established. Then, immediately under the mode chips:

- `.err` block, `margin:0 0 12px`: `<b>Loss</b>`, `.t` =
  `{Name} history does not exist — history depth is none (manual, denis · 31.08). Whatever is not
  collected while this feed is off is gone forever: there is nowhere to backfill it from.`
  `.m` = `Stopping now also drops the live loop for 274 instruments. The archive keeps what it
  already has; the gap starts at this click.`
- `.field` with label `Type {collection.code} to release the guard` and a text input
  (`autocomplete="off"`, `spellcheck="false"`, placeholder = the code).
- **Save stays disabled** until `trim().toLowerCase() === collection.code`. Disabled style:
  `opacity:.4;cursor:not-allowed`. Server-side check as well, exactly like
  `ExchangesController.Status` does with the exchange code today.
- Save label becomes `Stop collecting` whenever the change stops collection (guard or not).

Counter-case, same position — when history is **not** `none` (candles, funding), print a calm note
instead, `padding:10px 12px`, `border:1px solid var(--border-hairline)`,
`border-radius:var(--radius-sm)`, `400 11.5px/1.5 var(--font-body)`, `var(--text-muted)`:
*History depth is full, probed by dump probe on 22.08 — a gap made now can be backfilled later.
Stopping is reversible for this feed.* The guard must read as data-driven, not decorative.

**3 · Primary transport** — options are the **intersection** of `transports_venue` and
`transports_us`, never more:
- ≥2 options → `.chips` with one chip per transport.
- exactly 1 → no control at all: a static line `400 12.5px/1.4 var(--font-mono)`,
  `var(--text-data)` with the transport name. Not a dropdown.
- 0 → no control; the note explains it.

Note line under it (`.code`, `white-space:normal`), one of:
- `Venue rest, ws · we implement rest, ws. This picks the primary; the WS→REST fallback is always on and cannot be switched off.`
- `Only rest — venue rest, we implement rest. Not a choice, so not a dropdown. WS→REST fallback is always on: it is protection against loss, not a setting.`
- `No transport both sides have: venue not established, us none.`
- `Derived series — nothing is fetched, so there is no transport to choose.`

**No fallback toggle anywhere.** Per the brief: it is protection against data loss, not a setting.

**4 · Interval and retention** — `display:grid;grid-template-columns:repeat(auto-fit,minmax(190px,1fr));gap:0 16px`,
one `.field` each (`Interval (s)`, `Retention (days)`), `margin-bottom:14px`.
Empty input = inherit; placeholder shows what is inherited (`collection 60`, or
`collection: never rotate`).

Under each input, the **cascade strip** — this is what answers "where does this value come from":
`display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:1px;background:var(--border-hairline)`,
`border:1px solid var(--border-hairline)`, `border-radius:var(--radius-xs)`, `overflow:hidden`.
Three cells (`padding:7px 9px`, `background:var(--surface-raised)`), labels `own` · `collection` ·
`global`, each with its value (`.mono`, `11.5px`) or `—`. Resolution order is own → collection →
global; the **first non-null cell is the effective one**:
- effective and it is `own` → `var(--csx-gold-ink)` (an override, same gold as the existing
  "overrides global 10" hint on the settings form);
- effective and inherited → `var(--text-data)`;
- all other cells → `var(--text-faint)`.

Cell label styling: `400 9px/1 var(--font-mono)`, `.1em`, uppercase, `var(--text-faint)` — do **not**
reuse `.code` here, its `overflow:hidden;text-overflow:ellipsis` truncates "collection".

`retention` null in `collection` means *never rotate* (a law for candles), not *inherit* — print
`never`, and do not fall through to the global.

**5 · Note** — `.field`, label `Note`, placeholder `why this venue departs from the default`,
bound to `exchange_collection.note`.

### Footer
`padding:14px 20px`, `border-top:1px solid var(--border-hairline)`, flex `gap:16px`.
Left (flex:1): the newest `capability_log` entry as `.code` (`white-space:normal`) —
e.g. *01.09 12:28 · we_implement false → true · deploy 6424c23 — WS book collector: snapshot +
deltas with seq check; depth freshness across 274 pairs fell from ~60 s to 7 s.* — plus
`capability log →` (`.linkbtn`, `font-size:10px`). When the feed has no entries: `No entries — this
feed has never been probed.`
Right: `Cancel` (`.linkbtn`) and `Save changes` (`.btn.btn-sm`).

### Interactions / state
Vanilla JS, delegated on `document` so it survives `live.js` swapping `<main>` (same pattern as the
Lifecycle guard already in `Details.cshtml`):

| State | Trigger | Effect |
| --- | --- | --- |
| `mode` | mode chip click | re-evaluates guard vs. calm note, Save label, tag in the header |
| `transport` | transport chip click | `.is-on` moves |
| `interval` / `retention` | input | cascade `own` cell and its colour update live |
| `guardCode` | input | enables Save on exact match with the collection code |
| dialog open | `edit` in a row | loads that feed's policy into the form |

Server: `POST /Admin/Exchanges/Feed/{code}` with `collection`, `mode`, `intervalS`, `retentionDays`,
`transport`, `note`, `confirmCode`; validate the guard again, write `exchange_collection`, and append
to `capability_log` only when a capability value actually changed (policy edits are not capability).

---

## Screen 3 · Collections
New top-level section, nav group **Operations**, right after `Exchanges`.

`.page-head`: `.eyebrow` `Admin · operations`, `<h1>Collections</h1>`, `.page-note`
*The catalogue of data kinds. Defaults live here; a venue row appears only where a human departed
from them.*

Cards: `display:grid;grid-template-columns:repeat(auto-fit,minmax(430px,1fr));gap:14px`.
One `.panel` per collection, in `sort_order`:
- `.panel-h`: `<h2>{name}</h2>` + `.tag.dashed` `derived` when applicable; `.meta` = `{code}`.
- `.panel-b` (`padding:14px 18px 12px`, column, `gap:13px`): the catalogue description at
  `400 12.5px/1.55 var(--font-body)`, `var(--text-body)`, `text-wrap:pretty`; then a flex row
  `gap:26px` of three `.ex-stat`: `Default mode`, `Interval`, `Retention`
  (`never` when `default_retention_days is null`).
- `.th` `grid-template-columns:132px 92px minmax(0,1fr)`: `Venue` · `Mode` · `Departs from default`.
- One `.tr` per venue (`padding:9px 18px`, `align-items:center`): `.mono` name at `12px`, the mode
  tag, then a `.code` note (`white-space:normal;text-wrap:pretty`):
  - a recorded reason (`exchange_collection.note`) → `var(--text-muted)`;
  - same as default, nothing recorded → `follows the default`, `var(--text-faint)`;
  - departs from the default with **no** reason recorded → `mode {x} — no reason recorded` in
    `var(--csx-gold-ink)`. An unexplained deviation is the one thing on this screen worth chasing.

Data: one query joining `collection` × `exchange_collection` for exchanges with
`status = 'enabled'`. **The Hyperliquid / Binance / WEEX rows in the prototype are invented** —
only `kraken-futures` is seeded.

---

## Design tokens used
Colours, all existing: `--surface-page`, `--surface-card`, `--surface-raised`, `--surface-overlay`,
`--border-hairline`, `--border-card`, `--border-strong`, `--border-gold`, `--tint-gold`,
`--text-heading`, `--text-body`, `--text-muted`, `--text-faint`, `--text-data`, `--eyebrow`,
`--csx-gold-ink`, `--gold-300`, `--violet-400`, `--lilac-500`, `--link`, `--action-primary`,
and the lifecycle palette `--status-{planned,enabled,disabled,maintenance,abandoned}(-wash|-border)`.
Radii: `--radius-xs` 4 (chips, cascade strip), `--radius-sm` 6 (inputs, calm note),
`--radius-md` 8 (panels), `--radius-lg` 12 (dialog).
Type: `--type-h1`, `--type-h3`, `--type-card-title`, `--type-eyebrow`, `--type-data`,
`--font-mono` for every number, label and code.
No new colours, no icons, no emoji. `—` for "not measured".

## New CSS (add to `app.css`, replace the prototype's inline styles)
```css
/* Data feeds: one row per collection × exchange, the three axes side by side */
.r-feed{grid-template-columns:164px 184px 188px minmax(190px,1fr) 92px;min-width:900px}
.feed-cell{display:flex;flex-direction:column;gap:5px;min-width:0}
.feed-cell .why{white-space:normal;text-wrap:pretty}

/* Edit feed dialog */
.modal-scrim{position:fixed;inset:0;z-index:20;background:var(--surface-overlay);
  display:flex;align-items:flex-start;justify-content:center;padding:44px 20px;overflow-y:auto}
.modal{width:min(880px,100%);background:var(--surface-raised);border:1px solid var(--border-strong);
  border-radius:var(--radius-lg);overflow:hidden}
.modal-h{display:flex;align-items:flex-start;justify-content:space-between;gap:18px;
  padding:17px 20px;border-bottom:1px solid var(--border-hairline)}
.modal-b{display:grid;grid-template-columns:minmax(0,340px) minmax(0,1fr);gap:1px;
  background:var(--border-hairline)}
.modal-b > div{background:var(--surface-raised);padding:16px 20px 20px}
.modal-f{display:flex;align-items:center;gap:16px;flex-wrap:wrap;padding:14px 20px;
  border-top:1px solid var(--border-hairline)}
@media (max-width:820px){.modal-b{grid-template-columns:minmax(0,1fr)}}

/* capability row: value + provenance */
.cap-row{display:flex;flex-direction:column;gap:5px;padding-bottom:10px;
  border-bottom:1px solid var(--border-hairline)}
.cap-row .k{display:flex;align-items:baseline;justify-content:space-between;gap:10px}
.cap-row .k i{font-style:normal;font:var(--fw-medium) 9.5px/1 var(--font-mono);letter-spacing:.1em;
  text-transform:uppercase;color:var(--eyebrow)}
.cap-row .p{display:flex;align-items:center;gap:8px}

/* cascade strip: own → collection → global, effective step highlighted */
.cascade{display:grid;grid-template-columns:repeat(3,minmax(0,1fr));gap:1px;
  background:var(--border-hairline);border:1px solid var(--border-hairline);
  border-radius:var(--radius-xs);overflow:hidden}
.cascade span{display:flex;flex-direction:column;gap:4px;padding:7px 9px;background:var(--surface-raised)}
.cascade i{font-style:normal;font:400 9px/1 var(--font-mono);letter-spacing:.1em;
  text-transform:uppercase;color:var(--text-faint)}
.cascade b{font:400 11.5px/1 var(--font-mono);color:var(--text-faint)}
.cascade .is-eff b{color:var(--text-data)}
.cascade .is-own b{color:var(--csx-gold-ink)}
```

## Bug found in the repo while building this
`wwwroot/theme-light.css:71` — `html[data-theme="light"] .dot.fresh{animation-name:csxFreshLight` is
missing its `;` and `}`, so the whole paper `--status-*` block on lines 72–77 is swallowed by that
rule and never reaches `html[data-theme=light]`. On the light theme `.tag.enabled` therefore stays
`#41EDA0` on white (≈1.6:1) instead of `#1E7A55`. Affects every screen with lifecycle or mode tags,
not just these. The fixed file is in this bundle.

## Assets
None new. Brand marks come from `wwwroot/ds/assets/` (`cryptosmith-mark.svg`,
`cryptosmith-mark-paper.svg`), already in the repo.

## Files in this bundle
| File | What |
| --- | --- |
| `Admin Data Feeds.dc.html` | the three screens, interactive (sidebar switches screens, `edit` opens the dialog) |
| `support.js` | runtime for the prototype only — do not port |
| `src/CryptoSmithX.WebApp/wwwroot/app.css` | the repo's stylesheet as of this design |
| `src/CryptoSmithX.WebApp/wwwroot/theme-light.css` | with the brace bug fixed |
| `src/CryptoSmithX.WebApp/wwwroot/ds/` | tokens + brand marks the prototype loads |
| `_ds/` | design-system tokens and fonts, so the prototype renders offline |

Open `Admin Data Feeds.dc.html` in a browser. Prototype states worth checking before you start:
`depth` → `edit` → `disabled` (the guard), `candles` → `edit` → `disabled` (the calm counter-case),
`liquidations` → `edit` (everything not established, both live modes blocked), and the theme toggle
in the header for the paper theme.
