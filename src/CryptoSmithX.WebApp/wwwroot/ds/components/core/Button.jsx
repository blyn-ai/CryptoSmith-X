import React from 'react';
const S = {
  base: { display: 'inline-flex', alignItems: 'center', justifyContent: 'center', gap: 8, font: 'var(--type-button)', borderRadius: 'var(--radius-sm)', border: '1px solid transparent', cursor: 'pointer', transition: 'var(--transition-color)', textDecoration: 'none' },
  size: { md: { padding: '11px 22px' }, sm: { padding: '7px 14px', fontSize: 12 }, lg: { padding: '13px 26px', fontSize: 14 } },
  variant: {
    primary: { background: 'var(--action-primary)', color: 'var(--text-on-action)' },
    ghost: { background: 'none', borderColor: 'var(--border-strong)', color: 'var(--lilac-200)' },
    gold: { background: 'var(--gold-400)', color: 'var(--text-on-gold)' },
    danger: { background: 'var(--tint-down)', color: 'var(--down-300)', borderColor: 'rgba(239,93,111,.35)' },
    quiet: { background: 'var(--tint-violet)', color: 'var(--violet-200)' },
  },
};
export function Button({ variant = 'primary', size = 'md', disabled, style, children, ...rest }) {
  const [hover, setHover] = React.useState(false);
  const hoverStyle = hover && !disabled ? {
    primary: { background: 'var(--action-primary-hover)' },
    ghost: { borderColor: 'var(--violet-400)', color: 'var(--lilac-100)' },
    gold: { background: 'var(--gold-300)' },
    danger: { background: 'rgba(239,93,111,.2)' },
    quiet: { background: 'rgba(161,138,255,.2)' },
  }[variant] : null;
  return (
    <button {...rest} disabled={disabled}
      onMouseEnter={() => setHover(true)} onMouseLeave={() => setHover(false)}
      style={{ ...S.base, ...S.size[size], ...S.variant[variant], ...hoverStyle, ...(disabled ? { opacity: .45, cursor: 'not-allowed' } : null), ...style }}>
      {children}
    </button>
  );
}
