/** Strategy list row — name, status tag, optional AI tag, mono metric line. */
export interface StrategyCardProps {
  name: string;
  status?: 'running' | 'paused' | 'stopped';
  /** Show the violet AI WATCHLIST tag */
  ai?: boolean;
  metrics?: { label: string; value: string; tone?: 'up' | 'down' }[];
  /** Suppress bottom hairline on the final row */
  last?: boolean;
}
