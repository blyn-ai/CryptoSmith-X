/** Panel surface — ink card with violet hairline, optional titled header. */
export interface CardProps {
  title?: React.ReactNode;
  /** Right-aligned header slot (Tabs, Button) */
  actions?: React.ReactNode;
  /** false for flush content like tables */
  pad?: boolean;
  children?: React.ReactNode;
}
