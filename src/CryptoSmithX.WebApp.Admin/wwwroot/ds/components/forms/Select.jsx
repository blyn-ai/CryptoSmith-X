import React from 'react';
export function Select({ label, options = [], style, selectStyle, ...rest }) {
  return (
    <label style={{ display: 'block', ...style }}>
      {label && <span style={{ display: 'block', font: 'var(--type-eyebrow)', letterSpacing: 'var(--track-eyebrow)', textTransform: 'uppercase', color: 'var(--text-muted)', marginBottom: 7 }}>{label}</span>}
      <span style={{ position: 'relative', display: 'block' }}>
        <select {...rest} style={{ width: '100%', height: 'var(--control-h)', padding: '0 34px 0 14px', background: 'var(--surface-sunken)', border: '1px solid var(--border-input)', borderRadius: 'var(--radius-sm)', color: 'var(--text-heading)', font: '400 14px var(--font-body)', outline: 'none', appearance: 'none', WebkitAppearance: 'none', cursor: 'pointer', ...selectStyle }}>
          {options.map((o) => (typeof o === 'string' ? <option key={o} value={o}>{o}</option> : <option key={o.value} value={o.value}>{o.label}</option>))}
        </select>
        <span style={{ position: 'absolute', right: 13, top: '50%', transform: 'translateY(-50%)', color: 'var(--text-muted)', fontSize: 10, pointerEvents: 'none' }}>▾</span>
      </span>
    </label>
  );
}
