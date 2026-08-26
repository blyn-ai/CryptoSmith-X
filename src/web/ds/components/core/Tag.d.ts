/** Status/label chip in mono caps — RUNNING, PAUSED, AI WATCHLIST, LIVE. */
export interface TagProps {
  /** gold = running/live, violet = AI/inference, neutral = paused/off, up/down = market */
  tone?: 'violet' | 'gold' | 'neutral' | 'up' | 'down';
  children?: React.ReactNode;
}
