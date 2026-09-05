# CryptoSmith X Studio — ingestion & storage rework

Brief for the coding agent. Target repository: `blyn-ai/CryptoSmith-X`, Studio / market-data hub.
Stack: .NET 10, PostgreSQL 16, raw SQL. Date of intent: 2026-09-05. Owner: Denisas.

Read this whole document, then the repository (schema, collectors, `product-vision.md`,
`recovery-playbook.md`), before writing any code. Exchange-specific facts below are
starting points, not truth: verify every channel name, message shape and limit against the
live exchange documentation and record what you found in the ADR (section 8).

---

## 0. Purpose and non-goals

The hub was built as a liquidity census: broad, cheap, lossy. It must become the evidence
base for strategy research. Two jobs, in this order:

1. **Verification by replay.** Any strategy — including the old `trading-bot` Momentum /
   Reversal logic — must be replayable against stored data so that at every decision time
   the replay sees exactly what a live process could have seen, no more and no less, and the
   resulting fills, fees, funding and P&L can be compared with what actually happened.
2. **Feature richness.** Strategy design must have access to order flow, book shape,
   cost-to-execute, funding state, open-interest dynamics and instrument lifecycle — not
   only "price went up / price went down".

Explicit **non-goals**: saving disk, saving CPU, simplifying, keeping tables small.
The host has ~10 TB SSD; storage is not the constraint. The constraints are exchange limits
and, above all, information that is lost at collection time and can never be recovered.

Out of scope: the `trading-bot` repository (read-only reference only), order execution,
ML, new frameworks. No ORM, no message bus, no workflow engine. Raw SQL, one migration
location, one DB per service — the existing invariants stand.

**Do not delete anything.** No retention job is to be written or scheduled in this rework.
Retention is decided after 30 days of measured volumes (section 9), and the default answer
is "keep".

---

## 1. Facts to start from (audit of 2026-09-05)

- Exchanges: Kraken Futures (276 instruments), WEEX Futures (1006), Hyperliquid (177).
  Binance USD-M planned. Public endpoints only.
- Ticker is fetched in bulk every 10 s but persisted once per minute: **5 of 6 observations
  are discarded**. These are non-replenishable (mark, bid/ask, sizes, OI, funding state).
- Depth is REST-polled per instrument every 60 s and stored only as band sums at
  10/25/50 bp. WEEX pass takes ~468 s, so WEEX depth is effectively every ~8 min and the
  tail of every pass is systematically staler than the head.
- Trades are not collected at all. Liquidations are not collected.
- Candles (1m) are REST-polled per instrument every 60 s — the same per-instrument budget
  as depth, spent on data most exchanges will hand back later.
- Hyperliquid REST: 1200 weight/min per IP; `l2Book` and `allMids` weigh 2,
  `metaAndAssetCtxs` and `candleSnapshot` weigh 20 (+ extra per 60 candles returned).
  Per-instrument candle polling = 177 × 20 = 3540/min, i.e. 3× the budget by itself; depth =
  177 × 2 = 354. `candleSnapshot` returns at most 5000 candles → 1m history reaches back
  only ~3.5 days. WS: max 100 connections, 1000 subscriptions, 2000 client messages/min;
  no batch subscriptions. `l2Book` levels carry `px`, `sz`, `n` (order count per level).
- Kraken Futures: WS `book` (snapshot + deltas with sequence), `trade`, `ticker`.
  Charts REST supports `tick_type` = trade | mark | spot. Market History API exposes public
  execution events, public order events and mark-price events with continuation tokens —
  retention window unknown, probe it.
- WEEX: WS exists on a separate contract host; depth and trade channels reportedly
  snapshot + incremental with update ids (verify names, sequencing rules, per-connection
  subscription limits). REST public default reportedly 10 req/s per IP.
- Current growth ~660 MB/day at ~195 bytes/row all-in. Persisting all 10 s ticker
  observations ≈ 1488 × 8640 ≈ 12.9 M rows/day ≈ 2.5 GB/day uncompressed. The VM's 256 GB
  data disk will not hold this; data must live on a host volume.

---

## 2. Invariants (must hold everywhere, tested)

1. **Never invent data.** `NULL` ≠ `0`. An absent observation is not an observation. An
   exchange outage produces no rows plus a gap record — never zeros, never carried-forward
   values.
2. **Append-only history.** No `UPDATE` / `DELETE` on observation tables. Exchange
   revisions and backfills are new rows with their own `known_from`. The only mutable
   table is the `*_latest` cache, which research code must never read.
3. **Bitemporal by default.** Every observation row carries `event_time` (what the data is
   about) and `known_from` (= `received_at`, when this process could first have used it).
   Point-in-time queries filter on **both**: `event_time <= t AND known_from <= t`.
   Derived rows carry `known_from = max(known_from of inputs) + compute latency`, and rollup
   buckets are addressable by bucket **end**.
4. **No silent forward-fill.** As-of lookups return `value + observed_at + age`. Freshness
   thresholds are a strategy decision, expressed in the strategy, never in SQL.
5. **Instrument identity** = (`exchange`, native id, `first_seen`). Symbols are labels.
   Specs (tick size, lot size, min notional, max leverage, contract multiplier, funding
   interval, status) are SCD2 versions, never overwritten.
6. **Raw is the asset; derived is a cache.** Every derived table has `derived_version` and
   a rebuild path from raw. Changing a formula = new version, full recompute, old version
   retained until explicitly dropped by a human.
7. **Units are explicit.** Every size/volume column states its unit (contracts / base /
   quote) in schema metadata; inverse (`PI_`) and linear (`PF_`) contracts are never mixed
   in one column without a unit tag.
8. **Time is UTC, from a disciplined clock.** NTP on the VM is verified and monitored;
   clock offset is logged per collection run.
9. **Coverage decisions are causal.** If any instrument ever receives less than full
   coverage (only when an exchange hard cap forces it), the decision is made by a rule
   evaluated at time t, recorded as a `promotion_event` with `reason` and `rule_version`,
   and is never applied retroactively.

---

## 3. Work packages, in priority order

Cheap-and-lossless first. WP1, WP2 and WP8 cost zero exchange budget and can ship in days.

### WP1 — Stop discarding ticker observations
- Persist every 10 s bulk-ticker observation for every instrument (rename/replace the
  minute history with a 10 s history; the minute layer becomes a rollup).
- Add to the snapshot everything the ticker already returns and we currently drop:
  predicted funding, next funding time, index price, mark price, 24 h volume and turnover,
  open interest, best bid/ask with sizes, last price and last-trade time if provided.
- Add the common observation columns (section 4.1).

### WP2 — Raw response archive ("black box recorder")
- Append-only, zstd-compressed NDJSON (or Parquet) of **every** raw payload: REST responses
  and every WS frame, partitioned `exchange / feed / yyyy-mm-dd / HH`.
- One record per message: `received_at`, `sent_at` (REST), `url` or `channel`, HTTP status /
  WS event type, byte length, body. Store the body verbatim.
- A `rebuild` tool that replays the archive through the current parsers into the parsed
  tables for a given exchange/feed/day and reports row counts and checksums. Parser bugs are
  discovered late; without this tool the affected weeks are lost.
- Never rotated.

### WP3 — Trades (highest-value missing feed)
- Subscribe to public trades over WS on Hyperliquid and Kraken Futures; WEEX after
  verification. Store every trade: trade id, `event_time`, price, size (+unit), notional,
  taker side, liquidation flag where the exchange provides it, sequence if provided.
- Backfill on Kraken via Market History public executions (continuation tokens); mark
  backfilled rows `source = backfill` with their real `known_from`.
- Derived flow layer at 1 s, 10 s, 1 m (and 1 h as distribution): trade count, buy/sell
  count, buy/sell notional, net taker notional and CVD, VWAP, realized volatility from
  trade prices, trade-size distribution (mean, median, p90, p99, max), inter-trade time
  (median, max), burstiness (e.g. share of trades in the busiest 10 % of the bucket),
  liquidation count/notional where known. `derived_version` on every row.

### WP4 — Book over WS, full shape retained
- Per instrument: WS snapshot + deltas with sequence validation; local book maintained in
  memory; on any gap → record `collector_gap`, resync (REST or resubscribe), and never
  fabricate the interval in between.
- Persist three things:
  1. raw deltas → archive (WP2);
  2. reconstructed **top-N levels** (N = 20 or the exchange maximum) as arrays
     `bid_px[], bid_sz[], bid_n[], ask_px[], ask_sz[], ask_n[]` every 1 s (tier A) and no
     coarser than 10 s elsewhere, plus on every resync; `n` is `NULL` where the exchange
     does not provide it — never estimated;
  3. a feature layer at the same cadence (and rolled up to 1 m / 1 h as distributions):
     spread (bp), mid, microprice, top-of-book sizes, depth at ±5/10/25/50/100 bp,
     level count per band, largest level size and its distance (bp), concentration
     (HHI over levels in ±50 bp), bid/ask imbalance at ±5/10/25/50 bp, book slope,
     **cost-to-execute** (average fill price and slippage in bp) for a notional ladder
     [50, 100, 250, 500, 1000, 2500, 5000, 10000, 25000] quote units, both sides,
     book persistence (share of ±25 bp depth at t still present at t+1 s / t+10 s),
     update count per bucket.
- REST depth is demoted to bootstrap, resync and sanity checks. Any REST polling that
  remains runs in randomized order with jitter and records per-instrument `received_at`.
- Do not subdivide coverage unless an exchange hard cap forces it. Budget check first:
  Hyperliquid 177 × (l2Book + trades) = 354 of 1000 subscriptions; Kraken — verify cap;
  WEEX 1006 × 2 — likely needs several connections; find the real per-connection limit.

### WP5 — Candles become canonical, not primary
- Keep 1 m candles as the exchange-canonical layer, fetched lazily in batches (e.g. every
  15–60 min, last N bars, with overlap) — except Hyperliquid, where the 5000-candle cap
  requires at least hourly fetches.
- Add Kraken **mark-price** candles (`tick_type=mark`) — stops and liquidations trigger on
  mark, and trade candles on Kraken are empty 87 % of minutes.
- Columns: `open_time`, `close_time`, `is_final`, `source`, `known_from`. Unclosed bars are
  never written to the canonical table.
- Rollups tf > 1 remain as cache, versioned and rebuildable.

### WP6 — Funding as a decision input
- Predicted funding, current funding, next funding time in every 10 s snapshot (WP1).
- Per-instrument funding schedule (interval, settlement times, sign convention, rate
  basis) as an SCD2 spec; realized history as today, with `known_from`.

### WP7 — Discovery as lifecycle, not state
- `instrument_spec_version` (SCD2: `valid_from`, `valid_to`, `known_from`) for every spec
  field; `instrument_status_event` (listed, halted, reduce-only, delisting announced,
  delisted, re-listed). Never overwrite the current row. Native id + `first_seen` is the key.

### WP8 — Collection health, first-class
- `collector_run`: feed, exchange, instrument (nullable for bulk), started/finished,
  transport (rest/ws), status, HTTP status, latency, request weight used, clock offset.
- `collector_gap`: instrument, feed, `gap_start`, `gap_end`, cause (429, timeout, WS
  gap, resync, exchange maintenance, collector down).
- Every rollup bucket carries `expected_count`, `actual_count`, `gap_count`,
  `max_gap_seconds`. "Market was dead" and "we were blind" must be separable in SQL.

### WP9 — Rollups as distributions, resolution ladder
- Replace hourly means with distributions: spread (median, p90, p95, p99, max), depth
  (median, p10, min), cost-to-execute per ladder step (median, p90, p99), OI
  (open/high/low/close), imbalance (mean + percentiles), flow (buy/sell notional, CVD,
  count, active seconds), plus the health block from WP8.
- Ladder: event/raw → 1 s → 10 s → 1 m → 1 h. Nothing jumps from 1 m straight to 1 h.
  Everything at 1 m and coarser is permanent; finer layers are also kept until section 9
  says otherwise.

### WP10 — Point-in-time query layer
- One documented API (SQL functions and a thin .NET facade): `as_of(instrument, feed, t)`
  → latest row with `event_time <= t AND known_from <= t`, plus `age`;
  `universe_as_of(t)`; `book_as_of(instrument, t)`; `candles_closed_before(instrument, t)`;
  `flow_window(instrument, t_from, t_to)` with the same knowledge-time filter.
- No other path into historical tables from research or replay code.
- Any query crossing a resolution boundary (e.g. from 1 s to 1 m data) must report the
  resolution used per segment or refuse.

### WP11 — Replay harness and parity test
- A harness that walks decision times, serves only PIT views (WP10), applies a cost model
  built from stored book state (cost-to-execute for the order's notional, taker fees,
  funding accrued over the holding period) and produces fills and P&L.
- **Parity test**: replay the old bot's recorded decisions and trades (export from the
  `trading-bot` database, provided by the owner — do not modify that repo) for the last
  45 days on Kraken. Compare universe, signal values, entry/exit prices within the measured
  slippage distribution, fees, funding and P&L. Every mismatch is classified as a data
  gap, a knowledge-time leak, or a cost-model error, and listed in a report.
  This test is the acceptance criterion for the whole rework.

### WP12 — Exchange budget governance
- Per-exchange budget accounting (Hyperliquid weights computed locally; WEEX/Kraken from
  response headers where available), subscription registry per connection, connection
  pooling for WS, 429 and disconnect accounting into WP8.
- Verify all limits against live documentation before implementation; write findings to
  the ADR with dates.

---

## 4. Schema guidance (adapt to the existing schema; keep names consistent)

### 4.1 Common observation columns
`exchange`, `instrument_id`, `event_time`, `exchange_ts` (nullable), `received_at`,
`persisted_at`, `known_from` (= `received_at` unless backfilled), `source`
(`rest` | `ws` | `backfill` | `archive_rebuild`), `source_seq` (nullable),
`collector_run_id`.

### 4.2 Tables (new or changed)
- `market_snapshot_10s` — full ticker state per observation (WP1, WP6).
- `market_snapshot_1m`, `_1h` — rollups with distributions (WP9).
- `trade` — every public trade (WP3).
- `flow_1s`, `flow_10s`, `flow_1m`, `flow_1h` — trade-derived features (WP3), `derived_version`.
- `book_topn` — reconstructed top-N arrays with `is_snapshot`, `seq` (WP4).
- `book_feature_1s`, `_10s`, `_1m`, `_1h` — book features (WP4, WP9), `derived_version`.
- `market_candle` — add `close_time`, `is_final`, `source`, `known_from`; add `price_type`
  (`trade` | `mark`).
- `instrument_spec_version`, `instrument_status_event`, `funding_schedule` (WP6, WP7).
- `collector_run`, `collector_gap` (WP8).
- `feature_definition` (`id`, `name`, `version`, description/formula reference) referenced
  by every `derived_version`.
- `promotion_event` — only if coverage is ever tiered (invariant 9).

### 4.3 Archive layout
`/data/archive/{exchange}/{feed}/{yyyy-mm-dd}/{HH}.ndjson.zst`, one JSON object per
message with the envelope from WP2. Rebuild tool: `rebuild --exchange --feed --date`.

Partition all high-volume tables by day. Prefer `double precision` / `bigint` scaled
integers over `numeric` for hot columns; keep exact strings in the archive.

---

## 5. Look-ahead traps that come from storage shape (each needs a test)

- Rollup keyed by bucket start visible before bucket end.
- Candle labeled by open time joined with `<= t` — leaks the unclosed bar.
- Backfilled rows without `known_from` → the replay sees data the bot never had.
- Mutable instrument row → 2026 backtest runs with 2027 specs.
- Universe "as of now" applied to the past → look-ahead on listings and survivorship on
  delistings.
- Nearest-after interpolation, bucket averages treated as bucket-start values, LOCF.
- `ORDER BY id` as a time order under parallel collectors.
- Cross-exchange joins on one shared timestamp while per-instrument `received_at` differs
  by minutes (the WEEX tail problem).
- Rotation/resolution boundary silently changing the data resolution mid-backtest.
- Sliding 24 h windows differenced to get "last hour" volume.
- Reading `*_latest` from research code.

---

## 6. Acceptance tests (must exist and pass)

- **T1 PIT leak**: a row with `event_time < t < known_from` is invisible to `as_of(t)`.
- **T2 Candle finality**: no unclosed bar in `market_candle`; `candles_closed_before(t)`
  never returns a bar with `close_time > t`.
- **T3 Rollup availability**: bucket `[a, b)` invisible at any `t < known_from`, where
  `known_from >= b`.
- **T4 Universe as-of**: instrument listed at L and delisted at D appears only for
  `L <= t < D` by knowledge time.
- **T5 Gap semantics**: an injected outage yields no rows and a `collector_gap`; no zeros.
- **T6 No LOCF**: an OI observation 2 h old is returned with `age = 2h`; the freshness
  helper rejects it at a 30 s threshold.
- **T7 Book resync**: injected sequence gap → gap recorded, resync, no fabricated levels.
- **T8 Archive rebuild**: drop one day's parsed partition, rebuild from archive, checksums
  equal.
- **T9 Cross-exchange staleness**: `as_of` across exchanges reports per-instrument age;
  a test asserts the WEEX tail is flagged, not silently aligned.
- **T10 Units**: mixing inverse and linear sizes without a unit tag fails at the schema
  level.
- **T11 Replay parity** (WP11): report generated; mismatch classes enumerated; the number
  of unexplained mismatches is the metric tracked across PRs.

---

## 7. Process

1. Read the repository, the current schema and the two docs named above. Write down the
   current per-feed rows/day and bytes/day as the baseline.
2. Verify exchange specifics live (channels, message shapes, sequencing, limits, history
   depth for candles / executions / mark events). Probe Kraken Market History retention and
   Hyperliquid candle depth empirically. Record results with dates in the ADR.
3. Ship in this order: WP1 → WP2 → WP8 → WP3 → WP4 → WP5 → WP6 → WP7 → WP9 → WP10 → WP11
   → WP12. Each WP is a reviewable PR (or a small series) with migration, code, tests.
4. Every change to running collectors goes into the existing change journal.
5. Old data: keep the existing tables read-only under their current names; do not migrate
   old rows into new semantics silently — old rows lack `known_from` and top-N shape, and
   the schema must say so (e.g. `known_from = persisted_at` with `source = 'legacy'`).
6. After 7 days on the new ingestion, produce a measurement report: rows/day and bytes/day
   per table and per archive feed, WS message rates per exchange, gap statistics, budget
   utilization. Update the "where we are" table in `product-vision.md` with facts only.

---

## 8. Deliverables

- ADR: `docs/adr/00X-market-data-capture.md` — decisions, verified exchange facts (dated),
  open questions.
- Migrations, collectors, PIT query layer, replay harness, tests T1–T11.
- Archive writer and rebuild tool.
- Measurement report after 7 days (section 7.6).
- Parity report from WP11.

---

## 9. What is explicitly deferred

- Any retention or downsampling policy (decide after 30 days of measured volumes; default
  is keep).
- Liquidation feeds that require anything beyond a free WS subscription.
- Full-book (beyond top-N) reconstruction snapshots — only if a concrete hypothesis needs
  them, and then via WS deltas already in the archive.
- Binance USD-M — design the schema so it drops in, do not implement now.
- L3 / order-event reconstruction on Kraken — probe availability, do not build.
