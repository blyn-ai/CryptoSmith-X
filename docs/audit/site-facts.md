# Site facts — audit of 2026-09-06

Scope: `bykovas/trading-bot` and `blyn-ai/CryptoSmith-X`, their code and their production
databases. Read-only throughout; all SQL was `select`.

Hosts checked: `38.242.248.83` (`bykovas-contabo-vps`, ssh alias `csx-prod`) runs every
container of both systems. `38.242.207.16` (`lauresta-contabo-vps`, ssh alias `contabo`)
runs **no** trading-bot or CryptoSmith-X container and holds **no** such image — it is an
infrastructure host (authentik, pihole, guacamole, traefik, wg-portal, meshcentral,
uptime-kuma, vector, coturn); `docker images | grep -iE "trading|crypto|smith|byko"`
returns nothing and `docker ps -a` lists 21 containers, none of them either system.
`dev-vm` (10.11.12.14) has no docker on the probed path. So both systems exist on exactly
one host, and the numbers below are from it.

Deployed versions at audit time: futures workers on image tag
`d1fcfec05b2566aa9eb1f8835bdd3b62598b8e2b`, `worker_build_utc` 2026-09-01T12:08:12Z
(`select worker_commit, worker_build_utc from dry_run_cycles`); trading-bot repo HEAD
`ecf232d` (2026-09-04) — the three commits between are diagnostics workflow files only.
CryptoSmith-X containers run floating `:latest` tags, images created 2026-09-06T18:43:31–41Z;
repo HEAD `4e927e0` (2026-09-06).

---

# Inventory

**A1 — Instances running now.** Two: `futures-live` (Kraken Futures, live real fills, first
cycle 2026-07-27 04:14:28Z, 26,875 cycles) and `futures-lukas-live` (Kraken Futures, live,
first cycle 2026-07-30 18:39:27Z, 12,992 cycles). Three are stopped, all last cycle
2026-08-21 07:48–07:49Z: `futures-virtual`, `spot-live`, `spot-virtual`. (`select
bot_instance_id, min(utc), max(utc), count(*) from dry_run_cycles group by 1`; live status
from `dry_run_actions.fill_source='REAL'` — 452 rows futures-live, 158 lukas; `docker ps` on
csx-prod.)

**A2 — Strategies with live activity in the last 45 days.** Exactly one: **Momentum**, first
live entry 2026-08-28 11:16:00Z; 155 entries on futures-live, 47 on futures-lukas-live.
Entries before that date carry `strategy = null` (133 futures-live, 46 lukas). No second
strategy name has ever been written. (`select bot_instance_id, strategy, count(*), min(utc)
from dry_run_actions join dry_run_decision_facts using (cycle_id, decision_index) where
action like 'WOULD_OPEN%' and fill_price>0 group by 1,2`.)

**A3 — Decision log.** Table `dry_run_decision_facts`, 4,279,041 rows, first row 2026-07-27
04:14:26.149958Z, last 2026-09-06 18:46Z. 35 columns per decision: pair, price, fast_ema,
slow_ema, rsi, desired_position, score, short_score, long/short thresholds,
minimum_ema_gap_percent, risk_approved, entry_rejection_reason, spread_percent,
price_action_direction/trend, bullish/bearish structure flags, ema_gap_velocity,
allows_short, early-entry fields. Risk-gate rejection text lives in
`dry_run_decision_risk_reasons` (4,284,339 rows, one row per reason). Coverage over the last
24 h (119,904 rows): score and risk_approved 100 %, ema/rsi/short_score 99.99 %,
entry_rejection_reason 98.4 %. (`information_schema.columns`; counts as shown.)

**A4 — Public API.** 15 endpoints declared in `src/TradingBot.Api/Program.cs`:
`/api/bot-instances`, `/api/bot-status`, `/api/cycles`, `/api/cycles/{cycleId}`,
`/api/dashboard`, `/api/decisions`, `/api/entry-diagnostics`,
`/api/export/cycles-and-snapshots.csv`, `/api/health`, `/api/market-snapshots`,
`/api/portfolio`, `/api/positions`, `/api/public-stats`, `/api/simulate`,
`/api/trade-cycles`. Probed live: `algo.bykovas.lt` 301-redirects to `blynai.bykovas.lt`;
there `/api/health` 200, `/api/bot-instances` 200, `/api/positions` 200 (14,318 B);
`/api/public-stats` returns **503** on that host but 200 on `fin.bykovas.lt`.

**A5 — Markets per cycle and measured cycle duration, last 7 days.** futures-live min 1,
median 102, max 116 over 5,118 cycles; futures-lukas-live min 1, median 75, max 88 over
5,069 cycles. Measured interval between consecutive cycles: median **120.0 s** both
instances, p95 120.1 s, max 1,499 s (futures-live) / 360 s (lukas). (`dry_run_cycle_facts.
active_pairs_count`; `lag(utc) over (partition by bot_instance_id order by utc)`.)

**A6 — Manual interventions and copied entries.** No manual-intervention marker exists
anywhere: `portfolio_position_state.origin` takes only the value `BOT`, and
`dry_run_actions.exit_trigger_source` only null/exchange/mark/bid/ask. Copied entries are
marked, as `entry_channel = 'Mirror'`: 36 live entries, all on futures-live, between
2026-08-19 14:38:00Z and 2026-08-24 19:12:19Z — that is **zero since commit 0edf9bb**.
`futures_entry_mirror_commands` holds 21 rows. Note the premise of the question: 0edf9bb
(2026-08-24 21:21:19 +0300, "Begin the own-strategy experiment: mirror off, control vs
treatment") **switched the mirror off**; it did not introduce marking. (`git show
--stat 0edf9bb`; SQL as above.)

**A7 — Uptime, gaps > 10 min in cycle timestamps, last 45 days.** futures-live 22 gaps,
1,502.0 min total, longest 1,150.2 min. futures-lukas-live 4 gaps, 28,262.8 min total,
longest 27,305.7 min (18.96 days). Stopped instances for reference: futures-virtual 10 /
1,280.0 min, spot-live 15 / 1,332.9 min, spot-virtual 16 / 1,353.1 min.

**A8 — CryptoSmith-X exchanges writing rows in the last 24 h.** Four:
`kraken-futures`, `weex-futures`, `hyperliquid`, `binance-usdm` — all four with snapshot
writes at 2026-09-06 18:50Z. `binance-usdm` is the newest: first snapshot row 2026-09-06
12:29:22Z, first candle 2026-09-06 00:00Z, funding back to 2026-08-30 13:00Z. The `fake`
segment wrote only `rollup` runs (status `disabled`, no external traffic). No other exchange
wrote a row. (`collector_run` grouped by segment/collector over 24 h; row counts joined
through `exchange_instrument.segment_code`.)

**A9 — Per exchange, per dataset.** Nominal intervals come from `dataset`
(snapshot poll 10 s / keep 60 s, depth 60 s, candles 60 s, funding 3600 s, discovery 3600 s)
with two overrides in `segment_dataset`: weex-futures snapshot poll 30 s, binance-usdm depth
300 s. Trades, open_interest and liquidations are `disabled` on all five segments.

| segment | dataset | earliest | latest | rows | measured median interval |
|---|---|---|---|---|---|
| kraken-futures | snapshot | 2026-09-01 13:05:43Z | 2026-09-06 18:50:03Z | 2,076,086 | 60.00 s (p95 70.0) |
| kraken-futures | candles (Sep) | 2026-09-01 00:00Z | 2026-09-06 18:49Z | 2,724,842 | — |
| kraken-futures | funding | 2026-08-25 14:00Z | 2026-09-06 18:00Z | 80,428 | — |
| kraken-futures | depth | (in snapshot) | 2026-09-06 | — | 60.2 s (p95 68.5, max 123.9) |
| weex-futures | snapshot | 2026-09-01 17:53:57Z | 2026-09-06 18:50:10Z | 7,205,850 | 52.40 s (p95 93.2) |
| weex-futures | candles (Sep) | 2026-09-01 00:00Z | 2026-09-06 18:44Z | 8,327,362 | — |
| weex-futures | funding | 2026-08-25 20:00Z | 2026-09-06 18:00Z | 54,228 | — |
| weex-futures | depth | (in snapshot) | 2026-09-06 | — | 82.7 s (p95 110.5, max 669.7) |
| hyperliquid | snapshot | 2026-09-02 04:17:04Z | 2026-09-06 18:50:03Z | 1,175,058 | 59.97 s (p95 70.7) |
| hyperliquid | candles (Sep) | 2026-09-02 00:00Z | 2026-09-06 18:47Z | 1,552,391 | — |
| hyperliquid | funding | 2026-08-26 05:00Z | 2026-09-06 18:00Z | 49,064 | — |
| hyperliquid | depth | (in snapshot) | 2026-09-06 | — | 81.1 s (p95 96.6, max 172.3) |
| binance-usdm | snapshot | 2026-09-06 12:29:22Z | 2026-09-06 18:50:09Z | 208,888 | 60.21 s (p95 74.9) |
| binance-usdm | candles (Sep) | 2026-09-06 00:00Z | 2026-09-06 18:44Z | 404,487 | — |
| binance-usdm | funding | 2026-08-30 13:00Z | 2026-09-06 18:00Z | 21,894 | — |
| binance-usdm | depth | (in snapshot) | 2026-09-06 | — | 353.7 s (p95 391.0, max 1000.7) |

Snapshot medians measured over the last 60 min per instrument; depth medians over the last
6 h on distinct `depth_at` per instrument. August partitions hold only `fake` rows (9,969
candles, 4,180 snapshots) — no real venue has data before 2026-09-01.

**A10 — Depth storage.** Band aggregates only. `market_snapshot_*` carries
`depth_bid_10bps, depth_ask_10bps, depth_bid_25bps, depth_ask_25bps, depth_bid_50bps,
depth_ask_50bps, depth_at`. Three bands per side: 10, 25 and 50 bps. No raw-level table
exists in the schema. (`information_schema.columns`; `book_topn` absent.)

**A11 — Trades.** Not collected, and never were. `dataset` row `trades` has
`default_mode = disabled` and every `segment_dataset` row for it reads `disabled`; there is
no `trade` table. The `dataset.description` records the reason: "A different order of volume
— needs its own storage decision before it can be turned on." Trades are WP3 in the brief and
are listed there as missing as of the 2026-09-05 audit. (`select * from dataset`;
`plans/studio-data-rework-brief.md:126`.)

**A12 — Ticker/snapshot retention.** Verified in code and measured. Code path:
`src/CryptoSmithX.MarketData.Hub/Ingestion/SnapshotCollector.cs:8-12` and `:56-71` —
`market_snapshot_latest` is upserted on every pass, the same rows are appended to history
only once per `history_interval_s` bucket. `history_interval_s` defaults to 60 while the
poll interval is 10 s. Commit `e59e1ff` (2026-09-05) turned that constant into a database
setting and states in its own message: "The collector polled every ten seconds and kept one
observation a minute, so five of every six … stopped existing anywhere at all"; it also
states the default stays 60, "so the migration changes nothing on its own". Measured over
three full hours (`collector_run.items` received vs rows written): kraken-futures 4.79
received per stored (79.1 % dropped), hyperliquid 4.71 (78.8 %), binance-usdm 3.58 (72.1 %),
weex-futures 1.38 (27.7 % — its poll interval is 30 s, not 10 s). So the September claim is
directionally right and slightly overstated for three venues, and materially wrong for WEEX.

**A13 — Instrument registry.** 2,258 instruments: trading 2,042 (weex 1,005, binance 566,
kraken 276, hyperliquid 177, fake 18), halted 150 (binance 131, weex 18, fake 1), delisted 65
(hyperliquid 56, weex 5, kraken 4), post_only 1. Lifecycle columns populated:
`first_seen_at` 2,258/2,258, `last_seen_at` 2,258/2,258, `status_changed_at` 2,258/2,258,
`listed_at` **997/2,258**. Only the latest status change is kept — there is no
`instrument_status_event` table. Discovery runs 26 times per segment per 24 h.

**A14 — Adapters, classified from code.**

| Venue on the website | Classification | Evidence |
|---|---|---|
| Hyperliquid | collecting | `src/CryptoSmithX.MarketData.Connectors/Hyperliquid/` (6 files), segment `hyperliquid` enabled, rows in last 24 h |
| Kraken Futures | collecting | `.../Kraken/KrakenFuturesMarketData.cs` + 7 files, segment `kraken-futures` enabled, rows in last 24 h |
| Kraken Spot | nothing in the repo | only `Kraken/KrakenFutures*`; no spot client; no spot segment (`segment.kind` is `perp` for all five) |
| WEEX | collecting | `.../Weex/` (10 files), segment `weex-futures` enabled, rows in last 24 h |
| Binance perp | collecting | `.../Binance/BinanceUsdmMarketData.cs` + 9 files, segment `binance-usdm` enabled, first rows 2026-09-06 |
| Binance spot | nothing in the repo | only `BinanceUsdm*`; no spot client, no spot segment |
| OKX | nothing | `grep -ril okx src/` → 0 files |
| Bybit | nothing | `grep -ril bybit src/` → 0 files |
| Coinbase | nothing | `grep -ril coinbase src/` → 0 files |
| Deribit | nothing | `grep -ril deribit src/` → 0 files |
| Gate | nothing | `grep -ril "gate\.io\|gateio" src/` → 0 files (the 37 "gate" hits are `Pacing/VenueGate.cs`, a rate limiter) |
| MEXC | nothing | `grep -ril mexc src/` → 0 files |
| Bitget | nothing | `grep -ril bitget src/` → 0 files |

Plus `Fake/FakeExchangeMarketData.cs`, an in-process test venue, segment status `disabled`.

**A15 — Collection health.** Yes, per feed. `collector_status` (segment × collector) carries
`last_attempt_at, last_success_at, last_error_at, last_error, consecutive_failures,
instruments_expected, last_duration_ms, avg_duration_ms, watermark_at` — 26 rows, all four
live segments × 5 collectors. `collector_run` logs every pass (116,159 rows), `collector_gap`
logs what was missed (295 rows: hyperliquid/snapshot rate_limited 287, binance/snapshot
rate_limited 5, binance/depth rate_limited 4, weex/depth error 1). Exposed in the admin UI at
`src/CryptoSmithX.WebApp.Admin/Areas/Admin/Views/Exchanges/` (`_DataFeedsPanel`, `_LatencyPanel`,
`_StalestPanel`, `_ThroughputPanel`, `Runs.cshtml`).

**A16 — Public API gateway with keys/quotas.** None. No API-key authentication, quota or
tenant rate-limit code exists in `CryptoSmith-X/src` (`grep -rniE
"apikey|api_key|x-api-key|quota|ApiKeyAuth"` returns only prose in Studio comments).
`tenant` has 0 rows, `webapp_user` 1.

**A17 — WP1–WP12 from `plans/studio-data-rework-brief.md`.**

| WP | Status | Evidence |
|---|---|---|
| WP1 stop discarding ticker observations | partial | `e59e1ff` made the interval a setting; default stays 60 s, so 72–79 % is still dropped (A12) |
| WP2 raw response archive | not started | no archive table; `raw_archive` absent from `information_schema.tables` |
| WP3 trades | not started | `trade` table absent; dataset `trades` disabled on all segments |
| WP4 book over WS, full shape | not started | `book_topn`, `book_feature_1s` absent; only 3 bands per side stored (A10) |
| WP5 candles canonical | partial | `market_candle` partitioned by month and populated; no `_1m`/`_1h` rollup tables |
| WP6 funding as decision input | partial | `funding_rate` in every snapshot and `funding_rate_history` populated; no predicted funding, no next-funding-time column, `funding_schedule` absent |
| WP7 discovery as lifecycle | partial | status/first_seen/last_seen/status_changed on the instrument; `instrument_status_event` and `instrument_spec_version` absent, so change history is not retained |
| WP8 collection health | done | `868ae00` "record what was not observed", `2ae2745` "finish WP8 — the health columns are filled and the gaps are on screen"; `collector_run`/`collector_gap`/`collector_status` all present and populated (A15) |
| WP9 rollups as distributions | partial | `market_metric_hour` (179,046 rows) holds averages and counts; no distribution columns, no resolution ladder tables |
| WP10 point-in-time query layer | not started | one view exists in the whole schema, `instrument_v`; no PIT views |
| WP11 replay harness and parity test | not started | no file matching `*replay*`/`*pit*`/`*parity*` in `src/` or `tests/` |
| WP12 exchange budget governance | partial | `exchange.request_budget_per_s`, `max_concurrent_requests`, `request_budget_source` populated for all 5, and `Pacing/VenueGate.cs` exists; but `collector_run.http_status`, `request_weight` and `clock_offset_ms` are **0/6,873 populated** over the last 6 h, so 429/disconnect accounting is not landing |

26 schema migrations applied, versions 1–26, 2026-08-31 20:28Z to 2026-09-06 18:44Z.

**A18 — Agent.** No `Agent` project exists in either repository (`find src -maxdepth 1 -type
d -iname '*agent*'` → nothing in both). The only artefact is a UI mock,
`src/CryptoSmithX.WebApp.Admin/wwwroot/ui-mocks/agent.html`. The previous generation is
`trading-bot/src/archive/TradingBot.SpotWorker` (archived; its instances `spot-live` and
`spot-virtual` last cycled 2026-08-21 07:49Z). Bots registered on the platform: **0**
(`select count(*) from bot` → 0; `bot_event` 0; `bot_policy` 0).

**A19 — Studio.** Exists and is deployed, contrary to the brief's expectation. 202 files,
**4,771 lines of C#** under `src/CryptoSmithX.Studio/`; container `cryptosmithx-studio` is
`Up`, image `ghcr.io/blyn-ai/cryptosmithx-studio:latest` created 2026-09-06T18:43:35Z; the
traefik router was committed as `64901c3`. It owns no tables — it reads the market-data
schema. The public-site design document `plans/studio-public-site.md` (started 2026-09-06)
opens with "Ничего не реализовано", which describes the redesign, not the running service.

---

# Findings

**F1 — Per-trade counting flatters the live result, but only by about a tenth.**
- Numbers: futures-live, last 45 days — per trade −0.4556 %/trade (n = 284, sd 1.3095);
  per instrument-day −0.5094 %/instrument-day (n = 254, sd 1.3698). Ratio 0.894, i.e.
  per-trade counting understates the loss by 10.6 % relative, 0.054 pp absolute.
  futures-lukas-live: −0.3529 %/trade (n = 81) vs −0.3573 %/day (n = 80), 1.2 % relative.
- Method: every `WOULD_CLOSE` with `fill_price>0 and entry_price>0` in the last 45 days;
  return signed by side from recorded entry and fill prices; a flat 0.10 % round-trip taker
  fee subtracted; clustering by `(pair, date(utc))`.
- Sample: 284 closes over 254 instrument-days (futures-live); 81 over 80 (lukas).
- What would have falsified it: a ratio far from 1 — the ledger's earlier research figure was
  +0.09 %/trade against −0.30 %/coin-day, a sign flip. Nothing like that appears live.
- Why the effect is small here, and this is the substance of the finding: the live book
  averages **1.12 closes per instrument-day** (284/254), so there is almost nothing to
  cluster. The large inflation factor recorded in research came from a book that re-entered
  the same coin many times a day; the live book does not.
- Evidence: SQL over `dry_run_actions ⨝ dry_run_decision_facts`, run 2026-09-06.

**F2 — There is no entry edge after costs on either side, and both sides are significantly
negative.**
- Numbers: futures-live, last 45 days, per instrument-day, net of 0.10 % round trip —
  LONG −0.5449 % (118 instrument-days, sd 1.4597, t = −4.055); SHORT −0.4429 %
  (147 instrument-days, sd 1.2752, t = −4.211). Per trade: LONG −0.5144 % (n = 125),
  SHORT −0.4094 % (n = 159).
- Method: as F1, split by `side`; one-sample t against zero on the instrument-day series
  (t = mean/sd × √n), which is the unit that removes within-day repetition.
- Sample: 284 closes, 265 side-instrument-days, 2026-07-23 → 2026-09-06.
- What would have falsified it: either side's instrument-day mean at or above zero, or |t| < 2.
  Both sides are below zero at |t| > 4.
- Caveats that belong with the number: spread is already inside the recorded fill prices, but
  funding is **not** subtracted — `portfolio_position_state.funding_paid_eur` is null for both
  live instances, so funding cost is unmeasured and the true expectancy is at or below these
  figures. The 0.10 % fee is the observed rate (`fee_eur/filled_notional_eur` ≈ 0.05 % per side).
- Evidence: SQL run 2026-09-06; consistent with `.ai/strategy-challengers.md:32-34`
  ("gross edge at entry is ~zero").

**F3 — Between 72 % and 79 % of the ticker observations the collectors receive are never
stored, and one venue's figure is a quarter of that.**
- Numbers, over three full hours: kraken-futures 4.79 received per row stored (79.1 %
  dropped, 237,636 → 49,658), hyperliquid 4.71 (78.8 %, 150,096 → 31,860), binance-usdm 3.58
  (72.1 %, 365,070 → 101,880), weex-futures 1.38 (27.7 %, 250,245 → 180,900).
- Method: `sum(collector_run.items)` where `collector='snapshot'` against rows appended to
  `market_snapshot_2026_09`, both restricted to the same three whole-hour window, joined to
  segment through `exchange_instrument`.
- Sample: 2,617 collector runs, 1,002,047 observations received, 364,298 stored.
- What would have falsified it: a ratio near 1. WEEX at 1.38 is the near-miss and it has a
  mechanical cause — its poll interval is configured at 30 s against a 60 s keep bucket, so it
  can only ever drop about half.
- The September audit's "five of six" (83.3 %) is therefore an overstatement for all four
  venues, and wrong by a factor of three for WEEX.
- Evidence: SQL run 2026-09-06; code path `SnapshotCollector.cs:56-71`; commit `e59e1ff`.

**F4 — WEEX depth arrives at 1.5× to 7.7× its nominal interval, and the shortfall has been
there for the whole life of the feed.**
- Numbers: nominal 60 s (`dataset.default_interval_s`, no WEEX override). Measured median
  interval between distinct `depth_at` values per instrument: **460.2 s on 2026-09-05**
  (102,521 deltas) and **93.2 s on 2026-09-06** (401,941 deltas). Over the last 6 h,
  median 82.7 s, p95 110.5 s, max 669.7 s across 1,005 instruments.
- Method: `distinct (exchange_instrument_id, depth_at)` from the September snapshot
  partition, `lag()` within instrument and day, median of the deltas.
- Sample: 504,462 intervals across 1,005 instruments, the entire depth history of the feed —
  `depth_at` is populated only from 2026-09-05, so two days is all there is.
- What would have falsified it: a median at or under 60 s on either day.
- Comparison on the same query: kraken-futures 60.2 s against nominal 60 s, hyperliquid
  81.1 s against 60 s, binance-usdm 353.7 s against its own 300 s override.
- Evidence: SQL run 2026-09-06.

**F5 — The "markets scanned" figure on the site is a sum over two instances that scan
overlapping universes; the distinct count is 104, not 184.**
- Numbers: `/api/public-stats` returned `marketsNow: 184` at 2026-09-06 18:59:04Z. In the
  latest cycle of each live futures instance the union of distinct pairs is **104** while the
  sum of the two per-instance counts is **180**. Median pairs per cycle over 7 days:
  futures-live 102, futures-lukas-live 75.
- Method: the endpoint computes `running.Sum(instance => instance.ActivePairsCount)` over
  instances not matching `spot-%` or `%virtual%` (`Program.cs:510-518`, `:546-553`); measured
  against `count(distinct pair)` over both instances' latest cycles.
- Sample: 2 instances, 1 cycle each, 180 decision rows.
- What would have falsified it: a union close to the sum, i.e. two instances scanning
  disjoint universes.
- Evidence: live HTTP call; `src/TradingBot.Api/Program.cs`; SQL run 2026-09-06.

**F6 — The decisions counter includes the retired dry-run instances.**
- Numbers: `/api/public-stats` returned `decisionsTotal: 4,279,221`;
  `select count(*) from dry_run_decision_facts` returned 4,279,041 one minute earlier.
  Excluding `%virtual%` the count is **3,067,100** — the counter carries 1,211,941 rows from
  `futures-virtual` (1,159,327) and `spot-virtual` (52,614), plus 53,590 from the retired
  `spot-live`.
- Method: read the endpoint, read the table, compare; then read the implementation.
  `ReadDecisionsTotal` issues `select count(*) from dry_run_decision_facts` with no instance
  predicate (`Program.cs:576-580`).
- Sample: the whole table.
- What would have falsified it: a WHERE clause on `bot_instance_id`, or the endpoint value
  matching the live-only count.
- Evidence: HTTP call and SQL, both 2026-09-06; `src/TradingBot.Api/Program.cs:571-580`.

**F7 — The Reversal strategy has never produced a live entry, so nothing can be said about it.**
- Numbers: **0** live entries. Across all 458 live entries ever recorded, `strategy` takes two
  values only — `null` (256) and `Momentum` (202); `entry_channel` takes Breakout 180,
  Standard 174, ShortBreakdown 38, ShortReclaim 21, Mirror 17, Reclaim 19, Continuation 7,
  ShortContinuation 2. No Reversal row exists in either column.
- Method: `select strategy, entry_channel, count(*) from dry_run_actions where action like
  'WOULD_OPEN%' and fill_price>0 group by 1,2`.
- Sample: every live entry since 2026-07-27, 458 rows over 42 days.
- What would have falsified it: any row carrying the strategy.
- The finding is the absence: with N = 0 entries over 42 live days, no claim about Reversal is
  supportable in either direction. `Reversal.Enabled` is `false` in
  `src/TradingBot.FuturesWorker/appsettings.json`.
- Evidence: SQL run 2026-09-06.

**F8 — Exit rules alone do not turn the negative entry edge positive; the best measured
policy is +0.042 %/instrument-day, and it is not the one running.**
- Numbers, recorded 2026-08-30 on the arm's own 45-day entries with price exits held constant
  and only two rules varied, clustered by coin-day: P1 max-hold without signal-exit
  −0.121 %/coin-day; P2 drop max-hold −0.047 %; P3 signal-exit without max-hold **+0.042 %**
  (the only positive, and positive in both halves); P4 both −0.023 %. Separately, exit regime D
  measured **+0.077 %/coin-day** over 45 days, positive in both halves.
- Method: same entries, exits simulated, clustered by coin-day, split-half check.
- Sample: the arm's 45-day entry set.
- What would have falsified it: any exit configuration lifting expectancy clearly above zero
  and holding in both halves at a magnitude comparable to the −0.44 to −0.54 %/instrument-day
  entry deficit of F2.
- The tension is the point: +0.042 to +0.077 %/coin-day from exits sits an order of magnitude
  below the measured entry deficit, and the two were computed under different cost
  assumptions, so they cannot simply be added.
- **Not reproduced in this audit** — recorded numbers, taken from notes, not re-run.
- Evidence: `.ai/worker-changelog.md:43` and `:67`.
- Live exit-rule mix for context (futures-live, 45 d, mean net %/trade): SELL_STOP_LOSS
  −1.3021 (n = 109), EXCHANGE_TRAILING_STOP +0.6749 (n = 75), EXCHANGE_CLOSE −0.0892 (n = 36),
  SELL_MAX_HOLD −1.3291 (n = 28), null −0.1387… +0.1387 (n = 27), EXCHANGE_MAX_HOLD_RELEASE
  +0.2288 (n = 6), EXCHANGE_STOP_LOSS −0.9232 (n = 3).

**F9 — The columns that would evidence a common clock and a request budget are never written.**
- Numbers: over the last 6 h, `collector_run` holds 6,873 rows with `clock_offset_ms`
  populated **0** times, `http_status` **0**, `request_weight` **0**.
- Method: `select count(*), count(clock_offset_ms), count(http_status), count(request_weight)
  from collector_run where started_at >= now() - interval '6 hours'`.
- Sample: 6,873 collector runs across four venues and five collectors.
- What would have falsified it: any non-null value in any of the three.
- Consequence: cross-venue clock skew is not measured anywhere, and the per-venue budgets in
  `exchange.request_budget_per_s` are not reconciled against observed cost.
- Evidence: SQL run 2026-09-06.

---

# Rejected

- **B2 — the "both halves" robustness test, claimed pass rate 4–8 %.** No notebook, script or
  recorded run exists in either repository (`grep -rilE "shuffl|permut|random parameter|null
  distribution|block.?screen"` over both trees returns only `.ai/strategy-challengers.md`,
  which uses "both halves" as a pass criterion and never states a null pass rate). The number
  is **not recoverable**; candidate dropped.
- **B5 — backtest vs live divergence.** No measurement is possible from stored data: for all
  381 live futures entries `filled_notional_eur / requested_notional_eur = 1.0000` exactly
  (no partial fills recorded, so fill rate has no variance), `modeled_fill_price` is null on
  every futures row (populated only for the retired `spot-live`, mean absolute deviation
  0.0411 %, n = 193), and `time_to_fill_ms` has a median of 0. **Not recoverable**;
  candidate dropped.
- **B8 — LLM watchlist fallback rate.** The watchlist and AI-provider code exist only under
  `trading-bot/src/archive/TradingBot.SpotWorker/`, whose instances stopped cycling
  2026-08-21 07:49Z. Nothing in the live FuturesWorker path calls a model, so there are no
  cycles over which to measure a fallback fraction, and no model-picked vs heuristic-picked
  comparison exists. **Not applicable to what runs**; candidate dropped.
- **"Depth cannot be backfilled"** — true by construction, not a measurement.
- **"Switched to ATR exits" / "mirror switched off" / "arm resized to three slots"** — changes
  made, not results.

---

# Claims audit

| # | Claim | Status | Evidence |
|---|---|---|---|
| C1 | "Kraken Futures, WEEX and Hyperliquid collecting; Binance USD-M next" | **FALSE (stale)** | Binance USD-M has been collecting since 2026-09-06 12:29:22Z — 208,888 snapshot rows, 404,487 candles, 21,894 funding rows; `collector_status` shows 0 consecutive failures on all five of its collectors |
| C2 | "Binance Perpetuals — LIVE" | **VERIFIED** | as C1; segment `binance-usdm` status `enabled`, latest write 2026-09-06 18:50:09Z, 566 trading instruments |
| C3 | "Four exchanges are collecting now. Twelve more are queued." | **PARTIAL** | four verified (A8). No list of twelve exists: `plans/exchange-roadmap.md` names six additional venues — OKX, Bybit, Coinbase, Deribit, MEXC, Bitget — and the `exchange` table holds five codes including `fake` |
| C4 | "OKX, Bybit, Coinbase — IN PROGRESS (adapter is being written)" | **FALSE** | `grep -ril` over `CryptoSmith-X/src` returns **0 files** for each of okx, bybit and coinbase. No stub, no branch: `git log --all` (165 commits) contains no adapter work for them |
| C5 | "We keep the order book in memory and write what we saw" | **PARTIAL** | the book is built in memory — `KrakenBookBuilder.cs`, `BinanceBookBuilder.cs`, `WeexBookBuilder.cs`, `HyperliquidBookMath.cs` — but what is written is six numbers per observation (bid/ask at 10, 25, 50 bps), not the book. Per the 2026-09-05 audit, Hyperliquid supplies per-level order counts and `HyperliquidDtos.cs:33-39` discards them (`docs/audit/coverage-2026-09-05.md:87`) |
| C6 | "A gap is never passed off as a zero" | **VERIFIED, with one uncovered case** | `SnapshotCollector.cs:11-12`: "A row goes in whole or not at all … an observation missing a field is skipped rather than completed with a zero." `collector_gap` records 297 real gaps with causes (rate_limited 296 — 287 hyperliquid/snapshot, 5 binance/snapshot, 4 binance/depth — plus 1 weex/depth error). Uncovered: the 72–79 % of observations dropped by the keep-bucket (F3) are neither stored nor recorded as gaps — they are absent, not zeroed, so the claim holds literally |
| C7 | "Cross-exchange spreads stay comparable on one clock" | **FALSE as stated** | the column that would evidence it is empty: `clock_offset_ms` is populated in **0 of 6,873** collector runs over 6 h (F9). Each row does carry its own `received_at`, and depth its own `depth_at`, so staleness is visible per row — but the depth legs are not on one cadence, let alone one clock: median depth interval 60.2 s (kraken) vs 353.7 s (binance) vs 82.7 s (weex) (A9) |
| C8 | "Discovers new instruments by itself and tracks their whole life cycle" | **PARTIAL** | discovery runs 26×/24 h per segment and finds instruments unattended; `status` carries trading/halted/delisted/post_only, and `first_seen_at`/`last_seen_at`/`status_changed_at` are populated for all 2,258. But only the **latest** change is kept — there is no `instrument_status_event` table (WP7 not started), and `listed_at` is populated for 997 of 2,258 |
| C9 | "More than 150 markets every 120 seconds" | **PARTIAL** | "every 120 seconds" verified — measured cycle interval median exactly 120.0 s, p95 120.1 s over 5,118 cycles. "More than 150 markets" is a sum over two instances (184 at the time of the call); the union of distinct pairs is **104** (F5) |
| C10 | "Every record can be reproduced" | **FALSE** | no raw response archive exists (WP2 not started, `raw_archive` absent), so no record can be rebuilt from source bytes; 72–79 % of ticker observations were never written (F3); depth is stored only as six aggregates, discarding the levels it was computed from (A10); and CryptoSmith-X runs floating `:latest` image tags, so the code that produced a given row is not identifiable from the deployment |
| C11 | "The testbed's code is public (MIT)" | **VERIFIED** | `gh repo view` — both `bykovas/trading-bot` and `blyn-ai/CryptoSmith-X` are PUBLIC with licence `mit`; `LICENSE` files present in both (MIT, © 2026 Denisas Bykovas / MB "BlynAI") |
| C12 | "Manual interventions and copied entries are marked in the journal" | **PARTIAL** | copied entries are marked — `entry_channel = 'Mirror'`, 36 live entries, plus a dedicated `futures_entry_mirror_commands` table (21 rows). Manual interventions are **not** marked anywhere: `portfolio_position_state.origin` only ever holds `BOT`, and no MANUAL value exists in any column of `dry_run_actions` |
| C13 | "4,273,377 decisions logged" — derived from the decision table, excluding dry-run? | **derived: YES. excludes dry-run: NO** | `ReadDecisionsTotal` = `select count(*) from dry_run_decision_facts`, no predicate (`Program.cs:576-580`); table held 4,279,041 rows at audit time, so the published figure is this counter read earlier. It includes 1,211,941 rows from `futures-virtual` and `spot-virtual` and 53,590 from the retired `spot-live`; the two live futures instances alone account for 3,012,614 (F6) |

---

# Unknowns

- **The published website itself was never located.** The claims in C1–C13 were audited as
  worded in the audit request. `grep` over both repositories finds no source containing
  "Twelve more", "150 markets", "IN PROGRESS" as venue status, or the venue block; the closest
  artefacts are the design-system page
  `src/CryptoSmithX.WebApp.Admin/wwwroot/ds/ui_kits/site/index.html` ("every decision logged",
  "decisions to date", "On four venues", "Spot and perps across Kraken, Binance, WEEX and
  Hyperliquid") and the mock `wwwroot/ui-mocks/pairs-monitor.html`. To close this: the
  deployed URL of the marketing page, or the repository and path that renders it. Note that
  the design-system page's "Spot and perps" is contradicted by the data — all five segments
  have `kind = perp` and no spot adapter exists for any venue (A14).
- **Funding cost per live trade.** `portfolio_position_state.funding_paid_eur` is null for
  both live instances, so F2's expectancy excludes funding and is an upper bound. To close it:
  persist the funding leg per position, or reconcile against Kraken's wallet history.
- **B2's 4–8 % figure.** Neither the script nor a recorded run exists. To close it: the
  notebook, or a re-run specification (which parameter space, which shuffle, which pass test).
- **Backtest-vs-live divergence.** Requires storing a modelled counterfactual fill alongside
  the real one for futures (`modeled_fill_price` is futures-null today) and a non-degenerate
  `time_to_fill_ms`; WP11's replay harness does not exist.
- **Whether the 2026-09-05 audit's "5 of 6" was ever true at the venue level.** `depth_at` and
  the collector_run item counters only reach back to 2026-09-05, so F3 could only be measured
  on a current window. To close it: a longer-retained counter, or the audit's own worksheet.
- **Which commit each CryptoSmith-X container is running.** Images are tagged `:latest`
  (created 2026-09-06T18:43:31–41Z). trading-bot does record it — `worker_commit
  d1fcfec05b2566aa9eb1f8835bdd3b62598b8e2b`, `worker_build_utc` 2026-09-01T12:08:12Z, in every
  `dry_run_cycles` row. To close it: tag CryptoSmith-X images by commit, as trading-bot does.
- **`instruments_expected` disagrees with the registry** on WEEX: `collector_status` expects
  867 snapshot instruments while `exchange_instrument` lists 1,005 trading. Not resolved here.
