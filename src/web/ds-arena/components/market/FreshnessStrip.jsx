import React from 'react';
import { TYPE } from '../core/Num.jsx';

/** The row's own freshness, in the venue cell: a scale that runs green where a call has just
 *  landed to magenta where it is spent, a tick per call, the span between the freshest and
 *  the oldest, and each call named with its age — because inside one platform the three
 *  calls answer at different rates. */
export function FreshnessStrip({ calls, windowSeconds = 30 }) {
  const present = calls.filter(c => c.seconds !== null && c.seconds !== undefined);
  const xs = present.map(c => Math.min(c.seconds / windowSeconds, 1));
  const ages = present.map(c => c.seconds);
  const lo = xs.length ? Math.min.apply(null, xs) : 0;
  const hi = xs.length ? Math.max.apply(null, xs) : 0;
  const minA = ages.length ? Math.round(Math.min.apply(null, ages)) : null;
  const maxA = ages.length ? Math.round(Math.max.apply(null, ages)) : null;
  const dead = maxA !== null && maxA >= windowSeconds * 12;
  const sec = n => n > 99 ? '99+ s' : n + ' s';

  return (
    <span style={{ display: 'flex', flexDirection: 'column', gap: 'var(--gap-inline)' }}>
      <span style={{ position: 'relative', display: 'block', width: 'var(--strip-w)', height: 'var(--strip-h)', background: 'var(--age-scale)' }}>
        <i style={{ position: 'absolute', top: 1, height: 1, left: (lo * 100).toFixed(1) + '%', width: Math.max(0.9, (hi - lo) * 100).toFixed(1) + '%', background: 'rgba(30,20,8,.8)' }} />
        {present.map((c, k) => {
          const x = Math.min(c.seconds / windowSeconds, 1);
          return <i key={k} style={{ position: 'absolute', top: -2, height: 7, width: 1, left: (x * 100).toFixed(1) + '%', background: x >= 1 ? 'var(--surface-card)' : 'var(--text-heading)' }} />;
        })}
      </span>
      <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 6, width: 'var(--strip-w)', ...TYPE.age, color: 'var(--text-faint)' }}>
        <span>{dead ? '' : 'fresh ' + sec(minA)}</span>
        <span style={{ display: 'flex', alignItems: 'center', gap: 3, color: maxA >= windowSeconds ? 'var(--state-hold-ink)' : 'var(--text-faint)' }}>
          {maxA >= windowSeconds ? <i style={{ fontStyle: 'normal', color: 'var(--state-stale)' }}>△</i> : null}
          {dead ? 'live data degraded' : 'old ' + sec(maxA)}
        </span>
      </span>
      <span style={{ display: 'flex', alignItems: 'center', gap: 7, flexWrap: 'wrap', ...TYPE.age, fontWeight: 'var(--fw-medium)', color: 'var(--text-faint)' }}>
        {calls.map((c, k) => c.seconds === null || c.seconds === undefined ? null : (
          <span key={k} style={{ color: c.seconds >= windowSeconds ? 'var(--state-hold-ink)' : 'var(--text-faint)' }}>
            {c.label + ' ' + sec(Math.round(c.seconds))}
          </span>
        ))}
      </span>
    </span>
  );
}
