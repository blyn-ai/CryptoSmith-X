import type { ReactElement } from 'react';

/**
 * The shell around one venue's candle chart. It owns the header and the sized container; the
 * host owns the chart instance, because the instances have to be tied to each other.
 * @startingPoint section="Market" subtitle="A venue's candle panel with its price range" viewport="700x300"
 */
export interface CandlePanelProps {
  platform: string;
  symbol: string;
  /** e.g. "6.3181 – 6.4303 · shared scale" — state the scale, it is shared across panels. */
  range: string;
  /** Receives the sized container; return a cleanup function. Create the chart here. */
  onMount?: (el: HTMLDivElement) => (() => void) | void;
  height?: string;
}

export function CandlePanel(props: CandlePanelProps): ReactElement;
