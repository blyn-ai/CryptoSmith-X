import type { ReactElement } from 'react';

/**
 * Feed state as a dot: filled = observed, hollow ring = no observation. The operational label
 * only — never the exchange's own instrument status, and never "degraded", which in this
 * product is a property of a whole venue's collector, not of a row.
 */
export interface StateDotProps {
  state?: 'observed' | 'stale' | 'none';
  size?: string;
  /** The word, for hover — colour is never the only signal. */
  title?: string;
}

export function StateDot(props: StateDotProps): ReactElement;
