# marks

Vendored identity marks for the Studio surface: an exchange's own logo, and an asset's.
Third-party artwork, self-hosted, nothing loads from a CDN.

**Read `docs/logos.md` first.** It carries the reasoning — why this is allowed at all against
the design system's "No icons" rule, what a missing mark does, and how to add a venue. This
file is only the map.

```
marks/
  MANIFEST.json          one record per file: source, date, terms, and what we changed
  index.json             generated — which codes actually have a file
  licenses/              the licence texts the sources require
  venue/branded/<code>.svg   <- exchange.code, verbatim: kraken, binance, hyperliquid …
  venue/mono/<code>.svg
  asset/branded/<CODE>.svg   <- canonical asset.code, verbatim: BTC, ETH, HYPE …
  asset/mono/<CODE>.svg
  tools/index.py         rebuild index.json; refuses to write while a file is broken
```

**The filename is the code.** There is no mapping table between the two, because there is no
mapping — `marks/venue/mono/kraken.svg` is the mark for the row whose `exchange.code` is
`kraken`. Nothing in the database records any of this; the convention *is* the record.

**Ask `index.json` before asking for a file.** A page that probes with `<img onerror>` shows
the reader a broken image first, and a broken image is exactly the dishonest failure this
whole thing is written to avoid. 90 of 177 collected assets have a mark; 87 do not.

**`mono` is masked, never `<img>`-ed.** Every fill in a mono file is `currentColor`, and an
`<img>` cannot inherit it — load one that way and it renders black, invisible on the night
theme. Use `mask-image` plus `background: currentColor`, or inline the file.
`src/web/ds-studio/components/market/IdentityMark.jsx` does both correctly.

191 files, 284,370 bytes.

## After you change anything here

```
python3 marks/tools/index.py          # rewrite index.json
python3 marks/tools/index.py --check  # verify only; non-zero exit if stale or broken
```

and add the file's record to `MANIFEST.json` by hand — where it came from, when, under what
terms, and what you changed. That record is the whole point of vendoring rather than hot-
linking. A file with no record is a liability in a year.
