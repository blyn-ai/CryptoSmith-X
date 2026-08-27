# Icons

There are two. That is not an accident and it is not a starting point.

## The rule

`src/web/ds/readme.md` states it plainly: **there is no icon set.** Meaning is
carried by colour-coded dots, unicode arrows (`↑ ↓ ▾ →`) and `●` in mono,
mono-caps tags, and the brand marks. No icon font, no ad-hoc SVG icons, no emoji.

The two files here are a **sanctioned exception**, granted for the theme toggle
after two alternatives were built and rejected in review:

1. Text labels (`INK` / `PAPER`) — what the design system and the theme brief
   both specified. Rejected: read as jargon rather than as a control.
2. Unicode glyphs (`U+263E` / `U+2600`) — stays inside the charter, since the DS
   already allows unicode marks in mono. Rejected for a concrete reason: IBM Plex
   Mono has neither codepoint, so both fell back to a system face and rendered as
   a hairline outline that was invisible at 12 px.

SVG was the third attempt and the one that worked. It is not a precedent. A third
icon needs the same argument made again, in the open, and a note added here.

## Files

| File | Used by | Notes |
|---|---|---|
| `theme-moon.svg` | theme toggle, dark segment | filled crescent; outlines vanish at this size |
| `theme-sun.svg` | theme toggle, light segment | filled disc + 8 stroked rays |

## Conventions

- `viewBox="0 0 16 16"`, drawn on a 16 px grid, rendered at 15 px.
- **`currentColor` only.** No hard-coded fill. The toggle sets `color` from
  `--text-muted` / `--violet-200`, so the icon follows the theme with no
  per-theme override — this is what keeps them out of `theme-light.css`.
- Solid shapes. A 1 px outline is a hairline at 15 px and reads as a rendering
  artefact, which is exactly how the unicode attempt failed.

## Known duplication

These are the masters. The app **inlines** the same paths in
`src/CryptoSmithX.WebApp/Views/Shared/_ThemeToggle.cshtml`, because an
`<img src>` would break `currentColor` and a CSS `mask` would cost two requests
to save one copy-paste.

So the path data exists in two places. Change one, change the other. If a third
icon ever lands, that trade stops being worth it — link this folder into
`wwwroot` the way the csproj already links `src/web/ds`, and switch to masks.
