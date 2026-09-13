#!/usr/bin/env python3
"""Count the dash cells per column for chosen venues on a rendered /studio/v2/<ASSET> page.

Acceptance for plans/prompt-dex-five-venues.md: every new venue's row has all fifteen columns
filled, and this is how that is checked — by reading the page the server rendered, not by eye.

    ssh csx-prod 'curl -s http://10.8.0.1:7778/studio/v2/BTC?probe=$RANDOM' | ops/v2_dashes.py dYdX

A cell counts as a dash when its PRIMARY figure — or either half of a paired figure (Bid / Ask,
Bid / Ask size) — reads "—", or when the cell has no figure at all. The age line under a cell and the
figure's own sub-line are not the column's value and are not read: an age of "—" means the page has
not ticked yet, and a sub-line is a second fact under the first.

Exit status is the number of dash cells found across the chosen venues, capped at 255, so a shell can
fail on it.
"""
import html
import re
import sys

# Column name as the header reads it -> the cell's data-label, matched EXACTLY: "Bid / Ask" is a prefix
# of "Bid / Ask size", and the open-interest cell is labelled "Open" with "interest" on the second line.
COLUMNS = {"Bid / Ask": "Bid / Ask", "Spread": "Spread", "Last": "Last", "Mark": "Mark", "Index": "Index",
           "Funding": "Funding", "Venue rate": "Venue rate", "Interval": "Interval", "Turnover": "Turnover",
           "Liquidations": "Liquidations", "Last trade": "Last trade", "Open interest": "Open",
           "Bid / Ask size": "Bid / Ask size", "Depth": "Depth", "Book reach": "Book reach"}


def text(fragment: str) -> str:
    return " ".join(html.unescape(re.sub(r"<[^>]+>", " ", fragment)).split())


def main() -> int:
    venues = [v.lower() for v in sys.argv[1:]]
    page = sys.stdin.read()
    rows = re.split(r'<div class="v2-row"', page)[1:]
    if not rows:
        print("no v2 rows on the page — not a rendered v2 grid", file=sys.stderr)
        return 255

    total = 0
    matched = 0
    for row in rows:
        plat = re.search(r'class="v2-plat">(.*?)</span>', row, re.S)
        name = text(plat.group(1)) if plat else "?"
        if venues and not any(v in name.lower() for v in venues):
            continue
        matched += 1

        cells = {}
        for m in re.finditer(r'<span class="v2-cell"[^>]*data-label="([^"]*)"[^>]*>(.*?)<span class="v2-cellage', row, re.S):
            label = " ".join(html.unescape(m.group(1)).split())
            body = re.sub(r'<span class="v2-figsub.*?</span>', " ", m.group(2), flags=re.S)
            parts = re.findall(r'<span class="v2-part[^"]*"[^>]*>(.*?)</span>\s*</span>', body, re.S)
            figs = [text(p) for p in parts] if parts else [text(body)]
            cells[label] = figs

        inst = re.search(r'data-instrument="(\d+)"', '<div class="v2-row"' + row)
        print(f"\n{name}  (instrument {inst.group(1) if inst else '?'})")
        dashes = 0
        for col, label in COLUMNS.items():
            key = label if label in cells else None
            if key is None:
                verdict, shown = "MISSING", ""
                dashes += 1
            else:
                figs = cells[key]
                empty = (not any(f.strip() for f in figs)) or any(
                    f.strip() == "—" or f.strip().endswith(" —") or f.strip().startswith("— ") for f in figs)
                verdict = "—" if empty else "ok"
                shown = " / ".join(f[:22] for f in figs)
                dashes += 1 if empty else 0
            print(f"  {col:15} {verdict:7} {shown}")
        print(f"  dash cells: {dashes}")
        total += dashes

    if matched == 0:
        print(f"no row matched {sys.argv[1:]}", file=sys.stderr)
        return 255

    print(f"\nTOTAL dash cells across {matched} matched rows: {total}")
    return min(total, 255)


if __name__ == "__main__":
    sys.exit(main())
