/** Equity curve chart — the brand's gold→violet gradient stroke with soft violet fill. */
export interface EquityCurveProps {
  /** Raw values, evenly spaced; scaled to fit */
  points?: number[];
  /** ViewBox height in px (default 240) */
  height?: number;
  showFill?: boolean;
}
