import React from 'react';
export function KpiTile({ label, value, delta, deltaTone = 'muted', style }) {
  const toneColor = { up: 'var(--pnl-up)', down: 'var(--pnl-down)', gold: 'var(--accent-gold)', muted: 'var(--text-muted)' }[deltaTone];
  return (
    <div style={{ padding: '16px 18px', background: 'var(--surface-card)', border: '1px solid var(--border-hairline)', borderRadius: 'var(--radius-md)', ...style }}>
      <u style={{ display: 'block', textDecoration: 'none', font: 'var(--type-eyebrow)', letterSpacing: 'var(--track-eyebrow)', textTransform: 'uppercase', color: 'var(--text-muted)', marginBottom: 8 }}>{label}</u>
      <b style={{ display: 'block', font: 'var(--type-stat)', letterSpacing: 'var(--track-stat)', color: 'var(--text-heading)' }}>{value}</b>
      {delta != null && <span style={{ display: 'block', font: '500 12px var(--font-mono)', marginTop: 6, color: toneColor }}>{delta}</span>}
    </div>
  );
}
