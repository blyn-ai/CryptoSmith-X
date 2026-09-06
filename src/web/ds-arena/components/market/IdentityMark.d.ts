import type { ReactElement } from 'react';

/**
 * A venue's or an asset's own mark — identity, not decoration, and the single exception to
 * the system's "No icons" rule (see IdentityMark.jsx and RULE-CHANGES.md item 5).
 *
 * The mark is an enhancement on top of a name that is always there. It is never the only
 * thing identifying a row, because for most rows there is no mark: 83 of 177 collected
 * assets have a one-ink file, and of the four real venues in the database one does not.
 */
export interface IdentityMarkProps {
  /** `exchange.code` for a venue, the canonical `asset.code` for an asset. */
  code: string;
  /**
   * The resolved URL of the file, or null when `marks/index.json` says there is none.
   * Null is the designed case, not the error case: it renders the typographic fallback.
   * Never pass a guessed URL — this component does not probe, precisely so a missing mark
   * can never surface as a broken image.
   */
  href?: string | null;
  /**
   * `mono` is masked and takes `tone` as its ink. It is not a default among options: one
   * ink is the rule on this surface (RULE-CHANGES.md item 6), because colour here means
   * which call wrote a figure and a brand's hue means nothing about our data — and because
   * 26 of the 98 full-colour files are under 1.6:1 against one of the two card grounds.
   * `branded` still renders (the files are on disk) but no Arena view may pass it.
   */
  variant?: 'mono' | 'branded';
  /**
   * Square edge in px, and there are two: `--slot-ident` (16) in a table row,
   * `--slot-ident-page` (28) in a page header. Anything under 16 renders the monogram
   * instead of the artwork — the component enforces it rather than trusting the caller.
   */
  size?: number;
  /** The ink for `mono`. Defaults to `var(--text-data)`, the ink of the name beside it. */
  tone?: string;
  title?: string;
}

/** The size below which artwork is a smear and the monogram takes the slot: 16. */
export const MARK_MIN_PX: 16;

export function IdentityMark(props: IdentityMarkProps): ReactElement;

/** The shape of `marks/index.json` — arrays of the codes that actually have a file. */
export interface MarkIndex {
  venue: { branded: string[] | Set<string>; mono: string[] | Set<string> };
  asset: { branded: string[] | Set<string>; mono: string[] | Set<string> };
}

/**
 * `marks/<kind>/<variant>/<code>.svg`, or null when the index does not list the code.
 * There is no name mapping: the filename is the code, verbatim. Hoist the index into Sets
 * once if you are rendering a long table.
 */
export function markHref(
  index: MarkIndex,
  kind: 'venue' | 'asset',
  code: string,
  variant?: 'mono' | 'branded',
  base?: string
): string | null;
