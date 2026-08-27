# Severity, colour and motion

Three rules that were decided while building the admin console and are easy to
break by accident. The short form lives as a comment at the top of
`src/CryptoSmithX.WebApp/wwwroot/app.css`; this is the reasoning behind it.

## 1. Gold owns operational severity. Green and red are market-only.

Green (`--up-*`) and red (`--down-*`) mean **money and direction** — PnL, long
vs short, order status. Nothing else. The moment a red row means "collector is
failing", red stops meaning "losing money", and a glance at the screen no longer
answers either question.

Operational severity is a three-step gold ramp:

| Step | Signal |
|---|---|
| ok | quiet lilac dot — **no gold at all** |
| degraded | hollow gold ring + bronze text |
| failing | solid gold dot + bronze text + gold row tint |

**The absence of gold is the ok signal.** That is what makes a healthy screen
readable at a glance: it is the one with no gold on it.

If you are adding `.down` to an operations table, stop — you want the failing
rung of the ramp.

## 2. Gold as text needs a different value from gold as fill

`#F5B84F` on white is 2.4:1. Illegible. But darkening `--gold-400` globally
would kill the LIVE dot, the active-nav bar and the mark, which are fills and
must stay bright.

So text-role gold routes through `--csx-gold-ink`: it resolves to `--gold-400`
on Ink (no visual change) and to bronze `#9A6A16` on Paper (4.7:1). The failing
rung uses `--gold-300`, which is `#7E4A08` on Paper (7.4:1) — so the two gold
rungs stay distinguishable from each other *and* legible.

Call sites are listed in [theme-ink-paper.md](theme-ink-paper.md) §6. A new one
uses the token; it never uses a raw gold.

## 3. There is exactly one animation in the product

`.dot.fresh` — a single 280 ms fade on load, on a status dot whose collector
succeeded inside the last interval. It never loops. Combined with the 10 s page
refresh it reads as a pulse at data cadence, and it needs no JavaScript.

Nothing else animates. No spinners, no skeleton shimmer, no hover transitions
beyond colour. A trading console that moves on its own trains you to ignore
movement, which is the one thing it needs you to notice.

The light theme swaps the keyframe (`csxFreshLight`) because `brightness(2.1)`
washes a dot out on white — same rule, different arithmetic. That is a
substitution, not a second animation.

## Contrast floor

4.5:1 for anything that is text, including 9–11 px mono meta, eyebrows and table
cells. Dots, rules, borders and chart strokes are exempt as decoration, but keep
strokes at 3:1 or better or the sparkline disappears on paper.
