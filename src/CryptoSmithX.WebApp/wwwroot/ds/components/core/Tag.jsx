import React from 'react';
const tagPalette = {
  violet: { background: 'var(--tint-violet)', color: 'var(--violet-400)' },
  gold: { background: 'var(--tint-gold)', color: 'var(--gold-400)' },
  neutral: { background: 'var(--tint-neutral)', color: 'var(--text-muted)' },
  up: { background: 'var(--tint-up)', color: 'var(--up-500)' },
  down: { background: 'var(--tint-down)', color: 'var(--down-500)' },
};
export function Tag({ tone = 'neutral', style, children }) {
  return <span style={{ display: 'inline-block', font: 'var(--type-badge)', letterSpacing: 'var(--track-badge)', textTransform: 'uppercase', padding: '3px 7px', borderRadius: 'var(--radius-xs)', ...tagPalette[tone], ...style }}>{children}</span>;
}
