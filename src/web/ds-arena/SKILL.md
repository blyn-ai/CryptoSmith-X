---
name: csx-arena-design
description: Use this skill to generate well-branded interfaces and assets for the CryptoSmith X market surface (CSX Arena), either for production or throwaway prototypes/mocks/etc. Contains essential design guidelines, colors, type, fonts, assets, and UI kit components for prototyping.
user-invocable: true
---

Read the `readme.md` file within this skill, and explore the other available files.
`styles.css` is the single stylesheet to link (it `@import`s `tokens/` and `fonts/`).
Components live in `components/core/` and `components/market/` as plain React JSX
(`export function Name(props)`) with a sibling `.d.ts` and `.prompt.md`; there is no build
step and no npm dependency beyond React. `ui_kits/pairs-monitor/index.html` is a working
recreation of the product view — open it in a browser to see the whole system assembled.
If creating visual artifacts (slides, mocks, throwaway prototypes, etc), copy assets out and create static HTML files for the user to view. If working on production code, you can copy assets and read the rules here to become an expert in designing with this brand.
If the user invokes this skill without any other guidance, ask them what they want to build or design, ask some questions, and act as an expert designer who outputs HTML artifacts _or_ production code, depending on the need.

Two rules of this brand are load-bearing and easy to get wrong: every figure carries the age
of the call that wrote it (not the row's age), and colour means which call, never profit and
loss. If a design drops the ages, it is not this brand.
