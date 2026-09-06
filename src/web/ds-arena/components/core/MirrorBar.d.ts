import type { ReactElement } from 'react';

/**
 * The bid and ask sides of one depth band, mirrored around a centre. Depth is two numbers in
 * this product on purpose; a single figure hides a one-sided book.
 */
export interface MirrorBarProps {
  bid: number | null;
  ask: number | null;
  /** The largest single side in this band across the venues shown. */
  max: number;
  width?: string;
  opacity?: number;
}

export function MirrorBar(props: MirrorBarProps): ReactElement;
