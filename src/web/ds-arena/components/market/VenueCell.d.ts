import type { ReactElement } from 'react';
import type { FreshnessCall } from './FreshnessStrip';

/**
 * The sticky venue column: platform, instrument type, and the row's freshness strip. Keep it
 * and the symbol column frozen when the table scrolls — a figure with no venue attached is
 * not information.
 */
export interface VenueCellProps {
  platform: string;
  kind?: 'spot' | 'perp';
  calls?: FreshnessCall[];
  windowSeconds?: number;
  /** The absolute snapshot clocks, for hover: "Price 12:03:39Z · OI 12:03:30Z · Depth …". */
  title?: string;
}

export function VenueCell(props: VenueCellProps): ReactElement;
