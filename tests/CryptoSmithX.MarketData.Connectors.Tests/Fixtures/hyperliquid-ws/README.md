# Hyperliquid public WebSocket — `activeAssetCtx` and `candle` — captured protocol

Same discipline as `Fixtures/weex-ws`: every file here is a frame that actually crossed the
wire, captured before any feed code was written for these two channels.

## Capture metadata

| | |
|---|---|
| URL | `wss://api.hyperliquid.xyz/ws` |
| Captured (UTC) | 2026-09-09 |
| Tool | Python 3.9, `websockets` 15.0.1 |
| Script | `capture.py` |
| Coin | `BTC` |
| Re-run | `python3 capture.py <this-directory>` |

`l2Book` is not captured here — it is already covered by the working `HyperliquidWsFeed` and
its own tests; this directory only adds the two channels new to this phase.

## Subscribe

```
> {"method":"subscribe","subscription":{"type":"activeAssetCtx","coin":"BTC"}}
> {"method":"subscribe","subscription":{"type":"candle","coin":"BTC","interval":"1m"}}
```

Acked individually, `subscription-response-ctx.json` / `subscription-response-candle.json`.
One coin per subscribe — confirmed no batched form, matching the existing `l2Book` feed's own
subscribe loop (`HyperliquidWsFeed.RefreshSymbolsAsync`).

## `activeAssetCtx`

`active-asset-ctx.json`. `data.ctx` is the exact field set of the REST `metaAndAssetCtxs`
second array element (`HlAssetCtx` in `HyperliquidDtos.cs`) plus three fields that record does
not map: `premium`, `impactPxs`, `dayBaseVlm`. All numeric fields are **strings**. Pushed
continuously — 16 frames arrived in 15 s idle-market observation, so this is not an on-change-
only stream.

```json
{"channel":"activeAssetCtx","data":{"coin":"BTC","ctx":{
  "funding":"0.0000125","openInterest":"34554.56282","prevDayPx":"78287.0",
  "dayNtlVlm":"2115733633.6778900623","premium":"-0.0002501933","oraclePx":"79138.8",
  "markPx":"79111.0","midPx":"79118.5","impactPxs":["79118.0","79119.0"],
  "dayBaseVlm":"26942.23195"}}}
```

`HlAssetCtx`'s existing properties (`Funding`, `OpenInterest`, `DayNtlVlm`, `OraclePx`,
`MarkPx`, `MidPx`) already match this shape under `JsonSerializerDefaults.Web` case-insensitive
binding — no new DTO needed, this frame's `ctx` object deserialises straight into it.

## `candle`

`candle.json`. **No history burst on subscribe** — this is the one place this venue's protocol
differs from WEEX's `klineSnapshot`: the first frame after the ack is already just the single
live (currently forming) bar, and every subsequent frame updates that same bar in place until
it rolls to the next minute. Confirmed over 6 frames: `t` stayed `1788936660000` throughout while
`c`, `v`, `n` changed on every push. A resubscribe (or reconnect) therefore seeds NO closed-bar
history at all, unlike WEEX's ~300-minute snapshot — `CandleCache.TryGetRange` naturally returns
false for any window reaching further back than "since this connection last subscribed", and the
caller falls through to REST for the whole request, exactly as intended.

```json
{"channel":"candle","data":{"t":1788936660000,"T":1788936719999,"s":"BTC","i":"1m",
  "o":"79139.0","c":"79118.0","h":"79139.0","l":"79110.0","v":"0.77139","n":89}}
```

Field set is `HlCandle` (`HyperliquidDtos.cs`) exactly — `t`/`T` numbers (ms), `o`/`c`/`h`/`l`/`v`
strings, `n` a number — deserialisable with the client's existing `CandleJson` options
(`PropertyNameCaseInsensitive = false`, needed because `t` and `T` would otherwise collide).

## Not established here

- What a gap in the candle stream looks like (a quiet coin, a dropped connection) — not
  observed in a 15 s capture on BTC, which trades every second.
- Whether `activeAssetCtx` ever omits a field present in the REST `ctx` object.
