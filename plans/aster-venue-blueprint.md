# Aster (asterdex) — venue blueprint

Status: **blueprint, no code changed.** Written 2026-09-16 from live probes of Aster's public API
and a read of this repository at `7527e5c`.

Where the probes ran from, because limits are per IP:
- **REST and WebSocket probes ran from the owner's workstation egress, not from TEST or PROD.**
  - PROD is out of bounds for this task.
  - The TEST host (`csx-test`, 38.242.248.83) refused this workstation's key (`Permission denied
    (publickey)`), so nothing ran there.
  - §11 gives the operator the commands to repeat the probe from both hosts before enabling.
- Binance USDⓈ-M was probed from the same egress in the same minutes, so every "same / different"
  statement below compares like with like.

Sources:
- live responses of `fapi.asterdex.com` and `fstream.asterdex.com`, with weights read from
  `x-mbx-used-weight-1m`;
- a 10-minute WebSocket capture (16:22–16:32 UTC);
- a seam test of the REST seed against the WS depth stream;
- `asterdex/api-docs` master, `V3(Recommended)/EN/aster-finance-futures-api-v3.md`;
- this repo's `Connectors/Binance/*`, `Hub/Ingestion/*` and migrations 0001–0064.

---

## 0. Verdict in one screen

**The wire protocol is a Binance USDⓈ-M clone.** Paths, payload shapes, WS stream names, the
combined-stream envelope, the `U/u/pu` book sequence, the seed rule, headers and error codes all
match. Every REST reader in `BinanceUsdmClient` parses Aster's responses unchanged.

**The semantics are not a clone. The adapter cannot run with just a different `base_url`.** Pointed
at Aster as it stands, `BinanceUsdmMarketData` plus its two feeds would do four wrong things:

1. **Admit 124 equity, ETF, commodity and FX perpetuals into the crypto asset table.**
   - Aster publishes them as `contractType = PERPETUAL`, `underlyingType = COIN`.
   - The only marker is `symbolType = 1` (plus `channel` / `underlyingSubType`).
   - Binance marks the same instruments `TRADIFI_PERPETUAL`. The allowlist in `BinanceMarkets`
     exists to keep exactly those out, and on Aster it lets them through.
2. **Exceed the documented 200-streams-per-connection cap by 2–4×.**
   - Both feeds subscribe the venue's whole in-scope set, not the collected set: 441 depth streams
     and 3 + 2 × 441 = 885 market streams.
   - Aster disconnects a connection over the cap, and its docs warn that repeatedly disconnected IPs
     may be banned.
3. **Drop quiet instruments from the snapshot while the socket is healthy.**
   - `!ticker@arr` on Aster pushes only symbols whose statistics changed: an average of 7 symbols a
     frame, 149 distinct in 10 minutes.
   - The WS branch of `GetTickersAsync` keeps only symbols with a ticker younger than 30 s and
     returns that partial batch instead of falling back.
   - Result, read from the code rather than observed: TRX, DOT and BCH would get no rows for long
     stretches, and no gap would be recorded.
4. **Fail the `open_interest` loop every pass.**
   - `GetOpenInterestHistoryAsync` calls `/futures/data/openInterestHist`, which Aster does not
     serve (HTTP 404, HTML body).

**So this is a parametrised reuse, not a config-only addition and not a new adapter.** A small
`BinanceUsdmProfile` record is passed into the existing classes (§4). Binance keeps its current
values byte for byte. Estimated effort: **2½–3 developer days**, then **one day on TEST** before
PROD (§12).

---

## 1. The Binance-clone hypothesis, field by field

### 1.1 REST — paths, shapes, weights

Weights are measured deltas of `x-mbx-used-weight-1m` between two identical calls, not copied from
docs. Binance values are from this repo's `BinanceUsdmClient` notes and today's probe.

| Endpoint | Aster | Binance | Shape |
|---|---|---|---|
| `GET /fapi/v1/exchangeInfo` | 200, weight 1, 813 KB, 595 symbols | 1, 1 114 KB, 897 | same top-level keys; symbol diff in §1.2 |
| `GET /fapi/v1/fundingInfo` | 200, **weight 10** | **0** (no header) | Aster lists **746** rows for all symbols, incl. 151 absent from exchangeInfo; carries `fundingFeeCap/Floor`, `interestRate`, `time`. Binance lists only deviating symbols and carries `adjustedFundingRateCap/Floor`, `disclaimer`, `updateTime`. `symbol` and `fundingIntervalHours` (the two fields we read) are identical. |
| `GET /fapi/v1/ticker/bookTicker` (all) | 200, **weight 2**, 573 rows | 5, 766 | identical keys |
| `GET /fapi/v1/premiumIndex` (all) | 200, weight 10, 746 rows | 6 measured / 10 documented, 900 | identical keys, incl. `interestRate`, `estimatedSettlePrice`, `time` |
| `GET /fapi/v1/premiumIndex?symbol=` | weight 1 | — | identical |
| `GET /fapi/v1/ticker/24hr` (all) | 200, weight 40, 590 rows | 40, 766 | identical keys |
| `GET /fapi/v1/ticker/price` | 200 | 200 | identical |
| `GET /fapi/v1/openInterest?symbol=` | **200, weight 0–1** (two measurements, 0 and ≈1), **undocumented** | 200, weight 1 | identical `{symbol, openInterest, time}`; without `symbol` → HTTP 400 `-1102` on both |
| `GET /futures/data/openInterestHist`, `globalLongShortAccountRatio`, `takerlongshortRatio` | **404 (HTML page)** | 200 | — |
| `GET /fapi/v1/depth?limit=100/500/1000` | weight 5 / 10 / 20 | 5 / 10 / 20 | identical: `lastUpdateId, E, T, bids, asks` |
| `GET /fapi/v1/klines` | limit 100 → **2**, 1500 → 10 | 1 / 10 | **12-element arrays, same order**; element 11 is `"0"` on both |
| `GET /fapi/v1/markPriceKlines`, `/indexPriceKlines?pair=` | 200, weight 2 each | 200 | identical 12-element arrays, volume columns `"0"`; element 8 is 60 on full bars |
| `GET /fapi/v1/fundingRate` | 200, no weight header | same | Aster rows lack `markPrice` and `rateType`; we read neither |
| `GET /fapi/v1/trades?limit=1000` | 200, weight ≈1 | 200, 5 | Aster lacks `isRPITrade` (unused) |
| `GET /fapi/v1/aggTrades?limit=1000` | 200, weight 20 | 200 | Aster lacks `nq` (unused) |
| `GET /fapi/v1/historicalTrades` | **401 `-2014` "API-key format invalid."** | same | needs a key on both (security type MARKET_DATA) |
| `GET /fapi/v1/allForceOrders` | 400 `{"code":400,"msg":"The endpoint has been out of maintenance"}` | 404 | not available |
| `GET /fapi/v1/constituents` | 404 | 200 | Aster's equivalent is `/fapi/v3/indexreferences` (index composition, per-exchange weights) |
| `GET /fapi/v3/*` | every market-data path above answers identically under `/fapi/v3` | — | see §1.5 |

### 1.2 `exchangeInfo.symbols` — field diff

- **Top level.** `rateLimits` is identical to Binance's:

  ```
  REQUEST_WEIGHT 2400 / 1 min
  ORDERS 1200 / 1 min
  ORDERS 300 / 10 s
  ```

- **Fields Binance has and Aster lacks.** `maxMoveOrderLimit` and `permissionSets` are both unused
  by us. So is the filter `POSITION_RISK_CONTROL`.
- **Fields Aster has and Binance lacks.**
  - Symbol fields: `symbolType`, `tradingMode`, `name`, `channel`, `sequenceNo`, `twapMinNotional`,
    `imn`, `tags`, `settlePlan`, `createTime`.
  - Filter: `MAX_NUM_ALGO_ORDERS`.
  - `PERCENT_PRICE` gains `ltMultiplierUp/Down`.
- **Fields we read** (`symbol`, `contractType`, `status`, `baseAsset`, `quoteAsset`, `onboardDate`,
  and `PRICE_FILTER.tickSize`, `LOT_SIZE.stepSize/minQty`, `MIN_NOTIONAL.notional`): present on all
  595 symbols. `onboardDate` is non-zero on all 595.
- **`liquidationFee` and `marketTakeBound`:** present on both venues, unused by us.

Vocabularies observed on Aster:

| Field | Values (count) | Binance for comparison |
|---|---|---|
| `contractType` | `PERPETUAL` 590, **`""` 5** (all `PENDING_TRADING`: MBL, AFEE, PHAROS, SKHX, SMSN) | `PERPETUAL` 702, `TRADIFI_PERPETUAL` 191, quarterlies 4 |
| `status` | `TRADING` 574, `SETTLING` 16, `PENDING_TRADING` 5 — all three already mapped in `BinanceMarkets.Status` | adds nothing new |
| `underlyingType` | `COIN` 595 — **including the stock perps** | `COIN`, `EQUITY`, `HK_EQUITY`, `COMMODITY` … |
| `symbolType` | `0` 462, **`1` 133** | absent |
| `channel` (symbolType 1) | `nasdaq`, `forex`, `hkstock`, `krstock`, `astock`, `{}` | absent |
| `underlyingSubType` | `STOCK` 80, `STOCK+Semiconductor` 21, `Commodities` 9, `STOCK+ETF` 6, plus crypto tags | similar |
| `tradingMode` | `0` 575, `1` 20 (mixed: OPENAI, SAMSUNG, but also RTX, CASHCAT) | absent; **meaning undocumented**, not used for scope |
| `quoteAsset` | `USDT` 583, `USD1` 10, `U` 2 | adds `USDC` 39, `BTC` 1 |
| `deliveryDate` | 4133404800000 on 579 rows; near dates on 16 (the SETTLING ones) | same convention |

### 1.3 Headers, limits, errors

- **Weight header.** `x-mbx-used-weight-1m` is present, but **it is not one counter**. Consecutive
  calls read 121 → 1 → 2 and 228 → 39 inside the same minute, so the header reflects whichever
  CloudFront/backend node answered. Treat it as a per-node sample, not as the IP's budget.
- **CloudFront sits in front of the API** (`server: CloudFront`, `x-amz-cf-pop: FRA56`).
  - After ~40 calls to `/fapi/v1/time` inside one minute, CloudFront answered **HTTP 429 with an
    empty body, no `Retry-After`, no `x-mbx-*` header**, while the recorded weight was ~107 of 2400.
  - That is a second limiter we cannot see in `rateLimits`.
  - `EnsureVenueSuccess` → `VenuePenalty.Apply` must treat a bodiless 429 as a penalty; it must not
    fail while parsing a `-1003` body. Pin this with a test.
- **429 → 418 escalation and bans (2 min to 3 days):** documented identically to Binance (docs
  lines 167, 227). Not triggered here.
- **Error envelope:** `{"code":-1102,"msg":…}`, the same codes. The exception is `allForceOrders`,
  which uses `code: 400`.
- **Unknown path:** a 404 **HTML** page (6.9 KB), not JSON. The same class of surprise the Binance
  client already survives with `EnsureVenueSuccess`.

### 1.4 WebSocket

| Fact | Aster | Binance (repo notes) |
|---|---|---|
| Hosts and paths | `wss://fstream.asterdex.com` with `/stream`, `/ws/<name>`, and also `/public/stream` and `/market/stream` — **all four deliver every stream; there is no routing.** Verified: `solusdt@depth@100ms` delivered 5 252 frames on `/market/stream`. | routed: `/public` vs `/market`, a stream on the wrong path is silent |
| Envelope | `{"stream":…, "data":…}` on combined paths; SUBSCRIBE ack `{"result":null,"id":n}` | same |
| `<symbol>@depth@100ms` payload | `e, E, T, s, U, u, pu, b, a` | same |
| Book sequence | `pu == previous u` held on 17 263 frames across 4 books, **0 breaks** in 10 min | same |
| Seed rule | REST `lastUpdateId = L`; drop `u < L`; the first kept frame satisfies `U ≤ L ≤ u`; the next chains by `pu`. Verified on BTCUSDT (22 dropped, bridge at #22, chain OK) and TRXUSDT (17 dropped, bridge at #17, chain OK) | identical — `BinanceBookBuilder` applies unchanged |
| Update ids | venue-global (`U` jumps ~500–1 000 between consecutive frames of one symbol) | same |
| `!markPrice@arr@1s` | **one frame per second with all 746 symbols** (108 KB/frame) | sharded (repo note) |
| `!markPrice@arr` | every 3 s, 746 symbols | — |
| `!ticker@arr`, `!miniTicker@arr` | 1 frame/s, **changed symbols only**: avg 7, max 23 per frame, 149 distinct in 10 min | — |
| `!bookTicker` | 6 166 msgs/s, **1.07 MB/s** | on REST in our adapter |
| `!forceOrder@arr` | 2 events in 10 min (AKEUSDT); payload `{e,E,o:{s,S,o,f,q,p,ap,X,l,z,T}}` | same |
| Stream cap | **200 per connection** (docs line 1665) | 1 024 |
| Incoming messages | ≤ 10/s per connection | same |
| Connection life | 24 h, server ping every 5 min, closed without pong within 15 min | same |
| Streams not offered | `!openInterest@arr`, `<symbol>@openInterest`: SUBSCRIBE acked, **no frames** | same — no OI socket |

### 1.5 Two path generations

The docs name `/fapi/v3` "Recommended" and `/fapi/v1` "Legacy". Every public market-data endpoint
answers identically under both (checked: ping, exchangeInfo, depth, openInterest, premiumIndex,
klines, fundingInfo, aggTrades). Keep `/fapi/v1`, because it is what `BinanceUsdmClient` builds. If
Aster retires v1, only the client's path prefix changes. Worth a profile field so the change is
config rather than a fork (§4).

### 1.6 Verdict

**Protocol: confirmed clone. Semantics: four differences that change what we store** — scope marker,
stream cap, change-only ticker array, no analytics namespace. The last three also carry cost and
ban-risk consequences. The blueprint is therefore **"reuse the Binance USDⓈ-M classes through a
profile"**, not "same class, different URL", and not "new adapter".

---

## 2. Datasets — possible or not

"Venue ts" means the venue's own clock is available for the column that asks for it; `received_at`
is always ours (see the note on `received_at` in `BinanceUsdmMarketData.GetTickersAsync`).

| Dataset | Possible | Source and transport | Cadence | Provenance |
|---|---|---|---|---|
| `discovery` | **yes** | `exchangeInfo` (1) + `fundingInfo` (10) | 900 s | `listed_at` ← `onboardDate` (all rows). Funding interval ← `fundingInfo` (lists every symbol: 4 h 350, 8 h 303, 1 h 90, 2 h 3). `raw_json` verbatim. |
| `spec_versions` | **yes** | written by discovery | with discovery | as Binance |
| `snapshot` | **yes** | bid/ask/sizes ← `bookTicker` (2); mark, index, funding, next funding ← `premiumIndex` (10) or `!markPrice@arr@1s`; last price, turnover, base volume ← `ticker/24hr` (40); OI ← per-symbol `openInterest` cycle | 15 s | `venue_ts` ← `premiumIndex.time` or markPrice `E`. `open_interest_at` ← `openInterest.time`, the venue's sampling instant, which can be minutes old on quiet symbols (TSLAUSDT read 13 min old). `next_funding_at` ← `nextFundingTime`. **See §4.3: the WS ticker path must not be used as is.** |
| `depth` | **yes** | WS book (`@depth@100ms`, seeded from REST `limit=1000`), REST fallback | 300 s | `depth_at` ← frame `E` / REST `E`. REST reach is thinner than Binance: BTC limit 100 reaches **13.6 bps**, 500 → 62.9 bps, 1000 → 308 bps. **REST fallback should use limit 500** (weight 10), which bounds all three bands on BTC; Binance's constant 100 would null BTC's 25 and 50 bps bands. |
| `book` | **yes** | the same maintained WS book, sampled | 15 s | `observed_at` ← frame `E`; `seq` ← `u` |
| `trades` | **yes** | `<symbol>@aggTrade` over WS | drain every 5 s | `event_time` ← `T`; `venue_uid` ← aggregate id `a`. **Backfill is also possible without a key** — see note 1. |
| `candles` | **yes** | `@kline_1m` WS, REST `klines` fallback (weight 2 at limit 100) | 60 s | `open_time` ← venue; closed bars only; `trade_count` ← element 8; history from 2021-09-01 (BTCUSDT) |
| `candles_mark` | **yes** | REST `markPriceKlines` (2) | 60 s | history from 2021-08-27 |
| `candles_index` | **yes** | REST `indexPriceKlines?pair=` (2) | 60 s | history from 2021-08-27 |
| `funding` | **yes** | REST `fundingRate` (no weight header) | 3 600 s | `funding_time` ← venue. Full history without a key: BTCUSDT back to 2021-08-27, ASTERUSDT to 2025-09-16, TSLAUSDT to 2025-07-10. **Quirk:** `startTime=0` is treated as absent and returns the latest rows; the collector always sends a real window, but a test must pin it. |
| `open_interest` | **only as our own sampled series, never as venue history** | per-symbol `openInterest` (0–1), bucketed onto the 300 s grid by `OpenInterestHistoryCollector`'s sampled branch, `source='rest'` | 300 s | `/futures/data/*` is 404 and no OI socket exists, so venue-published OI **history is impossible**. The sampled series starts when we start, and a bucket we were down for is absent, as on WEEX and Hyperliquid. |
| `liquidations` | **yes, as a sample, not a tape** — see §3 | `!forceOrder@arr` over WS | bucket drain 900 s | venue `T`; hourly buckets, `source='ws'` |
| `candles` for stock perps | out of scope for now (§6) | — | — | — |
| `vault_pair_state`, `vault_state`, `reference_depth` | **no** — order-book venue | — | — | stays `disabled` |

Note 1 — trade backfill without a key:
- `historicalTrades` needs a key, so raw-trade backfill is impossible, and `/trades` returns only the
  latest 1 000 (16 min of BTCUSDT today).
- But `aggTrades` answers `startTime/endTime` (≤ 1 h window) and `fromId` **without a key**, back to
  aggregate id 1 (2021-08-29), at weight 20 per 1 000 rows.
- The aggregate tape is exactly what the WS path stores (`@aggTrade`), so a backfilled row and a live
  row are the same kind of row (`source='backfill'` vs `'ws'`).
- The Binance adapter does not backfill trades today. This is a **separate, later** piece of work; it
  matters for the research question (§10).

---

## 3. Liquidations — a sample, recorded as one

Aster's docs, verbatim in meaning for both `<symbol>@forceOrder` and `!forceOrder@arr`: for each
symbol, only the latest liquidation order within 1 000 ms is pushed, and nothing is pushed if none
happened. Two liquidations of one symbol inside the same second arrive as one event. A bucket summed
from these events is therefore **a lower bound built from at most one event per symbol per second**,
not the venue's liquidated volume.

What the blueprint requires before `liquidations` is set to `collect` on `aster-perp`:

1. **Record it in the data.**
   - Add `note` on `segment_dataset (aster-perp, liquidations)`: "Sample: at most one liquidation
     per symbol per second is published (venue docs); buckets are lower bounds."
   - Add a capability row `history_depth` / `venue_supports` value `sampled_1s_per_symbol`
     (`source='documented'`, with the doc URL).
   - Both are readable by Studio without a schema change.
2. **Surface it in Studio.** Wherever `liquidation_volume_history` is drawn for this segment
   (`WebApp.Studio` — `Models/Names.cs` and `GapVoice.cs` already carry per-segment wording), the
   series carries the label "sampled (≤ 1 event/symbol/s) — lower bound", never a bare volume. The
   label is read from the capability row, not hardcoded to the segment code.
3. **Do not merge liquidations into `trade`.** Keep them apart for the reason the Binance feed gives:
   the same fills also arrive on `@aggTrade`, and the tape has no liquidation flag.

**Cross-venue finding — this applies to `binance-usdm` today.**
- Binance documents the same 1 000 ms snapshot rule for `!forceOrder@arr`.
- `LiquidationCollector`'s header describes those buckets as "our arithmetic over the venue's
  events", but nothing in the schema, the capability rows or Studio says the events are themselves a
  sample.
- Binance liquidation buckets already stored on TEST and PROD are lower bounds labelled as volumes.
- This is not fixed here, because it is out of scope. It should get the same note, capability row
  and Studio label in its own change.

---

## 4. Reuse plan

### 4.1 What in the Binance classes is Binance-specific today

| Where | What | Why it blocks Aster |
|---|---|---|
| `BinanceUsdmMarketData` | `sealed`; `SegmentCode => "binance-usdm"` literal; logger category `"Binance.Usdm"` | the Hub keys every collector query on `SegmentCode` |
| `BinanceMarkets` (static) | scope = `contractType ∈ {PERPETUAL}` ∧ quote ∈ {USD, USDT, USDC} | admits 124 Aster RWA perps |
| `BinanceUsdmClient.DepthLimit` (const 100) | REST book window | nulls BTC's 25 and 50 bps on Aster; 500 is right there |
| `BinanceUsdmMarketData.GetOpenInterestHistoryAsync` | calls `/futures/data/openInterestHist` | 404 on Aster |
| `BinanceWsFeed.RefreshSymbolsAsync`, `BinanceMarketWsFeed.RefreshSymbolsAsync` | subscribe **all in-scope TRADING symbols** of the venue | 441 depth and 885 market streams vs a cap of 200 |
| `BinanceUsdmMarketData.GetTickersAsync` WS branch | whole batch from symbols with a fresh `!ticker@arr` entry (30 s) | Aster pushes changed symbols only, so quiet symbols vanish silently |
| `BinanceSymbol` DTO | no `symbolType` | the scope marker is not read |

### 4.2 The change: a profile record, not a base class

```text
BinanceUsdmProfile (record, Connectors/Binance)
  SegmentCode              "binance-usdm"      | "aster-perp"
  LogName                  "Binance.Usdm"      | "Aster.Perp"
  ApiPrefix                "/fapi/v1"          | "/fapi/v1"   (v3 possible later, §1.5)
  IsInScope(BinanceSymbol) Binance rule        | Binance rule ∧ SymbolType == 0
  RestDepthLimit           100                 | 500
  SeedDepthLimit           1000                | 1000
  OpenInterestHistory      Analytics           | Sampled  (return [] → Hub samples)
  FeedSymbols              WholeVenue          | Collected  (CollectedSymbols(segment))
  MaxStreamsPerConnection  1024                | 200
  TickersFromMarketFeed    true                | false
```

- **`BinanceUsdmProfile.Binance` reproduces today's constants exactly.** Binance's code path is the
  same code reading the same values. A unit test asserts every Binance field against the constant it
  replaced, so a later edit to the Aster profile cannot move Binance.
- **`BinanceSymbol` gains `int? SymbolType`**, which is null on Binance. Aster's rule is an
  **allowlist `{0}`**, so an unknown value or a missing field is out and gets logged once. This
  follows the `ReportUnknownContractType` pattern.
- **`contractType ""`** (five PENDING_TRADING rows) is already skipped and logged once by the
  existing allowlist. No change is needed.
- **Status mapping** is reused as is; all three observed Aster values are mapped. An unknown status
  still throws, which is the right answer here too.
- **Scope is applied in one place.** `BinanceMarkets.IsInScope` becomes `profile.IsInScope`. The
  adapter and both feeds read it from the same profile, so they cannot drift (the reason
  `BinanceMarkets` exists).

What changes in each class:

- **`BinanceUsdmClient`** takes the prefix and the REST depth limit. The seed limit stays at the
  call site that seeds.
- **`BinanceUsdmMarketData`:**
  - unsealing is not needed — the class stays `sealed` and takes the profile;
  - `SegmentCode` and the logger name come from the profile;
  - `GetOpenInterestHistoryAsync` returns `[]` when the profile says `Sampled`. That is the
    interface's documented "venue has no history" answer, and `OpenInterestHistoryCollector` then
    buckets the snapshot OI itself, exactly as for WEEX.
- **`BinanceUsdmMarketData.GetTickersAsync`:** when `TickersFromMarketFeed` is false, skip the WS
  branch and take the REST branch.
  - The REST snapshot is cheap on Aster: 52 weight per pass, 208 weight/min at 15 s, 9 % of the
    budget.
  - The market feed still runs for trades, liquidations and WS klines.
  - A future improvement (mark/index/funding from `!markPrice@arr@1s`, which does carry all 746
    symbols every second, with last/turnover from REST) is noted, not designed here.
- **`BinanceWsFeed` and `BinanceMarketWsFeed`:**
  - take a `Func<CancellationToken, Task<string[]>>` symbol source; `BuildBinance` already has
    `CollectedSymbols(code)` for the OI feed;
  - `WholeVenue` keeps today's `exchangeInfo` refresh;
  - before subscribing, count the streams (depth feed: N; market feed: 3 + 2N). Above
    `MaxStreamsPerConnection`, subscribe the first ones in symbol order up to the cap. Log once, at
    warning level, which symbols were left out. Their depth then comes from REST through the
    existing fallback, and their trades are not collected, which the warning must say.
  - **No sharding in this change.** With the proposed universe (§6) the counts are 26 and 55.
    Sharding across connections is the named next step if the collected set passes 98 symbols.
- **`BinanceUsdmMarketData.Capabilities`:** Aster declares `open_interest` as `rest` (sampled) and
  `snapshot` as `rest`. Everything else is the same.
- **`ExchangeWorker.Build`:** `"aster-perp" => BuildBinance(config, gate, ct, BinanceUsdmProfile.Aster)`.
  `BuildBinance` takes the profile; the `"binance-usdm"` arm passes `BinanceUsdmProfile.Binance`.
- **`WebApp.Studio/Models/Names.cs`:** add `"aster-perp" => "Aster"`. Without it the fallback
  `Tidy(code)` prints the segment code.
- **debyko.com needs no change.** It picks the venue up from `/v1/exchanges`
  (`kind = perp`, `status = enabled`) and `/v1/coverage` once the segment is enabled on PROD. The
  venue is ranked there as an order book with a USDT quote.
- **Startup-liveness detector:** harmless on Aster. Paths are not routed, so a subscribe is never
  silently misrouted; the detector just never fires.

Alternatives considered and rejected:

- **Copy the adapter into `Connectors/Aster/`.** Rejected: 1 900 lines of feed code with a subtle
  seed/seam rule would drift from the Binance copy the first time either is fixed.
- **Extract a `BinanceCompatibleMarketData` base class.**
  - Rejected for now: it moves Binance's working adapter into a new hierarchy and exposes its
    private helpers as protected surface, for one second consumer.
  - The profile covers every difference found. If a third clone appears with a difference a profile
    field cannot express, that is the moment to extract a base.
- **Subclassing:** the classes are sealed with private state, and unsealing them is the riskier
  change.

### 4.3 Fixtures and tests (they belong to the change)

- **Fixtures:** capture into `tests/.../Fixtures/aster/` exchangeInfo (trimmed: BTCUSDT, TSLAUSDT,
  XAUUSD1, BTCUSD1, one `contractType ""`, one `SETTLING`, one `1000`-prefixed), fundingInfo,
  bookTicker, premiumIndex, 24hr, openInterest, depth, klines, mark/index klines, fundingRate, a
  404-HTML body and a bodiless 429.
- **Tests:**
  - scope: TSLAUSDT and XAUUSD1 out, USD1/U quotes out, `symbolType` missing → out and logged;
  - status mapping;
  - REST depth at limit 500;
  - OI history returns `[]`;
  - the ticker path ignores the market feed under the Aster profile;
  - the stream-cap guard at 200;
  - the Binance profile equals the old constants;
  - a bodiless 429 penalises the gate.
- **WS fixtures** under `Fixtures/aster-ws/`: a depth run with the seam (the captures in §1.4 are
  enough to write it), a markPrice array frame, a change-only ticker array, a forceOrder event.
- **`TreatWarningsAsErrors`** stays on; the new DTO field is nullable, so there are no new warnings.

---

## 5. Segment definition

Migration **`0065_aster_perp.sql`** (the head is 0064; check for a newer head at PR time).
Conventions follow 0057 and 0063: Russian header, the measurements in the header, and a
`raise exception` guard on each address.

```sql
insert into exchange (code, name, description,
                      request_budget_per_s, max_concurrent_requests,
                      request_budget_source, request_budget_url, request_budget_note)
values ('aster', 'Aster', 'Perp DEX (asterdex); REST/WS протокол — клон Binance USDⓈ-M.',
        5, 4, 'documented',
        'https://github.com/asterdex/api-docs/blob/master/V3(Recommended)/EN/aster-finance-futures-api-v3.md',
        '2400 весов/мин на IP из exchangeInfo.rateLimits; веса сняты заголовком 2026-09-16: bookTicker 2, '
        || 'premiumIndex 10, ticker/24hr 40, fundingInfo 10, openInterest 0–1, depth 5/10/20, klines(100) 2, '
        || 'aggTrades(1000) 20. Перед API стоит CloudFront со своим лимитом: 429 без тела и без Retry-After '
        || 'после ~40 запросов/мин к /fapi/v1/time при весе ~107. 5 req/s — с запасом под него; перемерить '
        || 'через сутки работы и сменить источник на measured.')
on conflict (code) do nothing;

insert into segment (code, name, description, status, adapter, exchange_code, kind, market_model,
                     base_url, ws_url, market_ws_url)
values ('aster-perp', 'Aster perpetuals', '…замер из этого плана…', 'planned', 'aster-perp', 'aster',
        'perp', 'orderbook',
        'https://fapi.asterdex.com',
        'wss://fstream.asterdex.com/stream',
        'wss://fstream.asterdex.com/stream')
on conflict (code) do nothing;
```

- **Two connections to the same path.** This is on purpose: it keeps the depth feed and the market
  feed as independent failure domains, as on Binance, and each stays far below 200 streams.
- **`status = 'planned'`, following 0057/0060/0063.** The ops script flips it to `enabled`
  (§9). The adapter exists after this PR, but whether it runs is the operator's decision, as argued
  in 0023 §5.
- **`segment_dataset`:** the cross join of `dataset` in **`disabled`**, with the note "Выключено до
  ops/enable-aster-perp.sql". Then set `interval_s` explicitly for the rows §9 enables:
  - discovery 900, spec_versions 3600, snapshot 15, depth 300, book 15, trades 5;
  - candles, candles_mark and candles_index 60;
  - funding 3600, open_interest 300, liquidations 900.
- **`segment_dataset_capability`:**
  - `transports_venue` with `source='probed'`: discovery rest, snapshot `rest,ws`, depth `rest,ws`,
    book `rest,ws`, candles `rest,ws`, candles_mark rest, candles_index rest, funding rest,
    trades `rest,ws` (aggTrades REST exists), open_interest rest, liquidations ws.
  - `transports_us` is left to Reconcile from `Capabilities`, as 0023 does.
  - Plus the liquidations sample row from §3.
- **`asset_alias`:** two global aliases Aster needs and the repo lacks:
  `(null, '1000NEX', 'NEX', 1000)` and `(null, '1000WOJAK', 'WOJAK', 1000)`, with the asset rows
  inserted first. The others already exist globally (1000BONK, 1000CAT, 1000CHEEMS, 1000FLOKI,
  1000LUNC, 1000PEPE, 1000RATS, 1000SATS, 1000SHIB, 1000XEC). `0G`, `2Z` and `4` are names, not
  prefixes, per 0023. `4STOCK` is a name.
- **No `request_budget_per_s` on the segment.** The host `fapi.asterdex.com` is its own gate under
  0054 and is not shared with any Binance host.

---

## 6. Universe

**Start with the 25 `asset.auto_collect` bases, USDT-quoted and `symbolType = 0`. All 25 are listed
and TRADING on Aster.** Discovery's 0029 policy then sets `collect = true` on exactly those. Add
**ASTERUSDT** by an operator decision: its 24 h turnover is 90 % of Binance's ASTERUSDT, the one
market where Aster is not a small satellite, which makes it the sharpest subject for §10.

24 h turnover on Aster, read about 16:15 UTC, with its ratio to Binance:
- BTC 1 108 M (0.086), ETH 717 M (0.064), SOL 95 M, XRP 65 M, ZEC 36 M, HYPE 31 M, DOGE 16 M,
  BNB 16 M;
- the tail is thin: TRX 0.10 M, DOT 0.09 M, AVAX 0.29 M, ONDO 0.29 M;
- venue total 2 579 M, of which RWA 328 M.

Why this set:
- It is the set every other venue collects, so Studio compares like with like and the §10
  comparison has Binance on the same bases.
- It keeps both feeds at 26 and 3 + 52 = 55 streams, far under the cap of 200.
- It includes thin markets on purpose. That is where the change-only ticker and the thin REST book
  would have bitten, and they are the honest test of §4.

**Stock, ETF, commodity and FX perps (`symbolType = 1`): out, deliberately**, for four reasons:
- Binance's equivalents (`TRADIFI_PERPETUAL`) are already excluded, so admitting Aster's would make
  the two venues' scopes disagree about the same instruments.
- They would auto-register `TSLA`, `NVDA`, `XAU`, `CL` and `SPCX` as crypto assets in a table built
  for crypto.
- **Session calendars break a continuous-market assumption.** TSLAUSDT did trade during this probe
  (US session open: 3 aggTrades and 3 814 depth frames in 10 min), but outside sessions these books
  go quiet by design. Our freshness tiers, `collector_gap` and the coverage-hour strip would read
  every overnight and weekend as "not observed" or "stale", which is a statement about us, not about
  a closed market.
- Their OI `time` lagged by 13 min while BTCUSDT's was current.

Admitting RWA needs a separate decision and a session-calendar concept (a "market closed" state
distinct from a gap). It is not a profile flag.

`USD1`- and `U`-quoted listings (BTCUSD1, ETHUSD1, XAUUSD1, BTCU …) stay out for the reason
`BinanceMarkets.UsdFamily` gives: third-party stablecoins, not the USD family.

---

## 7. Symbol normalisation and the registry

- **Instruments do not collide.** `exchange_instrument` is unique on `(segment_code,
  exchange_symbol)` (0001, renamed in 0019). `aster-perp/BTCUSDT` and `binance-usdm/BTCUSDT` are
  two rows with two ids, and every event table keys on the id.
- **Assets are shared on purpose.** Both resolve `BTC` through `asset_alias` (segment-specific
  first, then global, then identity), so both land on canonical `BTC` and pair with each other in
  `asset_family` `USD`, which is the point.
- **`1000`-prefixed contracts** resolve through the same global aliases as on Binance. The two
  missing ones are in §5.
- **Non-ASCII bases** (`龙虾`, `币安人生`, `哈基米`, `我踏马来了`, `牛来`) exist on Aster. **Binance
  lists the same five today**, so auto-registration and stream-name lower-casing already handle
  them on `binance-usdm`. No new risk; they are out of the collected set anyway.
- **Stream names** are `symbol.ToLowerInvariant()`, the same as `BinanceMarkets.ToStream`. Verified
  on `btcusdt`, `tslausdt`, `solusdt`, `trxusdt`.
- **No lookup confuses the two.**
  - Collectors filter `where segment_code = @code`.
  - Every `exchange_symbol = any(@symbols)` in `Api/Endpoints.cs` and `Api/HistoryEndpoints.cs`
    sits next to a `segment_code` predicate.
  - The one optional case, `/v1/instruments?symbols=BTCUSDT` without `exchange`, returns both rows
    labelled by segment, which is the intended answer.
  - The Studio stores have no `exchange_symbol` equality filter at all.

---

## 8. Disk estimate — 26 instruments

The method follows `plans/capacity-sizing.md`:
- rows/day × bytes/row all-in (heap + primary key);
- snapshot **233 B**, `market_candle` **139 B** (both measured there);
- the others computed from the 0032 schema and **not yet measured**, marked ≈;
- trade counts come from today's `ticker/24hr.count`, scaled by aggTrade/raw = 0.84 (the ratio of
  time spans covered by 1 000 raw trades vs 1 000 aggTrades on BTCUSDT).

| Table | Cadence | Rows/day | B/row | MB/day |
|---|---|---|---|---|
| `market_snapshot` | 15 s × 26 | 149 760 | 233 | **34.9** |
| `market_candle` | 1 m × 26 | 37 440 | 139 | 5.2 |
| `market_price_candle` (mark + index) | 1 m × 2 × 26 | 74 880 | ≈130 | ≈9.7 |
| `book_topn` (25 levels) | 15 s × 26, upper bound | ≤ 149 760 | ≈1 200 | **≤ 180** |
| `trade` (aggTrades) | 0.84 × 588 692 raw | ≈ 494 500 | ≈150 | **≈ 74** |
| `funding_rate_history` | 1–6 per symbol | ≈ 100 | ≈100 | ~0 |
| `open_interest_history` (sampled) | 300 s × 26 | 7 488 | ≈110 | 0.8 |
| `liquidation_volume_history` | 1 h × 26 | ≤ 624 | ≈110 | 0.07 |
| `instrument_spec` | on change | a few | ~2 000 | ~0 |
| **Total** | | **≈ 914 000** | | **≈ 305 MB/day ≈ 9.2 GB/month** |

- Without ASTERUSDT: trade ≈ 421 000 rows / 63 MB, total ≈ 285 MB/day.
- For scale: the same 25 bases on Binance printed **47.8 M** raw trades in the same 24 h, against
  **0.50 M** on Aster (1 %).
- **Aster's cost is dominated by `book_topn`, not by the tape.** `book_topn` is an upper bound:
  rows are written only when `seq` moves, and quiet books move less.
- WAL is not in this table. `capacity-sizing.md` §0.3 applies unchanged: WAL is several times the
  heap growth, driven by `market_snapshot_latest` upserts.
- **Measure on TEST after 24 h** with `pg_total_relation_size()` per partition, and replace every ≈
  above.

Request budget at this universe, worst case: every WS path down, all REST.

| Source | Weight/min |
|---|---|
| snapshot | 208 |
| REST depth at limit 500, 300 s | 52 |
| klines | 52 |
| mark + index | 104 |
| OI cycle, 1 min | ≤ 26 |
| discovery | ~1 |
| **Total** | **≈ 443 weight/min (18 % of 2 400)** |

- That is ≈ 121 requests/min, ≈ 2 req/s, under the 5 req/s gate.
- A reconnect reseed adds 26 × 20 = 520 weight, spread over 52 s by `SeedPace`.
- Aster disconnects every 24 h, so this is a daily event.

---

## 9. Enabling order on TEST

Run as `ops/enable-aster-perp.sql` in stages. Use the same shape as `ops/enable-gmx-perp.sql`: one
transaction per stage, with the stage selected by editing the `wanted` rows, or with one file per
stage if preferred.

| # | Enable | Watch before the next step |
|---|---|---|
| 1 | `segment.status = 'enabled'`; `discovery` 900, `spec_versions` 3600 | Instruments: 441 TRADING in scope, **no symbolType-1 rows at all**; 25 or 26 with `collect = true`; one log line for `contractType ""`; no status exception; `asset` gets only expected new codes (plus NEX and WOJAK via aliases); `listed_at` filled on all rows; funding interval from `fundingInfo`, not the 8 h default; no `collector_gap`. |
| 2 | `snapshot` 15 (REST path by profile) | 4 rows/min per instrument, **including TRX, DOT and BCH**; `open_interest` and `open_interest_at` filled (OI cycle started); `venue_ts` lag < 2 s; no 429 in `collector_run`; weight headers not needed. |
| 3 | `depth` 300 | Log: depth feed subscribed **26** streams (not 441); seed walk done within ~1 min; `book_reach_*` ≥ 50 bps on BTC and ETH; `depth_at` fresh; no reseed loop; REST fallback (if seen) at limit 500. |
| 4 | `candles` 60, `candles_mark` 60, `candles_index` 60 | Log: market feed subscribed 3 + 52 = **55** streams; closed bars only; `coverage` rows without `limit_hit` loops; mark/index present for all 26. |
| 5 | `funding` 3600 | First pass backfills to `backfill_hours`; `funding_time` spacing matches each instrument's interval (1 h / 4 h / 8 h). |
| 6 | `trades` 5 | `trade` rows ≈ aggTrade rate (BTC ~0.9/s, ETH ~0.6/s); `source = 'ws'`; after a forced reconnect, a gap is recorded, not papered over; no duplicate key errors. |
| 7 | `book` 15 | `book_topn` rows with `seq` monotonic per instrument; `is_snapshot = true`; row size recorded for §8. |
| 8 | `open_interest` 300 | `open_interest_history.source = 'rest'`, 12 buckets/h; **no 404s** in `collector_run` (proves the profile returns `[]` instead of calling `/futures/data`). |
| 9 | `liquidations` 900 — **only once the §3 note and capability row are in and Studio shows the label** | hourly buckets appear only for symbols with events; Studio shows "sampled — lower bound". |
| 10 | +24 h | The 24 h server disconnect happened on both feeds, was reconnected and reseeded, and gaps were recorded. Re-measure the gate from `collector_run` (429 count, request rate) and set `request_budget_source = 'measured'` with the numbers. Disk per table vs §8. Then decide on PROD. |

Stop conditions at any step:
- any 418;
- repeated WS closes;
- a 429 burst → `VenueGate.Penalize` should back off; if it does not, disable the segment first and
  investigate second.

---

## 10. Rationale — why this venue is worth the effort

DefiLlama temporarily delisted Aster's volume data after detecting an unusually high correlation
with Binance's perpetual volumes. We already collect Binance USDⓈ-M with this code path and these
clocks. Collected the same way, Aster lets us **measure that correlation on our own data** instead of
citing someone else's claim, and the result is publishable either way.

That changes priorities for this venue:
- **`trades` is as important as `snapshot`.** A volume correlation at 1-minute or hourly grain
  cannot tell mirrored flow from shared news flow. Trade-level data (timestamps to the millisecond,
  sizes, sides, lead/lag against Binance's tape) can.
- **The aggTrades backfill (§2, note 1) is worth building next**, because it reaches back to 2021
  without a key, so the study does not have to wait months for live history.

**A preliminary check from REST klines — not our stored data, and not a result:**

| Window, 1 m quote volume | Aster ~ Binance | Bybit ~ Binance (control) |
|---|---|---|
| BTCUSDT, 1 000 min: 1 m / log / hourly | 0.68 / 0.77 / 0.96 | 0.72 / 0.75 / 0.92 |
| ETHUSDT | 0.70 / 0.77 / 0.95 | 0.75 / 0.78 / 0.96 |
| SOLUSDT | 0.26 / 0.68 / 0.41 | 0.73 / 0.75 / 0.95 |

- On this ~17-hour window, **Aster's volume correlates with Binance no more than Bybit's does.**
  Naive Pearson correlation of volumes does not reproduce the claim.
- The same statistic over a 1 500-minute window read 0.89 for BTC, so it is window-sensitive and
  proves nothing on its own.
- Two things did stand out:
  - Aster's average trade is much larger than Binance's (BTC $11.7 k vs $3.0 k; ETH $8.5 k vs
    $1.8 k);
  - ASTERUSDT turns over 90 % of Binance's volume on about 27 % of its trade count.
- Both are questions for the tape, not for bars. **This is the case for collecting trades on day
  one.**

---

## 11. Not verified here, and how to close each

1. **Probe from TEST and PROD egress.** CloudFront limits and bans are per IP, and Aster has not yet
   seen those IPs. The operator runs:

   ```bash
   ssh csx-test 'for p in /fapi/v1/ping /fapi/v1/exchangeInfo "/fapi/v1/openInterest?symbol=BTCUSDT" "/fapi/v1/depth?symbol=BTCUSDT&limit=100"; do curl -s -o /dev/null -D - -w "%{http_code} %{time_total}s\n" "https://fapi.asterdex.com$p" | grep -Ei "^HTTP|x-mbx-used-weight|x-amz-cf-pop|s$"; sleep 1; done'
   ```

   Run the same on PROD through `csx-datahub-jump` when PROD enabling is decided.
2. **The 24 h disconnect and the 5-minute ping.** These are from the docs only; the capture ran
   10 minutes. Step 10 of §9 verifies them.
3. **What CloudFront's limit is.** Only one trigger was seen (`/time`, ~40/min). Do not probe it on
   purpose from a collector IP; the 5 req/s gate plus `Penalize` is the answer until step 10
   measures it.
4. **Row sizes marked ≈ in §8.** Step 7 and step 10.
5. **`tradingMode` meaning.** Undocumented, so it is stored in `raw_json` and not used.

---

## 12. Size of the work

| Piece | Estimate |
|---|---|
| `BinanceUsdmProfile` + `SymbolType` + scope via profile + REST depth limit + OI-history switch + ticker switch | 0.5 d |
| Feeds: symbol source + stream-cap guard (no sharding) | 0.5 d |
| `ExchangeWorker` arm, migration 0065, ops script | 0.5 d |
| Fixtures and tests (§4.3), including the Binance-unchanged pin | 1 d |
| Liquidations sample note, capability row and Studio label (§3) — for Aster, and the same three for Binance as a follow-up | 0.5 d |
| **Total to a mergeable PR** | **≈ 3 d** |
| TEST enabling and 24 h observation (§9) | 1 d elapsed |
| Later, separate: aggTrades backfill; `!markPrice@arr@1s` snapshot path; feed sharding beyond 98 symbols; RWA with session calendars | not in this estimate |
