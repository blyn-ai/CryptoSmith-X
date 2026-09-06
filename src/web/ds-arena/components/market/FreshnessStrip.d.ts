import type { ReactElement } from 'react';

export interface FreshnessCall {
  /** "Price" / "Depth" / "OI" — the call, not the field. */
  label: string;
  /** Seconds since it landed; null for a call this venue does not serve. */
  seconds: number | null;
}

/**
 * The whole row's freshness at a glance, for the venue cell. The scale is the same 0-to-spent
 * scale the figures fade on, so the strip and the fades are read the same way.
 * @startingPoint section="Market" subtitle="Row freshness: scale, ticks, freshest and oldest" viewport="700x140"
 */
export interface FreshnessStripProps {
  calls: FreshnessCall[];
  windowSeconds?: number;
}

export function FreshnessStrip(props: FreshnessStripProps): ReactElement;
