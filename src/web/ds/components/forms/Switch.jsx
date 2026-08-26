import React from 'react';
export function Switch({ checked, onChange, label, style }) {
  return (
    <label style={{ display: 'inline-flex', alignItems: 'center', gap: 10, cursor: 'pointer', ...style }}>
      <button role="switch" aria-checked={!!checked} onClick={() => onChange && onChange(!checked)}
        style={{ width: 36, height: 20, padding: 2, border: '1px solid ' + (checked ? 'var(--violet-700)' : 'var(--border-input)'), borderRadius: 999, background: checked ? 'var(--violet-700)' : 'var(--surface-sunken)', cursor: 'pointer', transition: 'var(--transition-color)', display: 'flex', justifyContent: checked ? 'flex-end' : 'flex-start' }}>
        <span style={{ width: 14, height: 14, borderRadius: '50%', background: checked ? '#fff' : 'var(--lilac-500)', transition: 'background var(--dur-fast) var(--ease)' }}></span>
      </button>
      {label && <span style={{ font: '400 13.5px var(--font-body)', color: 'var(--text-body)' }}>{label}</span>}
    </label>
  );
}
