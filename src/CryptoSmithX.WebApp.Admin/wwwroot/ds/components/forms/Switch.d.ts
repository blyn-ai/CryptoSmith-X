/** Toggle switch — violet when on; the only pill shape in the system. */
export interface SwitchProps {
  checked?: boolean;
  onChange?: (checked: boolean) => void;
  label?: React.ReactNode;
}
