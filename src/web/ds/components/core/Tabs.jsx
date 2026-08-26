import React from 'react';
export function Tabs({ items = [], value, onChange, style }) {
  return (
    <div style={{ display: 'flex', gap: 4, font: '500 11px var(--font-mono)', ...style }}>
      {items.map((it) => {
        const key = typeof it === 'string' ? it : it.value;
        const label = typeof it === 'string' ? it : it.label;
        const on = key === value;
        return (
          <button key={key} onClick={() => onChange && onChange(key)}
            style={{ font: 'inherit', padding: '4px 9px', borderRadius: 'var(--radius-xs)', border: 0, cursor: 'pointer', background: on ? 'var(--tint-violet)' : 'none', color: on ? 'var(--lilac-200)' : 'var(--text-muted)', transition: 'var(--transition-color)' }}>
            {label}
          </button>
        );
      })}
    </div>
  );
}
