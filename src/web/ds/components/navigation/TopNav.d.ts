/** Console top bar — mark + wordmark, mono-caps nav with gold active underline, LIVE dot, account chip. */
export interface TopNavProps {
  items: string[];
  active?: string;
  onNavigate?: (item: string) => void;
  /** Account chip text, e.g. "d.bykovas" */
  user?: string;
  /** LIVE badge text; empty string hides it */
  live?: string;
  /** Path to cryptosmith-mark.svg relative to the consuming page */
  markSrc?: string;
}
