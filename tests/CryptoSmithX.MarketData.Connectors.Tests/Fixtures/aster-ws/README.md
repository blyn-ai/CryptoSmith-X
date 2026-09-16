# Fixtures/aster-ws

Captured live from `wss://fstream.asterdex.com/stream`, 2026-09-16, the same session as
`Fixtures/aster`. `capture_ws.py` (kept alongside this note in the session that produced it, not
checked in — same one-off shape as `Fixtures/binance-ws/capture.py`) subscribed
`btcusdt@depth@100ms` plus the three whole-venue market streams and wrote frames verbatim.

- **`subscribe-ack.json`, `depth-deltas.jsonl`, `depth-snapshot.json`** — the same seam shape
  `Fixtures/binance-ws` pins: 8 frames captured before the REST snapshot (`limit=1000`), the run
  trimmed to straddle it. Zero `pu`-chain violations across the captured run — the book-sequence
  claim in blueprint §1.4 ("0 breaks in 10 min"), reproduced on a fresh capture.
- **`ticker-arr.json`** — one `!ticker@arr` frame, live: **6 entries**, not the whole ~600-symbol
  venue. This is the fact `BinanceUsdmProfile.Aster.TickersFromMarketFeed = false` exists because
  of (blueprint §1.4/§4.2): under the Binance profile's WS-ticker branch, every symbol NOT in this
  frame would simply be missing from that pass, with no gap recorded.
- **`markprice-arr.json`** — one `!markPrice@arr@1s` frame: 746 entries, matching blueprint §1.4's
  measured count — every symbol, every second, unlike the ticker array above.
- **`forceorder.json`** — one real `!forceOrder@arr` event, caught live in the capture window
  (MAXUSDT, filled): `{e,E,o:{s,S,o,f,q,p,ap,X,l,z,T}}`, the same shape Binance's `!forceOrder@arr`
  uses (`BinanceMarketWsFeed.HandleForceOrder` already reads it unchanged).
- **`session-transcript.txt`** — the capture run's own log lines, for the numbers quoted above.
