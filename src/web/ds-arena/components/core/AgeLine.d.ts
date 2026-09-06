import type { ReactElement } from 'react';

/**
 * The live age of one call, under the figure it wrote. Whole seconds only — a tenths digit
 * changing ten times a second reads as motion, not as data. Bounded wording: "99+ s ago"
 * past a hundred, "degraded" past twelve windows, because by then the exact count has
 * stopped meaning anything.
 * @startingPoint section="Market" subtitle="Live age under a figure, with the spent mark" viewport="700x120"
 */
export interface AgeLineProps {
  /** Seconds since the call landed. null renders "—". */
  seconds: number | null;
  /** The window the fade and the △ are measured against. Default 30. */
  windowSeconds?: number;
  /** Force the dash — the field is not in the API for this venue. */
  missing?: boolean;
}

export function AgeLine(props: AgeLineProps): ReactElement;
