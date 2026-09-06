import React from 'react';
import { TYPE } from './Num.jsx';

/** The age of the call that wrote the figure above it — whole seconds, a fixed slot so the
 *  block never changes size, and a △ once the call is past its window. Metadata about the
 *  figure, so it is deliberately the smallest type on the surface; it never fades, because a
 *  ghosted figure still has to be able to say when it died. */
export function AgeLine({ seconds, windowSeconds = 30, missing = false }) {
  if (missing || seconds === null || seconds === undefined) {
    return <span style={{ ...TYPE.age, color: 'var(--text-unmeasured)', width: 'var(--age-slot-w)', textAlign: 'right' }}>—</span>;
  }
  const spent = seconds >= windowSeconds;
  const dead = seconds >= windowSeconds * 12;
  const text = dead ? 'degraded' : (Math.round(seconds) > 99 ? '99+ s ago' : Math.round(seconds) + ' s ago');
  return (
    <span style={{
      display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 'var(--gap-inline)',
      width: 'var(--age-slot-w)', ...TYPE.age, whiteSpace: 'nowrap',
      color: spent ? 'var(--state-hold-ink)' : 'var(--text-faint)'
    }}>
      {spent ? <i style={{ fontStyle: 'normal', color: 'var(--state-stale)' }}>△</i> : null}
      {text}
    </span>
  );
}
