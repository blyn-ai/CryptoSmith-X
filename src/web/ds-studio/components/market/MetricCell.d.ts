import type { ReactElement } from 'react';

/**
 * A single table cell: BEST slot, figure, hourly line or comparative bar, and the age of the
 * call that wrote it. Fades with its own age down to a 0.15 floor; the age line does not.
 * @startingPoint section="Market" subtitle="One column's cell: figure, history, age" viewport="700x140"
 */
export interface MetricCellProps {
  value?: number | null;
  decimals?: number;
  signed?: boolean;
  percent?: boolean;
  unit?: string;
  /** Depth cells pass both sides instead of a value — they render mirrored. */
  bid?: number | null;
  ask?: number | null;
  /** The column's maximum across the venues shown, for the bar. */
  max?: number;
  call?: 'ticker' | 'oi' | 'depth';
  /** Hourly aggregates, where the backend keeps them. */
  series?: number[] | null;
  /** The series ran over the window — full-strength ink. */
  hot?: boolean;
  /** This venue wins the column. */
  best?: boolean;
  /** A categorical band on the figure: 'tight' | 'wide'. */
  band?: 'tight' | 'wide';
  /** Seconds since the call landed. Drives the fade. */
  age: number | null;
  windowSeconds?: number;
  /** Vertical group wash: 'oi' | 'depth'. */
  tint?: 'oi' | 'depth';
}

export function MetricCell(props: MetricCellProps): ReactElement;
