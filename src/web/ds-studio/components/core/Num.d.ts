import type { ReactElement } from 'react';

export type NumTone = 'data' | 'muted' | 'faint' | 'ticker' | 'oi' | 'depth' | 'alarm';

/**
 * Every figure on the market surface. Mono, tabular, and dash-honest: null renders "—" in
 * --text-unmeasured, a measured zero renders "0" in --text-zero. Never frame a number.
 */
export interface NumProps {
  /** null / undefined render as "—" — not measured, and never a zero. */
  value: number | null | undefined;
  /** Fixed decimals. Prices 4, spread 1, sizes and notionals 0. */
  decimals?: number;
  /** Prefix a "+" on positives — funding only. */
  signed?: boolean;
  /** Append "%" — funding only. */
  percent?: boolean;
  /** Trailing unit in faint ink, e.g. "USD". */
  unit?: string;
  /** Which ink. Call tones tie a figure to the call that wrote it. */
  tone?: NumTone;
  size?: string;
  align?: 'left' | 'right';
  title?: string;
  /** Freshness fade, driven by the host's clock. 1 = the call just landed. */
  opacity?: number;
}

export function Num(props: NumProps): ReactElement;
export const TYPE: Record<string, Record<string, string | number>>;
