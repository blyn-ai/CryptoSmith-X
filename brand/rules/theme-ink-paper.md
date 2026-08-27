<!-- Verbatim copy of the designer's handoff (`Static login window design.zip`,
     exports/light-theme/LIGHT-THEME.md), kept in the repository so the reasoning
     survives the zip file. It is a brief, i.e. the state of intent at handoff —
     where the implementation departed from it, the departure is recorded at the
     bottom under "Implementation notes", not by editing the brief. -->

# Light theme ("Paper") — implementation brief for Claude Code

Repo: `blyn-ai/CryptoSmith-X` · branch `main` · path `src/CryptoSmithX.WebApp`
Reference mockup: `Admin Shell.dc.html` in the design project (toggle it with the ribbon's **Theme: Ink / Paper**).

Goal: one user-flippable theme, persisted, applied to **every** page (shell pages and the sign-in page), with **no flash** of the wrong theme on load or on the 10 s refresh.

---

## 1. Non-negotiables (read before writing CSS)

1. **Light is a remap, not a second stylesheet.** The whole app already paints from `var(--*)`. The light theme is one block of token overrides. If you find yourself adding a light-specific rule to `app.css` for anything other than the few call sites in §6, you're doing it wrong — the fix belongs in the token block.
2. **No new hex outside `theme-light.css`.** `app.css` stays token-only. The handful of hardcoded `rgba()` values already in `app.css` (`.side-badge.long/.short`, `.alert-error/.alert-ok`) must be tokenised first — see §6.
3. **Do not edit `wwwroot/ds/*`.** That is the exported design system. The override file loads after it and wins on cascade order.
4. **Dark is the default.** Unknown/absent cookie ⇒ dark. The design system is dark-only by charter; Paper is an accommodation, not the new house style.
5. **Gold on paper is bronze.** `#F5B84F` as *text* on white is 2.4:1 — illegible. Gold keeps its brightness as a **fill** (LIVE dot, active-nav bar, mark, badge backgrounds) and darkens to bronze as **text**. Two different tokens, see §5.2. Don't "fix" it by darkening `--gold-400` globally — that kills the dot and the mark.
6. **The severity ramp stays three steps.** ok (quiet lilac dot) → degraded (hollow ring + bronze text) → failing (solid gold dot + darker bronze text + gold row tint). Both gold rungs must be distinguishable from each other *and* legible. Green/red stay market-only in both themes.
7. **Ink glows, paper doesn't.** Every `box-shadow` glow (`--shadow-live`, `--shadow-mark`) becomes a flat halo ring on paper. Blur-glow on white just looks like a rendering bug.
8. **Contrast floor: 4.5:1 for anything that is text**, including 9–11 px mono meta, eyebrows and table cells. Decorative dots, rules, borders and chart strokes are exempt (but keep strokes ≥ 3:1 or the sparkline disappears).
9. **Motion rule survives.** Still exactly one animation in the product (`csxFresh`). On paper `brightness(2.1)` washes the dot out, so the light theme swaps the keyframe — see §5.3.
10. **Two states only.** Ink and Paper. No `prefers-color-scheme` auto mode unless it's asked for later; an implicit third state doubles the QA surface.

---

## 2. Files to touch

| File | Change |
|---|---|
| `wwwroot/theme-light.css` | **new** — the token override block (§5) |
| `Views/Shared/_Layout.cshtml` | read cookie → `data-theme`, link the new css, render the toggle in both branches |
| `Views/Shared/_ThemeToggle.cshtml` | **new** — the toggle partial (§4) |
| `wwwroot/app.css` | the call-site fixes in §6 + toggle styles |
| `wwwroot/site.js` (or inline at end of `_Layout`) | the click handler (§4.3) |

---

## 3. The switch: cookie, read server-side

`_Layout.cshtml` already renders `<html lang="en" data-theme="dark">`. Make that value dynamic. **Read the cookie on the server** — that is what removes the flash; a client-side script that patches `<html>` after first paint will strobe on every one of the 10 s refreshes.

```csharp
@{
    // ...existing isAuth / isAdmin / name / env...
    var theme = Context.Request.Cookies["csx_theme"] == "light" ? "light" : "dark";
}
```

```html
<html lang="en" data-theme="@theme">
```

Link the override **after** the design system and after `app.css`:

```html
<link rel="stylesheet" href="~/ds/styles.css" asp-append-version="true">
<link rel="stylesheet" href="~/app.css" asp-append-version="true">
<link rel="stylesheet" href="~/theme-light.css" asp-append-version="true">
```

Cookie contract:

- name `csx_theme`, value `dark` | `light`
- `path=/`, `max-age=31536000` (1 year), `samesite=lax`
- **not** HttpOnly and **not** Secure-only — it is written by JS and carries no secret
- written by the toggle only; the server never sets it (so a user with JS off simply stays dark)

`localStorage` is *not* the source of truth — the server can't read it, which is exactly the flash problem. If you want it as a belt-and-braces mirror, fine, but the cookie wins on load.

---

## 4. The toggle

Position: **top-right of the header**, last item in the right-hand cluster (after Sign out). On the sign-in page it floats top-right of `.auth-wrap`.

### 4.1 `Views/Shared/_ThemeToggle.cshtml`

```html
<button class="theme-toggle" type="button" data-theme-toggle
        aria-label="Switch between dark and light theme" title="Switch theme">
    <span class="t-ink">Ink</span><span class="t-paper">Paper</span>
</button>
```

Two labelled segments, not an icon — the product has **no icon set** (design-system rule), and a lone sun/moon glyph would be the first icon in the app. Mono caps, same idiom as `.chip` / `.env`.

Render it in **both** branches of `_Layout`:

```html
@if (isAuth)
{
    ...
    <header class="head">
        <span class="env">@env</span>
        @await RenderSectionAsync("live", required: false)
        <span class="who"><b>@name</b> · @(isAdmin ? "ADMIN" : "USER")</span>
        <form method="post" action="/auth/logout">
            @Html.AntiForgeryToken()
            <button class="linkbtn" type="submit">Sign out</button>
        </form>
        <partial name="_ThemeToggle" />
    </header>
    ...
}
else
{
    <div class="theme-float"><partial name="_ThemeToggle" /></div>
    @RenderBody()
}
```

### 4.2 Styles (append to `app.css`)

```css
/* ── theme toggle ─────────────────────────────────────────── */
.theme-toggle{display:flex;padding:0;background:0;cursor:pointer;font:inherit;
  border:1px solid var(--border-strong);border-radius:var(--radius-xs);overflow:hidden}
.theme-toggle span{padding:5px 9px;font:var(--fw-semibold) 9px/1 var(--font-mono);
  letter-spacing:var(--track-badge);text-transform:uppercase;color:var(--text-faint);
  transition:var(--transition-color)}
.theme-toggle .t-paper{border-left:1px solid var(--border-strong)}
[data-theme=dark] .theme-toggle .t-ink,
[data-theme=light] .theme-toggle .t-paper{background:var(--tint-violet);color:var(--violet-200)}
.theme-float{position:absolute;top:18px;right:22px;z-index:2}
```

`.auth-wrap` is already `position:relative`, so `.theme-float` anchors to it. `.head .who` keeps its `margin-left:auto`, so the toggle lands at the far right without further layout work.

### 4.3 Handler

```js
document.addEventListener('click', (e) => {
  if (!e.target.closest('[data-theme-toggle]')) return;
  const next = document.documentElement.dataset.theme === 'light' ? 'dark' : 'light';
  document.documentElement.dataset.theme = next;
  document.cookie = 'csx_theme=' + next + ';path=/;max-age=31536000;samesite=lax';
});
```

That's the whole mechanism. No transition on `<html>`, no class juggling — the tokens change and every page follows.

---

## 5. `wwwroot/theme-light.css`

### 5.1 The block

```css
/* CryptoSmith X — Paper theme.
   A REMAP of the same base scales, not a second palette: violet still leads,
   gold is still the signal, green/red are still market-only. Every rule in
   app.css is untouched — it already paints from these tokens.

   Two things do not survive the flip and are handled explicitly:
   1. Gold as TEXT (2.4:1 on white) — see --csx-gold-ink below.
   2. Glows — on paper they become flat halo rings. */

html[data-theme="light"]{
  /* violet axis — #A18AFF has no contrast on paper, so the accent moves to the
     700 rung. The logo rule already says the "X" is #6B4EDB on light surfaces. */
  --violet-100:#EFEAF7;--violet-200:#5B3FC4;--violet-300:#7C6BB8;--violet-400:#6B4EDB;
  --violet-500:#6353A8;--violet-600:#5B3FC4;--violet-700:#5B3FC4;--violet-800:#4A32A8;

  /* gold axis — 400 stays bright because it is a FILL (dot, nav bar, mark).
     200/300/700 are the text rungs and go bronze. */
  --gold-200:#7E4A08;--gold-300:#7E4A08;--gold-400:#D89A2B;--gold-600:#9A6A16;--gold-700:#7E4A08;

  /* ink → paper. Cards are pure white so the violet-tinted page reads as a field. */
  --ink-950:#EDE8F7;--ink-900:#F4F1FA;--ink-850:#FFFFFF;
  --ink-800:#FFFFFF;--ink-700:#ECE7F7;--ink-600:#DED6F0;

  /* text — violet-tinted near-blacks, never neutral gray */
  --lilac-100:#161020;--lilac-200:#2A2138;--lilac-300:#453C57;
  --lilac-500:#665D7D;--lilac-700:#7E7590;
  --sand-200:#6B5636;              /* the one warm neutral, for mono data */

  /* market — the dark greens/reds are too light on white */
  --up-300:#2E9A6C;--up-500:#1E9B6A;--up-700:#177A53;
  --down-300:#C23D4E;--down-500:#CE3B4E;--down-700:#A32B3C;

  --surface-overlay:rgba(22,16,32,.38);
  --surface-glass:rgba(107,78,219,.055);
  --text-on-gold:#221704;

  --border-hairline:rgba(107,78,219,.14);
  --border-card:rgba(107,78,219,.16);
  --border-strong:rgba(107,78,219,.30);
  --border-input:rgba(107,78,219,.26);
  --border-gold:rgba(138,90,15,.34);
  --border-up:rgba(30,155,106,.34);      /* new — see §6 */
  --border-down:rgba(206,59,78,.34);     /* new — see §6 */

  --tint-up:rgba(30,155,106,.10);
  --tint-down:rgba(206,59,78,.09);
  --tint-violet:rgba(107,78,219,.10);
  --tint-gold:rgba(216,154,43,.15);
  --tint-neutral:rgba(109,100,132,.12);

  /* paper casts a real shadow; ink barely did */
  --shadow-card:0 1px 2px rgba(24,16,48,.05),0 8px 24px rgba(24,16,48,.05);
  --shadow-modal:0 18px 48px rgba(24,16,48,.18);
  --shadow-live:0 0 0 3px rgba(216,154,43,.24);   /* halo, not glow */
  --shadow-mark:0 0 0 1px rgba(216,154,43,.30);

  /* the sign-in wash is calibrated for ink; on paper it must be barely there */
  --wash-gold:radial-gradient(820px 520px at 2% 0%,rgba(216,154,43,.10),transparent 66%);
  --wash-violet:radial-gradient(820px 620px at 100% 100%,rgba(107,78,219,.09),transparent 70%);
}

html[data-theme="light"] ::selection{background:rgba(107,78,219,.22);color:var(--lilac-100)}
```

### 5.2 The gold-as-text token (goes in `app.css`, next to the other globals)

```css
/* Gold has two jobs and only one survives on paper. As a fill (#D89A2B) it is
   correct; as text it is 2.4:1 on white. Text-role gold routes through its own
   token: fill-weight on ink, bronze on paper. Ramp stays three steps either way. */
:root{--csx-gold-ink:var(--gold-400)}
html[data-theme="light"]{--csx-gold-ink:#9A6A16}   /* 4.7:1 — degraded */
/* failing keeps --gold-300, which is #7E4A08 on paper — 7.4:1 */
```

### 5.3 Keyframe

```css
/* app.css — unchanged */
@keyframes csxFresh{0%{filter:brightness(2.1)}100%{filter:brightness(1)}}
/* theme-light.css — brightness washes a dot out on white, so saturate down instead */
@keyframes csxFreshLight{0%{filter:saturate(2.4) brightness(.78)}100%{filter:none}}
html[data-theme="light"] .dot.fresh{animation-name:csxFreshLight}
```

---

## 6. Call-site fixes in `app.css`

These are the only places where the token swap alone is not enough. All of them are edits to **existing** rules — no light-specific selectors.

**Gold used as text → `--csx-gold-ink`:**

```css
.nav-item .bdg{color:var(--csx-gold-ink)}      /* sidebar counts, 9px */
.sev.warn      {color:var(--csx-gold-ink)}     /* DEGRADED chip */
.num.warn      {color:var(--csx-gold-ink)}     /* degraded ages / fails / ms */
.spark.warn polyline{stroke:var(--csx-gold-ink)}
.dot.warn      {border-color:var(--csx-gold-ink)}  /* 1px hollow ring, not a fill */
```

Leave as `--gold-400` (they are fills, and must stay bright): `.dot.fail`, `.nav-item.is-active .bar`, the mark, and any gold badge *background*.

**Tokenise the four hardcoded rgba values** so the light block can override them:

```css
.side-badge.long {border:1px solid var(--border-up)}
.side-badge.short{border:1px solid var(--border-down)}
.alert-error     {border:1px solid var(--border-down)}
.alert-ok        {border:1px solid var(--border-up)}
```

and add the dark defaults next to the other tokens in `app.css`:

```css
:root{--border-up:rgba(65,201,143,.3);--border-down:rgba(239,93,111,.35)}
```

**Check, don't assume:** `.th{background:var(--surface-sunken)}`, `input[type=text]`, `select`, `textarea` and `.token-alert code` all use `--surface-sunken`, which on paper is `#EDE8F7`. That reads as a correctly recessed field against white cards — but eyeball it before you sign off.

---

## 7. Acceptance checklist

- [ ] Toggle sits top-right on every authenticated page and on the sign-in page.
- [ ] Reload, hard-reload and the 10 s meta refresh all render the chosen theme with **no flash**.
- [ ] Cookie survives browser restart; a fresh incognito window is dark.
- [ ] Sign-in page: wash is faint, not a smear; card and inputs read on paper.
- [ ] Severity ramp on the status dashboard is three visibly distinct steps in **both** themes, and DEGRADED text is readable at 10 px.
- [ ] Sidebar nav counts, `.num.warn` cells and the sparklines are visible on paper.
- [ ] Green/red appear **only** in PnL, side badges and order/alert status — grep for `.up`/`.down` leaking into ops tables.
- [ ] Exactly one animation still exists in the product.
- [ ] `git grep -nE '#[0-9A-Fa-f]{3,6}' -- wwwroot/app.css` returns nothing.
