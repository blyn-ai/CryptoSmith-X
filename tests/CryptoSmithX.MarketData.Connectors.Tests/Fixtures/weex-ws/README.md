# WEEX contract V3 public WebSocket — captured protocol

Every file in this directory is the verbatim text of frames that crossed the wire. Nothing
here was written by hand from documentation, and nothing was reformatted: the `.json` files
each hold one frame exactly as received, `depth-deltas.jsonl` holds one frame per line, and
the `.txt` files add only `>` / `<` direction markers and `#` comment lines around untouched
frame text.

## Why this directory exists before any feed code

Commit `100f605` deferred a WEEX WebSocket feed and wrote down its reason: the V2 contract
protocol used chained `startVersion` / `endVersion` deltas. That conclusion was true when it
was written and went stale silently — WEEX shipped contract V3 and retired V2, and nothing in
the repository noticed, because the protocol fact lived in a commit message instead of in a
test. This capture is the fix for that class of error: the protocol is evidence in the tree,
and `WeexWsProtocolTests` fails loudly if it is ever replaced by frames from a different
protocol generation.

## Capture metadata

| | |
|---|---|
| URL | `wss://ws-contract.weex.com/v3/ws/public` |
| Captured (UTC) | 2026-09-06, 10:57:25Z – 11:06:48Z |
| Tool | Python 3.9.6 with `websockets` 15.0.1, driven by the scripts in this directory |
| Scripts | `capture.py` (greeting, ping, depth, ticker, kline, unsubscribe, reject), `capture_ping.py` (ping/pong both directions), `capture_unsub.py` (unsubscribe, repeated-reject close) |
| Symbol | `BTCUSDT` |
| Re-run | `python3 capture.py <this-directory>` — it overwrites the fixtures with a fresh live capture |

`ping_interval=None` was set on every connection, so the client library never sent a control
frame on its own: every PING/PONG recorded in `ping-pong.txt` is either the server's or one
sent deliberately by the script.

## The chaining rule

**Each depth frame's `U` is equal to the immediately preceding frame's `u` for that symbol —
not to `u + 1` — and the run is seeded by the `u` of the `depthSnapshot` that the socket
itself delivers.** Within a single frame `U < u` strictly. A frame whose `U` does not equal
the last applied `u` means the client missed frames and must resynchronise from a fresh
snapshot.

This is the load-bearing difference from Binance, whose otherwise identically-shaped protocol
requires `U <= lastUpdateId + 1 <= u` against a REST-seeded book. WEEX's rule is an exact
equality against the previous frame, and WEEX delivers the snapshot on the socket, so there is
no REST-seed race to lose. Verified over the 60 consecutive deltas in `depth-deltas.jsonl`:
60 of 60 chain exactly, 0 breaks, spanning `u` 16786244375 → 16786246795 in 29.5 s.

## Frame shapes

### Connect

The server greets unprompted the moment the socket opens; no request precedes it.

```
{"cid":"a605e7d3-2c56-63de-d330-7cfd5bd519b4","event":"connected","time":"1788692245686"}
```

`cid` is a per-connection id. `time` is epoch milliseconds **as a string**, unlike `E` on data
frames, which is a number.

### Subscribe / unsubscribe

Binance-shaped envelope. `SUBSCRIBE` and `UNSUBSCRIBE` take `params` of `<symbol>@<channel>`
and echo the caller's `id` in the ack.

```
> {"method":"SUBSCRIBE","params":["btcusdt@depth"],"id":1}
< {"result":true,"id":1}
> {"method":"UNSUBSCRIBE","params":["btcusdt@depth"],"id":1002}
< {"result":true,"id":1002}
```

The symbol is case-insensitive: `btcusdt@depth` and `BTCUSDT@depth` were both accepted and
both produced a snapshot. `s` on data frames always comes back upper-case.

A rejected channel is answered, not fatal on its own, and the error text names the offending
channel lower-cased:

```
< {"result":false,"id":99,"msg":"INVALID_ARGUMENT: invalid event : totally_bogus_channel"}
```

An unparseable frame gets a different text, `INVALID_ARGUMENT: unrecognized message : ping`,
and a well-formed frame with no recognisable event gets the same `invalid event : ` with an
empty channel name.

**Repeated rejects kill the whole connection.** `invalid-channel-close.txt` records six
rejected subscribes on one connection; the server answered all six and then closed with
`1007 Unrecognized message sent multiple times`. The threshold was six in both runs that hit
it. A feed that retries an unknown channel therefore loses every other subscription on that
socket, so an unknown channel must be dropped, not retried.

### Ping / pong

Three separate mechanisms, all confirmed live in `ping-pong.txt`:

- Client → server, application level: `{"method":"PING","id":7}` → `{"result":true,"id":7}`.
  The `id` is echoed; sent without an `id` the reply is a bare `{"result":true}`.
- Client → server, RFC 6455 control ping: the server answers with a control pong echoing the
  payload, in 0.26 s. Ordinary transport keepalive works.
- Server → client, application level: `{"event":"ping","time":"1788692610000"}`, unprompted,
  every 60 s on the minute boundary. Over a 4-minute idle none of these were answered and the
  server did not close the connection, so answering is not required to stay connected. It is
  still the cheapest liveness signal available and worth treating as one.

### Depth

`@depth` (default), `@depth15` and `@depth200`. The level appears on every frame as `l`, and
plain `@depth` returns `l: 15`, so the default is 15. No other level was accepted.

The first frame after an ack is always the snapshot; deltas follow at roughly 500 ms.

```
{"e":"depthSnapshot","E":...,"s":"BTCUSDT","U":16786244339,"u":16786244375,"l":15,"d":"SNAPSHOT","b":[["79979.8","2.5926"],...],"a":[...]}
{"e":"depth","E":...,"s":"BTCUSDT","U":16786244375,"u":16786244424,"l":15,"d":"CHANGED","b":[["79979.8","2.8356"],["79977.9","0"]],"a":[...]}
```

`e` is `depthSnapshot` or `depth`; `d` is `SNAPSHOT` or `CHANGED` and carries the same
distinction redundantly. `b` and `a` are `[price, qty]` pairs, both **strings**. A qty of
`"0"` is a removal of that price level, not a level with no size. Re-subscribing to a symbol
already subscribed produces a fresh snapshot and restarts the chain.

### Ticker

`@ticker`. `d` is an array of one object. Field letters follow Binance's 24hr ticker: `p`
price change, `P` percent, `w` weighted average, `c` last, `o`/`h`/`l` open/high/low, `v`
base volume, `q` quote volume, `O`/`C` window open/close ms, `n` trade count. `m` and `i` are
WEEX additions — mark price and index price — and are not in Binance's ticker.

### Kline

`@kline_<interval>`, e.g. `@kline_1m`. The first frame is `klineSnapshot` carrying a long
history array; subsequent `kline` frames carry the single live candle. Both add `p`, the
price basis (`LAST_PRICE`), which Binance does not have. Candle fields are Binance-shaped:
`t`/`T` open/close ms, `i` interval, `o`/`c`/`h`/`l`, `v` base volume, `n` trades, `q` quote
volume, `V`/`Q` taker buy volumes.

### Trade

`@trade`, captured only in the session transcript. `tradeSnapshot` then `trade`; `t` is a
UUID string, not the integer id Binance uses.

### Channels that do not exist

`@bookTicker`, `@markPrice` and `@miniTicker` were all rejected with
`INVALID_ARGUMENT: invalid event : <name>`. There is no top-of-book channel on this endpoint —
best bid and ask have to come from `@depth`.

## Files

| file | contents |
|---|---|
| `connect-greeting.json` | the unprompted greeting frame |
| `subscribe-ack.json` | ack for `btcusdt@depth`, id 1 |
| `subscribe-error.json` | the rejected-channel error, exact text |
| `unsubscribe-ack.json` | ack for the unsubscribe, id 1002 |
| `unsubscribe.txt` | the unsubscribe request and ack, with frame counts before and after showing the stream stops |
| `ping-pong.txt` | all three ping mechanisms, both directions |
| `depth-snapshot.json` | the `depthSnapshot` frame that seeds the chain below |
| `depth-deltas.jsonl` | 60 consecutive `depth` frames for `BTCUSDT`, one per line, in arrival order |
| `ticker.json` | one `ticker` frame |
| `kline.json` | one live `kline` frame |
| `kline-snapshot.json` | the `klineSnapshot` history frame |
| `invalid-channel-close.txt` | six rejected subscribes and the `1007` close that follows |
| `session-transcript.txt` | the full annotated transcript of the main capture connection |
| `capture*.py` | the scripts that produced all of the above |

## Not established here

- Whether the `u` of one symbol's depth stream relates to any other symbol's. The ids look
  venue-global (they advance while only one symbol is subscribed), but that was not measured
  and no feed should depend on it.
- The maximum number of channels one connection accepts. An earlier unrecorded run reported
  1011 depth channels on one socket; this capture used one symbol and does not confirm it.
- What happens to the chain across a WEEX-side restart, as opposed to a client reconnect.
