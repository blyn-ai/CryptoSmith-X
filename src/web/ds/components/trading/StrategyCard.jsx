import React from 'react';
import { Tag } from '../core/Tag.jsx';
export function StrategyCard({ name, status = 'running', ai, metrics = [], last, style }) {
  const statusTag = { running: ['gold', 'RUNNING'], paused: ['neutral', 'PAUSED'], stopped: ['down', 'STOPPED'] }[status] || ['neutral', status];
  return (
    <div style={{ padding: '16px 18px', borderBottom: last ? 0 : '1px solid var(--border-hairline)', ...style }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 9, flexWrap: 'wrap' }}>
        <b style={{ font: '500 14.5px var(--font-display)', color: 'var(--text-heading)' }}>{name}</b>
        <Tag tone={statusTag[0]}>{statusTag[1]}</Tag>
        {ai && <Tag tone="violet">AI WATCHLIST</Tag>}
      </div>
      {metrics.length > 0 && (
        <div style={{ display: 'flex', gap: 16, marginTop: 9, font: '400 11.5px var(--font-mono)', color: 'var(--text-muted)' }}>
          {metrics.map((m, i) => (
            <span key={i}>{m.label} <b style={{ color: m.tone === 'up' ? 'var(--pnl-up)' : m.tone === 'down' ? 'var(--pnl-down)' : 'var(--text-data)', fontWeight: 500 }}>{m.value}</b></span>
          ))}
        </div>
      )}
    </div>
  );
}
