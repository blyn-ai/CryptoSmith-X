import React from 'react';
export function VenueStatus({ venues = [], style }) {
  return (
    <div style={{ display: 'flex', gap: 12, ...style }}>
      {venues.map((v) => (
        <span key={v.name} style={{ flex: 1, display: 'flex', alignItems: 'center', gap: 8, font: '500 11.5px var(--font-mono)', color: 'var(--text-body)' }}>
          <s style={{ width: 6, height: 6, borderRadius: '50%', background: v.ok === false ? 'var(--status-off)' : 'var(--status-ok)', textDecoration: 'none' }}></s>
          {String(v.name).toUpperCase()}
          <em style={{ fontStyle: 'normal', color: 'var(--text-muted)', marginLeft: 'auto' }}>{v.latency || '—'}</em>
        </span>
      ))}
    </div>
  );
}
