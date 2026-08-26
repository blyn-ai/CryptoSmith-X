import React from 'react';
export function Checkbox({ checked, onChange, label, style }) {
  return (
    <label style={{ display: 'inline-flex', alignItems: 'center', gap: 9, cursor: 'pointer', ...style }} onClick={(e) => { e.preventDefault(); onChange && onChange(!checked); }}>
      <span aria-checked={!!checked} role="checkbox" style={{ width: 16, height: 16, borderRadius: 'var(--radius-xs)', border: '1px solid ' + (checked ? 'var(--violet-700)' : 'var(--border-input)'), background: checked ? 'var(--violet-700)' : 'var(--surface-sunken)', display: 'grid', placeItems: 'center', color: '#fff', font: '600 10px var(--font-mono)', transition: 'var(--transition-color)' }}>{checked ? '✓' : ''}</span>
      {label && <span style={{ font: '400 13.5px var(--font-body)', color: 'var(--text-body)' }}>{label}</span>}
    </label>
  );
}
