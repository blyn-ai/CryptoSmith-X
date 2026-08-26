/** Text input on sunken ink — mono eyebrow label, violet focus border. */
export interface InputProps {
  label?: React.ReactNode;
  /** Small mono helper line under the field */
  hint?: React.ReactNode;
  /** Mono value font — API keys, amounts, addresses */
  mono?: boolean;
  type?: string;
  value?: string;
  placeholder?: string;
  onChange?: (e: any) => void;
}
