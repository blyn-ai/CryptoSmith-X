/** Compact mono tab row for ranges and filters (1D/1W/1M/ALL). */
export interface TabsProps {
  items: (string | { value: string; label: string })[];
  value?: string;
  onChange?: (value: string) => void;
}
