import React from 'react';
export function SideBadge({ side = 'long', style }) {
  const long = String(side).toLowerCase() === 'long';
  return <span style={{ display: 'inline-block', font: 'var(--type-badge)', fontSize: 10, letterSpacing: '.1em', padding: '3px 8px', borderRadius: 'var(--radius-xs)', background: long ? 'var(--tint-up)' : 'var(--tint-down)', color: long ? 'var(--long)' : 'var(--short)', ...style }}>{long ? 'LONG' : 'SHORT'}</span>;
}
