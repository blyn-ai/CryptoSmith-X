# Binance `/market/stream` — captured protocol

Same discipline as `Fixtures/weex-ws`: raw frames from the live socket, captured before any
feed code depended on their shape. This is the SECOND Binance socket — `BinanceWsFeed` already
covers `/public/stream` (`@depth@100ms`) and is not touched here.

## Capture metadata

| | |
|---|---|
| URL | `wss://fstream.binance.com/market/stream` |
| Captured (UTC) | 2026-09-09 |
| Tool | Python 3.9, `websockets` 15.0.1 |
| Script | `capture.py` |
| Subscribed | `!ticker@arr`, `!markPrice@arr@1s`, `btcusdt@kline_1m` |
| Re-run | `python3 capture.py <this-directory>` |

## The envelope — the fact worth capturing this for

`/market/stream` is a COMBINED-stream endpoint: every frame is wrapped
`{"stream":"<name>","data":<payload>}`, not the bare payload `BinanceWsFeed` reads off
`/public/stream`. `subscribe-ack.json` is the plain `{"result":null,"id":1}` this venue always
answers a SUBSCRIBE with, correctly routed or not (see `BinanceWsFeed`'s own class remarks) — it
took real data frames arriving right after to confirm this path is the correct one, not the ack.

## `!markPrice@arr@1s` — sharded, not one push per second

`mark-price-arr.json`. `data` is an array of `markPriceUpdate` objects — but **not the whole
venue every second**: two different frame sizes recurred throughout the capture (744 entries and
191), and the 191-entry shard held metals/equity-tokenized perpetuals (`XAUUSDT`, `TSLAUSDT`, …)
while the 744-entry shard — the one saved here — held the crypto derivatives universe (665
USDT-margined linear, 30 COIN-M/dated, 49 USDC- and BTC-quoted). **A single push must be treated
as a partial update merged into a per-symbol cache, never as an atomic full-venue snapshot** —
the design this repository already uses for every other WS cache (`MarketCache<T>.Set` per
entry) happens to be exactly right for this reason, not by luck.

The 744-entry shard mixes symbols outside `binance-usdm`'s scope (COIN-M, USDC-margined) — the
feed must filter each entry against the same known in-scope symbol set `BinanceWsFeed` already
computes from `BinanceMarkets.IsInScope`, the same way it already filters what to subscribe.

Fields, all values **strings** except `E`/`T` (ms) and `st`: `p` mark price, `ap` "average
price", `P` estimated settlement price, `i` index price, `r` funding rate, `T` next funding
time.

```json
{"stream":"!markPrice@arr@1s","data":[
  {"e":"markPriceUpdate","E":1788936948001,"s":"BTCUSDT","p":"79107.36658171",
   "ap":"79107.36658171","P":"79176.63756423","i":"79139.09565217","r":"0.00008359",
   "T":1788940800000,"st":1}, ...]}
```

## `!ticker@arr` — same sharding, 24hr-ticker shape

`ticker-arr.json`. Same combined-array behaviour as markPrice. Fields follow the venue's
standard 24hr ticker: `c` last price, `q` quote-asset volume (this dataset's `Turnover24h`),
`o`/`h`/`l` window open/high/low, `v` base volume, `n` trade count — all price/volume fields
strings, `n` a number.

## `<symbol>@kline_1m`

`kline.json`. Per-symbol, not batched — one push per update, addressed the same way
`BinanceWsFeed`'s depth subscriptions already are (`<symbol>@kline_1m`, chunked SUBSCRIBE
frames). The payload nests under `data.k`, and carries `x`: **a boolean the other two venues'
kline-shaped streams do not have — true once this bar is closed, false while it is still
forming.** WEEX and Hyperliquid require inferring closure from the next bar's open time
advancing; here the venue says so directly. Not required for correctness (the cache keys on
open time regardless, matching the other two), but worth using if a future pass wants to avoid
ever caching a bar known to still be moving.

```json
{"stream":"btcusdt@kline_1m","data":{"e":"kline","E":1788936947361,"s":"BTCUSDT","k":{
  "t":1788936900000,"T":1788936959999,"s":"BTCUSDT","i":"1m","f":8062213398,"L":8062214482,
  "o":"79095.40","c":"79104.00","h":"79111.30","l":"79080.60","v":"42.797","n":1079,
  "x":false,"q":"3384949.92680","V":"14.001","Q":"1107450.49620","B":"0"}}}
```

## Not established here

- Whether `!ticker@arr` and `!markPrice@arr@1s` ever complete their sharded sweep within one
  second, or drift; only ~12 s were captured.
- The maximum symbols one `<symbol>@kline_1m` SUBSCRIBE batch accepts on this path — assumed
  the same as `/public/stream`'s measured 100 (`BinanceWsFeed.SubscribeChunk`), not re-measured
  here.
