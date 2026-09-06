import React from 'react';

/** Depth at a band, as the two numbers it actually is: bid to the left of the centre, ask to
 *  the right, both against the largest side in that band. A one-sided book — the thing this
 *  column exists to catch — reads as a lopsided bar. */
export function MirrorBar({ bid, ask, max, width, opacity }) {
  const pct = v => (v === null || v === undefined || !max) ? 0
    : Math.min(100, Math.log10(v + 1) / Math.log10(max + 1) * 100);
  return (
    <span aria-hidden="true" style={{
      display: 'flex', alignItems: 'center', gap: 1,
      width: width || 'var(--mirror-w)', height: 'var(--mirror-h)', opacity
    }}>
      <span style={{ flex: 1, display: 'flex', justifyContent: 'flex-end', background: 'var(--tint-neutral)', height: '100%' }}>
        <i style={{ display: 'block', height: '100%', width: pct(bid).toFixed(1) + '%', background: 'var(--bar-depth-bid)' }} />
      </span>
      <span style={{ flex: 1, background: 'var(--tint-neutral)', height: '100%' }}>
        <i style={{ display: 'block', height: '100%', width: pct(ask).toFixed(1) + '%', background: 'var(--bar-depth-ask)' }} />
      </span>
    </span>
  );
}
