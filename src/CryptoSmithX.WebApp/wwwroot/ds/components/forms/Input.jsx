import React from 'react';
export function Input({ label, hint, mono, style, inputStyle, ...rest }) {
  const [focus, setFocus] = React.useState(false);
  return (
    <label style={{ display: 'block', ...style }}>
      {label && <span style={{ display: 'block', font: 'var(--type-eyebrow)', letterSpacing: 'var(--track-eyebrow)', textTransform: 'uppercase', color: 'var(--text-muted)', marginBottom: 7 }}>{label}</span>}
      <input {...rest} onFocus={(e) => { setFocus(true); rest.onFocus && rest.onFocus(e); }} onBlur={(e) => { setFocus(false); rest.onBlur && rest.onBlur(e); }}
        style={{ width: '100%', height: 'var(--control-h)', padding: '0 14px', background: 'var(--surface-sunken)', border: `1px solid ${focus ? 'var(--violet-400)' : 'var(--border-input)'}`, borderRadius: 'var(--radius-sm)', color: 'var(--text-heading)', font: mono ? '400 13px var(--font-mono)' : '400 14px var(--font-body)', outline: 'none', transition: 'var(--transition-color)', ...inputStyle }} />
      {hint && <span style={{ display: 'block', font: '400 11px var(--font-mono)', color: 'var(--text-faint)', marginTop: 6 }}>{hint}</span>}
    </label>
  );
}
