# Pairs monitor — the public venue comparison

The screen a visitor of the CryptoSmith X storefront sees: one row per platform quoting the
same asset, every field the collector stores, and the age of the call behind each figure.

`index.html` is a static recreation on the real tokens, generated from the same illustrative
rows the product page uses. It is honest about two things and cuts one corner:

- **Honest:** the freshness model (three calls, three clocks, a fade over one window, △ past
  it, `degraded` past twelve of them) and the verdict model (BEST and WORST at the two ends
  of every comparable column, direction per column, no mark in between).
- **Cut corner:** no candle panels. Those need TradingView Lightweight Charts, self-hosted
  from the product repo (`wwwroot/vendor/lightweight-charts/`), one instance per venue with
  the time scales tied together — see `components/market/CandlePanel.prompt.md`. The live
  page mounts seven of them under the table.

The **INK** button in the header flips `data-theme="night"` so both registers of the token
set can be checked on one screen.

## What to copy from here

The column order. Each call's columns are contiguous — turnover sits with the ticker fields
because it arrives in the ticker response, not after open interest where the source admin
page had it — and that is the only reason the three header bands can be drawn at all.
