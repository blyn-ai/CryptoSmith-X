# Binance USDⓈ-M public WebSocket — captured frames

Every file in this directory is the verbatim text of frames that crossed the wire, written by
`capture.py` in this directory on **2026-09-06**. Nothing here was written by hand from
documentation, and nothing below describes a request this capture did not actually make.

That last sentence is a rule, not a flourish. The WEEX fixture directory next door made a claim in
its own README about depth levels it had never subscribed to, and the claim was believed for exactly
as long as nobody checked. So: what is asserted here is what `session-transcript.txt` shows, and
where a fact is not in the transcript it is not stated.

## What was captured

| file | what it is |
|---|---|
| `depth-snapshot.json` | the REST `/fapi/v1/depth?symbol=BTCUSDT&limit=1000` response, taken **while the socket was already buffering** |
| `depth-deltas.jsonl` | 60 consecutive `depthUpdate` payloads, one per line, straddling that snapshot |
| `subscribe-ack.json` | the answer to a `SUBSCRIBE` |
| `book-ticker.json` | one `btcusdt@bookTicker` frame, kept for its field names alone |
| `session-transcript.txt` | the annotated log of the whole run, including the routing probes |
| `capture.py` | what produced all of the above |

Connection: `wss://fstream.binance.com/public/stream`, opened with **no query string**, then
`{"method":"SUBSCRIBE","params":["btcusdt@depth@100ms"],"id":1}`. Every payload therefore arrives
wrapped as `{"stream":"...","data":{...}}`.

## The depth run

The run is the point of this directory, and it was captured in the order that makes it usable:
subscribe, buffer 6 s of frames, fetch the REST snapshot mid-run, buffer 8 s more. 144 frames were
seen in total; the 60 kept here are the window around the seam.

* snapshot `lastUpdateId` = **11488689156874**, 1000 levels a side
* 8 of the 60 frames predate it (`u < lastUpdateId`)
* the 9th frame is the seam: `U`=11488689150781, `u`=11488689158935 — so `U <= lastUpdateId <= u`
* that same seam frame has `pu`=11488689150608, which is **not** `lastUpdateId`, and `U` is **not**
  `lastUpdateId + 1`
* across the 60 frames there are 59 adjacent pairs, and `pu` equals the previous frame's `u` in
  **all 59** — zero violations, before and after the seam alike

The third and fourth bullets are why this directory exists. A builder written to WEEX's rule
(`U == previous u`) or to Binance **spot**'s rule (`U <= lastUpdateId + 1 <= u`, then
`U == previous u + 1`) would reject that seam frame, declare a gap, reseed, and reject the next one
forever. USDⓈ-M needs two rules: `U <= lastUpdateId <= u` at the seam, `pu == previous u` after it.

Field names on a `depthUpdate` payload, as captured:
`e`, `E`, `T`, `s`, `ps`, `U`, `u`, `pu`, `b`, `a`, `st`. Note `e` beside `E`, and `u` beside `U`:
two pairs differing only in case, in one object.

## Routing — the part with no error message

Binance splits its public socket across `/public`, `/market` and `/private`. A stream asked for on
the wrong path does not fail. From the transcript, all four probes verbatim:

| path | stream | handshake | subscribe ack | data frames |
|---|---|---|---|---|
| `/public/stream` | `btcusdt@depth@100ms` | 101 | `{"result":null,"id":99}` | **76 in 8 s** |
| `/public/stream` | `!markPrice@arr@1s` | 101 | `{"result":null,"id":99}` | **0 in 12 s** |
| `/market/stream` | `btcusdt@depth@100ms` | 101 | `{"result":null,"id":99}` | **0 in 12 s** |
| `/market/stream` | `!markPrice@arr@1s` | 101 | `{"result":null,"id":99}` | **16 in 8 s** |

The handshake succeeds either way. The subscribe is acknowledged either way, with the identical
success envelope. The only thing that differs is whether frames ever arrive — which is why
`BinanceWsFeed` has a startup liveness timeout, and why that timeout is not a nicety.

## What was NOT captured, and is therefore not claimed

* **The per-connection stream cap.** A separate probe subscribed all 566 in-scope symbols over one
  connection in six frames of 100 and saw 22 302 frames from all 566 distinct symbols in twelve
  seconds. That probe's output is quoted in `BinanceWsFeed`'s remarks but its frames are not stored
  here — 22 302 frames is not a fixture, it is a landfill. What is stored is the single-symbol run,
  which is what the tests replay.
* **`@depth` at other update speeds** (`@depth`, `@depth@500ms`) and **`@depth5/10/20`**. Never
  subscribed. Whether they are accepted is unknown to this repository.
* **The `/private` path.** Never opened; this service holds no keys.
* **Reconnect and unsubscribe behaviour.** Not probed. The feed's reconnect path is exercised by
  `WsConnection`'s own tests, not by frames from this venue.
