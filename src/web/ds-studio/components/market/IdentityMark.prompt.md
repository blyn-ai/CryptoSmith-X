The venue's or the asset's own mark, beside the name that is always there.

```jsx
// The pairs list. Hoist the index once, then the table needs no per-asset work.
const marks = await fetch('/marks/index.json').then(r => r.json());

<IdentityMark code="kraken" href={markHref(marks, 'venue', 'kraken')} title="Kraken" />
<IdentityMark code="BTC"    href={markHref(marks, 'asset', 'BTC')} />

// No file for this one. Pass null and get the fallback — never a guessed URL.
<IdentityMark code="FARTCOIN" href={null} />

// The pair page header, where one mark stands alone. Same ink, bigger slot.
<IdentityMark code="BTC" href={markHref(marks, 'asset', 'BTC')} size={28} />
```

This is the one exception to **No icons**, and it is an exception, not a loophole: a mark
here is *identity*, the same fact the platform and symbol columns already state in type.
Nothing decorative gets a glyph — not a search box, not a state, not a verdict.

**Design for the missing mark first.** Eighty-three of 177 collected assets have a one-ink
file; 94 do not, and neither does WEEX. `href={null}` is the normal case. It renders the
first two characters of the code in the table's own mono caps at label tracking, in
`--text-unmeasured` — the ink the em dash wears. No ground, no border, no circle, no colour,
because a tile with a letter in it *is* a logo shape and a reader will learn it as one.

**One ink, and it is the name's ink.** The mark is drawn in `--text-data`, masked rather
than `<img>`-ed — an `<img>` cannot inherit `currentColor`, so a mono file loaded that way
is black, and black is invisible on the night card. `branded` still renders and no Studio view
may pass it: colour on this surface means which call wrote a figure, and 26 of the 98
full-colour files are under 1.6:1 on one of the two card grounds anyway. See RULE-CHANGES.md
item 6.

**Two sizes, and the small one is a floor.** `--slot-ident` (16) in a table row,
`--slot-ident-page` (28) in a page header. Ask for less than 16 and you get the monogram, not
a smaller mark — measured across the 93 one-ink files, 16px is the median size at which a
mark's median stroke is still one device pixel.

**It never fades.** Figures fade across their call's window and the age line never does; the
mark is on the age line's side, because identity is not evidence and does not age. Do not
wrap it in the row's opacity: at the 0.15 floor a present mark and an absent one look the
same, which is the dash-vs-zero lie drawn instead of printed.

**A mark earns its slot only where it varies down the column.** On a pair page every row is
the same asset, so the asset mark goes in the header once and never into the symbol column;
the quote side never gets one at all, because a quote here is a family we defined, and
families have no logo.
