import React from 'react';
export function Card({ title, actions, pad = true, style, children }) {
  return (
    <section style={{ background: 'var(--surface-card)', border: '1px solid var(--border-hairline)', borderRadius: 'var(--radius-md)', ...style }}>
      {(title || actions) && (
        <div style={{ display: 'flex', alignItems: 'center', padding: '16px 20px', borderBottom: '1px solid var(--border-hairline)' }}>
          {title && <h2 style={{ margin: 0, font: 'var(--type-card-title)', fontSize: 16, color: 'var(--text-heading)' }}>{title}</h2>}
          {actions && <div style={{ marginLeft: 'auto', display: 'flex', gap: 8 }}>{actions}</div>}
        </div>
      )}
      <div style={pad ? { padding: '16px 20px' } : null}>{children}</div>
    </section>
  );
}
