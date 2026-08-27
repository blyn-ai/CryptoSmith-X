# Brand

Design material that the exported design system does not carry: product icons,
social cards, and the house rules that were decided while building the product.

Nothing here is compiled or deployed. It is source and specification — the place
you read before changing how something looks, and the place a designer's handoff
lands so it stops living in someone's Downloads folder.

## The boundary — read this first

There are two design surfaces in this repository and they are not the same thing:

| | `src/web/ds/` | `brand/` (here) |
|---|---|---|
| What | The **exported** design system: tokens, fonts, components, specimen cards | Everything the export does not cover |
| Authority | Canonical for colour, type, spacing, tone, casing | Canonical for product rules layered on top |
| Editing | **Do not edit.** It is generated; a hand edit is lost on the next export | Edited by hand, reviewed like code |
| Shipping | Linked into `wwwroot/ds` at build time | Not shipped |

If a question is answered by `src/web/ds/readme.md` — which colour, which face,
sentence case or caps, how a number is written — that file wins and nothing here
should repeat it. This folder exists for the questions it leaves open.

## Contents

- **[icons/](icons/)** — the product's icons. There are two, and the rule that
  keeps it that way.
- **[social/](social/)** — Open Graph and social share cards. Sizes, safe area,
  and what a card is allowed to claim.
- **[rules/](rules/)** — decisions that outlive the commit that caused them:
  - [theme-ink-paper.md](rules/theme-ink-paper.md) — the dark/light theme brief
  - [severity-and-colour.md](rules/severity-and-colour.md) — what gold means, why
    green and red are market-only, and the one-animation rule

## Adding to this folder

A new file here should answer a question that came up twice. One-off decisions
belong in a code comment next to the code; this is for the ones that will be
re-litigated by whoever touches the CSS next year.
