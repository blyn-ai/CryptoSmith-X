import React from 'react';
export function Toast({ tone = 'info', title, children, style }) {
  const edge = { info: 'var(--violet-400)', success: 'var(--up-500)', error: 'var(--down-500)', warn: 'var(--gold-400)' }[tone];
  return (
    <div style={{ display: 'flex', gap: 12, alignItems: 'flex-start', width: 360, padding: '13px 16px', background: 'var(--surface-raised)', border: '1px solid var(--border-card)', borderRadius: 'var(--radius-md)', boxShadow: 'var(--shadow-card)', ...style }}>
      <s style={{ width: 7, height: 7, borderRadius: '50%', background: edge, marginTop: 5, flexShrink: 0, textDecoration: 'none' }}></s>
      <div>
        {title && <b style={{ display: 'block', font: '500 13.5px var(--font-display)', color: 'var(--text-heading)' }}>{title}</b>}
        {children && <span style={{ display: 'block', font: '400 12.5px var(--font-body)', color: 'var(--text-muted)', marginTop: 3 }}>{children}</span>}
      </div>
    </div>
  );
}
