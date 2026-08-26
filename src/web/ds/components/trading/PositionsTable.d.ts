/** Open-positions table — mono data, right-aligned numerals, PnL coloured by sign. */
export interface PositionsTableProps {
  rows: { market: string; venue: string; side: 'long' | 'short'; size: string; entry: string; mark: string; upnl: string }[];
}
