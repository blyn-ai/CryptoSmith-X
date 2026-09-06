import type { ReactElement } from 'react';

/**
 * Names the three calls over their own columns. Order the table so each call's columns are
 * contiguous — turnover belongs beside the other ticker fields, not after open interest —
 * or the bands cannot be drawn.
 * @startingPoint section="Market" subtitle="The three-call header band over a column grid" viewport="700x120"
 */
export interface CallBandsProps {
  /** The grid-template-columns string the table body uses — they must match exactly. */
  template: string;
  venueSpan: number;
  tickerSpan: number;
  oiSpan: number;
  depthSpan: number;
  venueLabel?: string;
  tickerLabel?: string;
  oiLabel?: string;
  depthLabel?: string;
}

export function CallBands(props: CallBandsProps): ReactElement;
