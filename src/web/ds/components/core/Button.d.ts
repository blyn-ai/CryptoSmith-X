/**
 * Primary action control. Violet is the default action colour; gold is reserved
 * for the single most important brand action per view (e.g. "Go live").
 */
export interface ButtonProps {
  /** Visual style */
  variant?: 'primary' | 'ghost' | 'gold' | 'danger' | 'quiet';
  size?: 'sm' | 'md' | 'lg';
  disabled?: boolean;
  onClick?: () => void;
  children?: React.ReactNode;
}
