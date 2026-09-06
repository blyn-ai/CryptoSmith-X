import type { ReactElement } from 'react';

/**
 * An hourly series as a line. One number per hour is not an OHLC bar, so it is never drawn as
 * a candle — the price series gets candles in its own panel, everything else gets this.
 */
export interface SparklineProps {
  /** One value per hour, oldest first. Fewer than two points renders an empty slot. */
  values: number[] | null;
  call?: 'ticker' | 'oi' | 'depth';
  /** True where the series ran over the window — takes the call's full-strength ink. */
  hot?: boolean;
  width?: number;
  height?: number;
  opacity?: number;
}

export function Sparkline(props: SparklineProps): ReactElement;
