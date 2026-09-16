# Fixtures/aster

Captured live from `fapi.asterdex.com`, public endpoints only, no key — 2026-09-16 (the same day and
egress as `plans/aster-venue-blueprint.md`). Trimmed to the symbols the tests actually exercise:

- `BTCUSDT`, `ASTERUSDT` — in scope (`symbolType = 0`, USDT-quoted `PERPETUAL`, `TRADING`).
  `ASTERUSDT` carries a 4 h funding interval (`fundingInfo`), unlike the 8 h default `BTCUSDT` sits
  on — the same distinction Binance's own fixture set pins with `XTZUSDT`.
- `1000SHIBUSDT` — a `1000`-prefixed contract, in scope, for the alias-resolution path.
- `TSLAUSDT` — `contractType = PERPETUAL`, `symbolType = 1`: the RWA marker this whole blueprint is
  about. Admitted by Binance's own allowlist rule (`contractType` is carried) and excluded only by
  Aster's extra `symbolType == 0` check.
- `XAUUSD1`, `BTCUSD1` — `quoteAsset = USD1`, excluded by `BinanceMarkets.UsdFamily` regardless of
  `symbolType`.
- `MBLUSDT` — `contractType = ""`, `status = PENDING_TRADING`: already excluded and logged once by
  the existing contract-type allowlist, unchanged by this venue.
- `TONUSDT` — `status = SETTLING`: listed, not trading: maps to `Halted`, same as Binance's `OMGUSDT`
  case. Absent from `bookticker.json` (SETTLING symbols do not quote) but present in
  `premiumindex.json`/`ticker24hr.json` — the venue's own honest omission, not a fixture gap.

## Files

| File | Endpoint | Weight (measured) |
|---|---|---|
| `exchangeinfo.json` | `GET /fapi/v1/exchangeInfo` | 1 |
| `fundinginfo.json` | `GET /fapi/v1/fundingInfo` | 10 |
| `bookticker.json` | `GET /fapi/v1/ticker/bookTicker` | 2 |
| `premiumindex.json` | `GET /fapi/v1/premiumIndex` | 10 |
| `ticker24hr.json` | `GET /fapi/v1/ticker/24hr` | 40 |
| `openinterest.json` | `GET /fapi/v1/openInterest?symbol=BTCUSDT` | 0–1, undocumented |
| `depth.json` | `GET /fapi/v1/depth?symbol=BTCUSDT&limit=500` | 10 |
| `klines.json` | `GET /fapi/v1/klines?symbol=BTCUSDT&interval=1m&limit=10` | 1 |
| `markpriceklines.json` | `GET /fapi/v1/markPriceKlines?symbol=BTCUSDT&interval=1m&limit=5` | 2 |
| `indexpriceklines.json` | `GET /fapi/v1/indexPriceKlines?pair=BTCUSDT&interval=1m&limit=5` | 2 |
| `fundingrate.json` | `GET /fapi/v1/fundingRate?symbol=BTCUSDT&limit=10` | 0 (no header) |
| `openinteresthist_404.html` | `GET /futures/data/openInterestHist?symbol=BTCUSDT&period=5m&limit=5` | — 404, HTML, 6.9 KB, via CloudFront |

## The bodiless 429 — not captured here, and deliberately not provoked

Blueprint §1.3: CloudFront answered `HTTP 429` with an **empty body**, no `Retry-After`, no
`x-mbx-*` header, after ~40 calls/minute to `/fapi/v1/time` at a measured weight of ~107 of 2400 —
a second limiter invisible in `rateLimits`. The blueprint's own text is explicit that this must not
be probed on purpose from a collector IP.

`BinanceUsdmClientEnsureVenueSuccessTests` (in `AsterProfileTests.cs`) constructs this response
directly — `new HttpResponseMessage(HttpStatusCode.TooManyRequests)` with no content and no headers
set — rather than replaying a captured file, since there is nothing to replay: the shape being
tested IS the absence of a body. `EnsureVenueSuccess` throws `VenueRateLimitedException` before any
attempt to read content as JSON, on any client (Binance's or Aster's) — this is not new code, the
test pins existing, venue-agnostic behaviour against the specific empty-body shape Aster's CDN
produces.
