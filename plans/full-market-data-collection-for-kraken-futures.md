Task: full market-data collection for Kraken Futures in CryptoSmith-X —
each dataset separately switchable, parameters editable in the admin,
data visible in Studio. Nothing is enabled by default; enabling is an
operator action after deploy. Reference: /audit/collector-inventory.md
(commit 6ac83df2). Every file, table and line named below comes from it;
re-verify against HEAD before editing.

HARD RULES (repo invariants, do not negotiate):
- Never invent: NULL ≠ 0; an outage writes no rows and a gap record.
- Append-only observation tables; no UPDATE/DELETE on them; *_latest
  tables are the only mutable ones and never a research source.
- Every stored observation carries `received_at` (when we saw it) and,
  for backfilled rows, `known_from` (when we fetched it) + `source`.
- Units explicit; a value carried from a previous observation is never
  written as a fresh one.
- Raw SQL + Dapper, no ORM; migrations only under
  src/CryptoSmithX.Database/Migrations, one place applies them.
- TreatWarningsAsErrors; net10.0; existing tests keep passing.
- Public endpoints only; no API keys anywhere.
- Do not change behaviour of existing datasets (snapshot, depth,
  candles, funding, discovery, rollup) except where stated.
- Do not touch the 12 NOT NULL columns of market_snapshot_latest in this
  task; do not delete anything; do not fix PROD's missing `fake` segment
  here (flag it in the summary).

PHASE 0 — DESIGN CHECKPOINT (stop and report before coding)
Produce /plans/kraken-full-collection.md with: every new table's DDL;
the dataset → collector → table → admin group → Studio surface map;
every new dataset_setting key with kind, default and one-line meaning;
the estimated rows/day and bytes/day at defaults for the Kraken
universe (276 PF_ instruments; state the assumptions); the WS
subscription plan (channels, chunking, gate); which
IExchangeMarketData extension you chose (optional interfaces
ISupportsX vs default interface methods — recommended: optional
interfaces + Capabilities entry, so WEEX/Hyperliquid/Binance/Fake are
untouched and stay we_implement=false). Wait for approval of the DDL.

PHASE 1 — SCHEMA AND POLICY (migration 0030+; 0028 and 0029 were taken
on 2026-09-07 and PROD and TEST are both at 0029; the
migrator applies in order; do not renumber existing files)
1.1 New dataset rows (kind='feed', default_mode='disabled', sort_order
    after existing): `book`, `candles_mark`, `candles_index`,
    `spec_versions`. Existing `trades`, `open_interest`, `liquidations`
    keep their codes; set their default_interval_s and settings.
    Full-matrix invariant: one segment_dataset row per (segment, new
    dataset), mode 'disabled', pattern 0014_collections.sql:218-227;
    segment_dataset_capability rows per new cell, pattern :252-254.
1.2 Ticker extension: nullable columns on market_snapshot_latest AND
    market_snapshot: `funding_rate_predicted` (relative fraction, same
    unit as funding_rate), `next_funding_at timestamptz`,
    `last_trade_at timestamptz`, `volume_24h_base numeric`. Nullable
    means "venue did not give it"; never default them.
1.3 `trade` — partition by range (event_time), monthly, created by
    Partitions.EnsureCurrentAndNextAsync (extend it; keep "no default
    partition"). Columns: exchange_instrument_id, event_time, venue_uid
    text, seq bigint null, price numeric, qty numeric, qty_unit text
    ('base'|'contract'), taker_side ('buy'|'sell'), trade_type text null
    ('fill'|'liquidation'|'partial_liquidation'|'termination'|'block'),
    received_at, source ('ws'|'rest_recent'|'backfill'), known_from
    timestamptz null. PK (exchange_instrument_id, event_time, venue_uid);
    BRIN on event_time. Normalise Kraken's two spellings to one enum;
    backfilled rows have trade_type NULL (the executions API carries no
    marker) — document that in the column comment.
1.4 `book_topn` — partition by range (observed_at), monthly. Columns:
    exchange_instrument_id, observed_at (venue timestamp from the WS
    frame, NOT our clock — fixes §9.2.6 for this table), received_at
    (ours), seq bigint, is_snapshot bool, levels smallint, bid_px
    numeric[], bid_qty numeric[], ask_px numeric[], ask_qty numeric[],
    bid_n int[] null, ask_n int[] null, qty_unit text. PK
    (exchange_instrument_id, observed_at, seq).
1.5 `open_interest_history`: exchange_instrument_id, bucket_time,
    interval_s, oi_open, oi_high, oi_low, oi_close numeric, oi_unit
    text, source ('analytics'), received_at, known_from. PK
    (exchange_instrument_id, interval_s, bucket_time). Not partitioned.
1.6 `market_price_candle`: exchange_instrument_id, series text check in
    ('mark','index'), timeframe smallint, open_time, open, high, low,
    close numeric, received_at, known_from, is_final bool. PK
    (exchange_instrument_id, series, timeframe, open_time). Partition
    monthly by open_time. Volume is intentionally absent (venue sends
    "0"). No rollup in this task.
1.7 `liquidation_volume_history`: exchange_instrument_id, bucket_time,
    interval_s, volume numeric, volume_unit text, source ('analytics'),
    received_at, known_from. PK (exchange_instrument_id, interval_s,
    bucket_time).
1.8 `instrument_spec_version` (SCD2): id, exchange_instrument_id,
    valid_from, valid_to null, spec_hash text, spec jsonb (the
    normalised spec fields, not raw_json), raw_json jsonb, known_from.
    Unique (exchange_instrument_id, valid_from). Written by
    DiscoveryCollector on every pass where spec_hash differs from the
    open version; closes the previous row. Venue-agnostic — applies to
    all segments once enabled, but the dataset cell gates it.
1.9 Funding schedule: add `funding_interval_source text check in
    ('venue','measured','assumed')` next to
    exchange_instrument.funding_interval_hours; Kraken sets
    'measured' from the realized series (median gap), others keep
    their current value tagged 'assumed'. Do not make the column
    nullable in this task.
1.10 dataset_setting rows (kind int unless stated): trades/universe_top_n
    (50; 0 = all trading instruments), trades/backfill_hours (24),
    trades/deep_backfill (0|1, default 0 — uses Market History
    executions with continuation tokens), book/levels (25),
    book/universe_top_n (50), book/min_write_interval_s (10),
    open_interest/interval_s (60), open_interest/backfill_hours (720),
    candles_mark/backfill_hours (24), candles_index/backfill_hours (24),
    liquidations/interval_s (3600), liquidations/backfill_hours (720),
    spec_versions/enabled_note (text, informational). Universe rule for
    top_n: rank by market_snapshot_latest.turnover_24h at loop start,
    recomputed each pass, plus every instrument where
    exchange_instrument.collect is explicitly true by an operator
    (collect_changed_by not null).
1.11 Grants: select to studio_reader on every new table (pattern
    0026_arena_metric_hour.sql:100). Column comments on every new
    column stating unit and clock.

PHASE 2 — CONNECTOR (src/CryptoSmithX.MarketData.Connectors/Kraken)
2.1 DTOs: bind fundingRatePrediction, next_funding_rate_time (WS,
    epoch-ms absolute instant — the doc says "ms until", the wire is
    absolute; assert on the value magnitude and log once if it ever
    looks relative), lastTime (REST only), vol24h/volume. Make new DTO
    fields nullable; do not add zero defaults. REST predicted funding
    is absolute → divide by mark like KrakenFuturesMarketData.cs:116-122;
    WS gives relative directly.
2.2 WS: subscribe `trade` and `heartbeat` alongside ticker/book
    (KrakenWsFeed.cs:321). Parse trade_snapshot (100) and trade deltas
    with seq per product; on seq discontinuity emit a per-instrument
    gap event (cause ws_sequence_gap) and continue. Give KrakenWsFeed
    the VenueGate for its own REST calls (parity with WEEX/Binance).
    Use heartbeat + IdleTimeout to emit ws_disconnected / resync events
    the Hub can persist.
2.3 Book: expose TopN(symbol, n) from KrakenBookBuilder returning
    (seq, is_snapshot_since_last, venue timestamp, levels) or null when
    dirty/unseeded; the WS frame timestamp must be kept (today it is
    discarded at KrakenWsFeed.cs:204-205).
2.4 REST/charts client: parametrise tick_type ('trade'|'mark'|'spot');
    add analytics client for open-interest and liquidation-volume
    (epoch-second timestamps, decimal strings, page cap 2000,
    more:true continuation, allowed intervals only — reject others
    client-side); add `/derivatives/api/v3/history` (recent trades)
    and Market History `/api/history/v3/market/{symbol}/executions`
    with since/before/sort/count≤1000/continuation_token. All new
    calls go through the VenueGate; read Retry-After on 429 if present.
2.5 Fix QtyStep for negative contractValueTradePrecision
    (KrakenFuturesMarketData.cs:215-224): step = 10^(-precision) for
    negative values; add a test with precision −3 → 1000.
2.6 Capabilities: declare book/trades/open_interest/liquidations/
    candles_mark/candles_index/spec_versions on the Kraken adapter with
    TransportsUs; other adapters unchanged (inert cells stay inert).
2.7 Fixtures + tests: book_snapshot, book delta with seq gap,
    trade_snapshot, trade deltas with both liquidation spellings,
    ticker with next_funding_rate_time, analytics pages (2000 cap,
    more:true), charts mark/spot, executions page with continuation.

PHASE 3 — HUB (src/CryptoSmithX.MarketData.Hub)
3.1 Add the codes to KnownCollectorDatasets and BuildBodies; one
    CollectorLoop per (segment, dataset) as today; hot-reload semantics
    unchanged (30 s cache + reconcile).
3.2 TradeCollector: drains the WS trade buffer every interval_s
    (default 5) in one transaction, append-only, on conflict do
    nothing; on start pulls /history (source='rest_recent') for the
    universe; if deep_backfill=1 walks executions backwards within
    backfill_hours (source='backfill', trade_type NULL, known_from=now).
    Writes collector_gap with exchange_instrument_id for WS gaps.
3.3 BookCollector: every interval_s (default = book/min_write_interval_s)
    reads TopN for the universe and writes a row only when seq changed
    since the last written seq for that instrument; dirty book → no
    row + open per-instrument gap (cause resync) closed on next good
    row. Never copies a previous row forward.
3.4 OpenInterestCollector, LiquidationVolumeCollector: analytics
    backfill on first run within backfill_hours, then incremental from
    the last bucket; source='analytics', known_from=now.
3.5 MarkCandleCollector / IndexCandleCollector: same shape as
    CandleCollector but tick_type mark/spot into market_price_candle;
    unclosed bars never written (is_final only).
3.6 DiscoveryCollector: spec_version writing per 1.8 (gated by the
    spec_versions cell) and funding_interval_source per 1.9.
3.7 Health: WS feeds now report runs (transport='ws') and gaps; write
    collector_gap.exchange_instrument_id where the gap is
    per-instrument; write collector_run.http_status for REST calls and
    clock_offset_ms from ticker.time minus local time at each snapshot
    pass. Add producers for ws_sequence_gap, ws_disconnected, resync.
3.8 Tests: CollectorSelectionTests updated; new collector tests with
    FakeTimeProvider covering: outage → no rows + gap; seq gap →
    gap row with instrument; unchanged book → no write; backfilled
    trade has known_from and NULL trade_type.

PHASE 4 — ADMIN (src/CryptoSmithX.WebApp.Admin)
4.1 _DataFeedsPanel groups: "Market state" unchanged; new "Trades"
    (trades, liquidations → trade, liquidation_volume_history), "Book"
    (book → book_topn), "Open interest" (open_interest →
    open_interest_history), "Bars" adds candles_mark/candles_index →
    market_price_candle, "Instruments" adds spec_versions →
    instrument_spec_version. Nothing may fall into `unproduced` for
    kraken-futures after this task.
4.2 Edit-feed dialog works unchanged (mode/interval/keep/retention/
    transport/note) — verify the loss guard wording still makes sense
    for book and trades.
4.3 Parameters UI: the Datasets page gets an editable "Parameters"
    table for dataset_setting rows (validate by kind, updated_by =
    current user, updated_at), same anti-forgery and role rules as
    ExchangesController. This is the only new admin surface.
4.4 ExchangeStore run-contents switch and empty-run notes for each
    new collector; Runs.cshtml filter list; per-instrument gaps shown
    on the instrument page (they exist in the table now).
4.5 Tests for the parameters form (kind validation, rejects unknown
    key).

PHASE 5 — STUDIO (src/CryptoSmithX.WebApp.Studio)
Do NOT add columns to the 17-column pair table. Add sections on the
pair page (pattern §6.5 "as a section"), each with data-live-region
and a LiveRelevance case, each showing the age of the call that wrote
it and a dash where nothing was measured:
5.1 Funding: realized last 24 h per venue (from funding_rate_history —
    grant it to studio_reader), predicted rate and next funding time
    from the ticker row, interval with its source tag.
5.2 Trades: per venue, last 20 trades (time, side, price, qty, type
    mark for liquidations), trades/min over the last hour, and the
    number of gap records in that hour; from `trade`.
5.3 Book: per venue, top-10 of the stored top-N with seq, observed_at
    and age; "not collected" state when the cell is disabled.
5.4 Open interest: 1-minute series last 24 h from
    open_interest_history next to the existing hourly line.
5.5 Mark/index candles: a toggle on the existing candle panel
    (trade|mark|index) reading market_price_candle when present.
5.6 Design rules from src/web/ds-studio/readme.md apply; the glossary
    at Views/Pairs/Pair.cshtml gets one entry per new word
    (seq, liquidation, backfilled). Statement "17 fields · ages live"
    stays true — do not change the count.
5.7 Tests: PublicQueryTests for every new SQL (guards `collect`,
    status <> 'delisted', segment enabled, not fake), Live tests for
    the new regions.

PHASE 6 — DOCS, DEPLOY, RUNBOOK
6.1 docs/datagaps.md and docs/recovery-playbook.md updated: what is
    now recoverable (trades to 2022 via executions but without
    liquidation marks; OI to 2023-03; funding ~1 year; mark/index
    candles per venue retention) and what is not (book, live
    liquidation marks, ticker fields).
6.2 A runbook section: enabling order for an operator on TEST —
    spec_versions → candles_mark/index → open_interest →
    liquidations → trades (universe 50, no deep backfill) → book
    (levels 25, interval 10, universe 50); what to watch after each
    (collector_status, gaps, disk, rows/day vs the Phase 0 estimate).
6.3 Deploy to TEST only. PROD is seven migrations behind and runs
    pre-rename images; leave PROD alone and list what PROD needs
    first.
6.4 Summary: what was built, every default, what remains from the
    brief (WP2 raw archive, WP10 PIT facade, NOT NULL relaxation,
    PROD fake segment) — explicitly out of scope, not forgotten.

Commit per phase. After Phase 0 stop for approval; after Phase 3 stop
and report measured rows/day on TEST with each dataset enabled for one
hour, one at a time, then disabled again.
