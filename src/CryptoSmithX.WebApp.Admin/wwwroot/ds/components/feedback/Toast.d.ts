/** Toast notification — dot-coded tone, no icons. */
export interface ToastProps {
  tone?: 'info' | 'success' | 'error' | 'warn';
  title?: React.ReactNode;
  children?: React.ReactNode;
}
