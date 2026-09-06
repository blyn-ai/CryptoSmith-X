/** Modal dialog on raised ink — confirmations, destructive actions. */
export interface DialogProps {
  open?: boolean;
  title: React.ReactNode;
  children?: React.ReactNode;
  confirmLabel?: string;
  cancelLabel?: string;
  onConfirm?: () => void;
  onCancel?: () => void;
  /** Red confirm button for destructive actions */
  danger?: boolean;
  width?: number;
}
