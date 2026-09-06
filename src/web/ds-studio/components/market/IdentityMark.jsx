import React from 'react';
import { TYPE } from '../core/Num.jsx';

/* IDENTITY, NOT DECORATION — and therefore the one exception to "No icons."
   ------------------------------------------------------------------------
   The readme bans icons: no icon font, no SVG icon set, no emoji. That rule is about
   DECORATION — a magnifying glass beside a search box, a lightning bolt beside the word
   fast. A venue's own mark and a token's own mark are not decoration. They answer the same
   question the platform column and the symbol column already answer in type, only faster.
   Nothing here carries meaning the row does not already state in words, which is the test.
   The exception is written up as items 5-9 of RULE-CHANGES.md; it licenses marks in the
   identity columns and once in a pair page's header, and nowhere else.

   THE FALLBACK IS THE MAJORITY CASE, NOT THE EDGE CASE.
   Eighty-three of 177 collected assets have a one-ink file and 94 do not; of the four real
   venues in the database, three do and WEEX does not. So the mark is an enhancement layered
   onto a name that is always there, never a replacement for it, and the no-mark case is the
   one designed first.

   What it shows: the first two characters of the code, set in the table's own mono caps at
   label tracking, in --text-unmeasured — the ink the em dash wears. No ground, no border,
   no circle, no colour.

   Why not a tile or a disc with a letter in it, which is what everyone reaches for: it
   works. That is the problem. A grey circle with a K reads as Kraken's actual mark, and a
   reader who has seen it four times has learned a brand we invented. That is the visual
   equivalent of printing 0 where nothing was measured. Two spaced characters of DM Mono can
   never be mistaken for a logo, because no logo on earth looks like the table it sits in.

   Why not simply nothing, which is the honest minimum: MetricCell already settled this —
   "the slot is reserved whether or not the mark is there, so figures sit on one line across
   the row." A hole where a mark belongs reads as a broken image, which is the other thing
   the house rule forbids. Reserve the slot; put type in it.

   ONE INK, AND IT IS THE NAME'S INK. The mark is drawn in --text-data, the colour the name
   beside it wears. Never the brand's own colours: on this surface colour means which call
   wrote a figure (readme rules 4 and 6), and a brand's hue is somebody else's decision about
   nothing we measured. The measurement agreed before taste did — 26 of the 98 full-colour
   files are under 1.6:1 against one of the two card grounds. `variant="branded"` still
   works, because the files are still on disk, but nothing on the Arena surface may pass it:
   see RULE-CHANGES.md item 6.

   IT DOES NOT FADE. Figures fade across their call's window; the age line never does
   (rule 3), and the mark is on that side. Identity is not evidence and does not age — and
   at the 0.15 fade floor a mark would be indistinguishable from a slot that has none, which
   turns "old call" into "no mark" the way a 0 turns "not measured" into "measured zero".
   So this component takes no opacity, and a caller must not wrap it in one.

   TWO SIZES, AND A FLOOR THAT IS ENFORCED HERE. --slot-ident (16) in a table row,
   --slot-ident-page (28) in a page header. Below 16 no artwork is drawn at all: measured
   across the 93 one-ink files, 16px is the median size at which a mark's median stroke is
   still one device pixel, so under it the set is a smear. Ask for less and you get the
   monogram — that is not a caller's mistake to make silently.

   DELIVERY. mono is masked, not <img>-ed: an <img> cannot inherit currentColor, so a mono
   mark in an <img> resolves to black and disappears on the night theme. branded is a plain
   <img>. Both are files under marks/ named after the code itself. */

const FALLBACK_INK = 'var(--text-unmeasured)';
/** Below this, artwork is mud and the monogram is the honest thing. See RULE-CHANGES.md 8. */
export const MARK_MIN_PX = 16;

/**
 * A venue's or an asset's own mark, with the typographic fallback when there is none.
 * `href` is the resolved URL of the file, or null when marks/index.json says we have none —
 * this component never probes for a file and therefore never flashes a broken image.
 */
export function IdentityMark({
  code, href = null, variant = 'mono', size = 16, title, tone = 'var(--text-data)'
}) {
  const label = String(code || '').slice(0, 2).toUpperCase();
  const box = {
    display: 'inline-flex', alignItems: 'center', justifyContent: 'center',
    width: size, height: size, flex: '0 0 auto'
  };

  if (!href || size < MARK_MIN_PX) {
    return (
      <span
        title={title || `${code} · no mark on file`}
        aria-hidden="true"
        style={{ ...box, ...TYPE.label,
                 fontSize: size >= 24 ? 'var(--fs-ui)' : 'var(--fs-eyebrow)',
                 letterSpacing: 'var(--track-label)', textIndent: 'var(--track-label)',
                 color: FALLBACK_INK }}
      >{label}</span>
    );
  }

  if (variant === 'mono') {
    // The file is a silhouette; the page supplies the ink. Mask, so it is never black on
    // black. -webkit- first for Safari, which still wants the prefix.
    const mask = {
      WebkitMaskImage: `url("${href}")`, maskImage: `url("${href}")`,
      WebkitMaskRepeat: 'no-repeat', maskRepeat: 'no-repeat',
      WebkitMaskPosition: 'center', maskPosition: 'center',
      WebkitMaskSize: 'contain', maskSize: 'contain',
      backgroundColor: 'currentColor'
    };
    return <span title={title} aria-hidden="true" style={{ ...box, color: tone, ...mask }} />;
  }

  return <img src={href} alt="" title={title} width={size} height={size}
              style={{ ...box, objectFit: 'contain' }} />;
}

/**
 * Where a mark lives, given the code. There is no lookup table because there is no mapping:
 * the filename IS the code, verbatim — `exchange.code` for a venue, the canonical
 * `asset.code` for an asset. `index` is marks/index.json; consulting it is what turns
 * "we have no mark" into a decision made before render instead of an error after it.
 */
export function markHref(index, kind, code, variant = 'mono', base = '/marks') {
  const have = index && index[kind] && index[kind][variant];
  if (!have || !code) return null;
  const present = typeof have.has === 'function' ? have.has(code) : have.indexOf(code) !== -1;
  return present ? `${base}/${kind}/${variant}/${code}.svg` : null;
}
