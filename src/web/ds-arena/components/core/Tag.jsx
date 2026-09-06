import React from 'react';
import { TYPE } from './Num.jsx';

/* A border means STATE or CONTROL — nothing else. A categorical value (SPOT, PERP, TIGHT,
   OBSERVED) is type and colour only. BEST and WORST are the exceptions: they mark the two
   ends of a column rather than describing a value, so they get a chip — a washed ground with
   dark ink, never a saturated one, or the word stops being readable at 9px. WIDE takes the
   WORST chip, because on the spread column it is the same verdict. */
const TONES = {
  neutral: { color: 'var(--text-muted)' },
  spot: { color: 'var(--kind-spot)' },
  perp: { color: 'var(--kind-perp)' },
  tight: { color: 'var(--tag-tight)' },
  wide: { color: 'var(--tag-wide)' },
  alarm: { color: 'var(--state-stale)' },
  best: { color: 'var(--tag-best-ink)', background: 'var(--tag-best-bg)', padding: '2px 5px' },
  worst: { color: 'var(--tag-worst-ink)', background: 'var(--tag-worst-bg)', padding: '2px 5px' }
};

/** Mono-caps categorical label: instrument type, spread band, feed state, BEST. */
export function Tag({ tone = 'neutral', children, title }) {
  return (
    <span title={title} style={{
      display: 'inline-block', ...TYPE.label, whiteSpace: 'nowrap', ...TONES[tone]
    }}>{children}</span>
  );
}
