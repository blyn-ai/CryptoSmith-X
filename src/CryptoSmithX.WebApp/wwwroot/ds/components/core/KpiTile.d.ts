/** Stat tile — mono eyebrow, Grotesk value, optional mono delta line. */
export interface KpiTileProps {
  label: React.ReactNode;
  value: React.ReactNode;
  delta?: React.ReactNode;
  deltaTone?: 'up' | 'down' | 'gold' | 'muted';
}
