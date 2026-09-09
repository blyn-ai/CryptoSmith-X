# Trade and liquidation frames, captured live (2026-09-09)

Captured with `capture.py` against the four venues' production sockets, before any of the parsing
in this phase was written — the shapes below are what the venues actually sent, not what their docs
describe.

| file | venue | channel |
| --- | --- | --- |
| `kraken_trade.jsonl` | Kraken Futures | `feed: trade` (+ `trade_snapshot` backlog) |
| `weex_trade.jsonl` | WEEX | `@trade` (+ `tradeSnapshot`) |
| `hl_trades.jsonl` | Hyperliquid | `trades` |
| `binance_aggtrade_force.jsonl` | Binance USDⓈ-M | `@aggTrade` on `/market/stream` |
| `binance_forceorder.jsonl` | Binance USDⓈ-M | `!forceOrder@arr` on `/market/stream` |

## What each venue actually gives

**Kraken** is the only one that types a trade: every frame carries `type`, and a liquidation arrives
as an ordinary trade marked `liquidation`. That is why this venue needs no liquidation channel — and
why `trade.trade_type` exists as a column at all. It is also the only venue publishing `seq`.

```
{"product_id":"PF_XBTUSD","feed":"trade","uid":"418068fe-…","side":"buy","type":"fill",
 "time":1788964867837,"qty":0.0001,"price":79247.0,"seq":891201}
```

**WEEX** batches trades into `d[]` per frame, ids them with a uuid, and marks nothing: no `seq`, no
type. `m` is the buyer-is-maker flag Binance's tape also uses.

```
{"e":"trade","E":1788964868793,"s":"ETHUSDT",
 "d":[{"T":1788964868309,"t":"1961dae6-…","p":"2508.99","q":"0.011","v":"27.59889","m":false}]}
```

**Hyperliquid** batches into `data[]`, ids with a numeric `tid`, and states the aggressor's side
directly as `B`/`A`.

```
{"channel":"trades","data":[{"coin":"BTC","side":"A","px":"79234.0","sz":"0.00007",
 "time":1788964859615,"hash":"0x…","tid":721890601850362,"users":[…]}]}
```

**Binance** splits the two: `@aggTrade` is the tape (aggregate id `a`, maker flag `m`), and
`!forceOrder@arr` is a separate liquidation ORDER stream. Both describe the same executed quantity —
a liquidation's fills appear on the tape as well — which is why the forceOrder stream is bucketed
into `liquidation_volume_history` rather than stored a second time as `trade` rows.

```
{"stream":"!forceOrder@arr","data":{"e":"forceOrder","E":1788965417449,
 "o":{"s":"RAYSOLUSDT","S":"SELL","o":"LIMIT","f":"IOC","q":"70.5","p":"1.3810000",
      "ap":"1.3911902","X":"FILLED","l":"6.9","z":"70.5","T":1788965416437}}}
```

Liquidations are sporadic: a 150-second capture caught none at all, a 9-minute one caught several.
That is the venue being quiet, not the stream being wrong — worth knowing before concluding the
subscription is broken.
