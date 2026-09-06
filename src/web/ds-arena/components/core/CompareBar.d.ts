import type { ReactElement } from 'react';

/**
 * One figure against the largest venue on screen, log-scaled. Use it in the columns that
 * keep no hourly history (sizes, turnover, depth 10 / 50bps) so the cell still says
 * something about scale instead of sitting empty.
 */
export interface CompareBarProps {
  value: number | null;
  /** The largest value in this column across the venues shown. */
  max: number;
  /** Which call wrote the figure — sets the bar's hue. */
  call?: 'ticker' | 'oi';
  width?: string;
  /** Freshness fade, same clock as the figure. */
  opacity?: number;
}

export function CompareBar(props: CompareBarProps): ReactElement;
