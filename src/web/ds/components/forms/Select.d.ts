/** Native select restyled on sunken ink with mono ▾ indicator. */
export interface SelectProps {
  label?: React.ReactNode;
  options: (string | { value: string; label: string })[];
  value?: string;
  onChange?: (e: any) => void;
}
