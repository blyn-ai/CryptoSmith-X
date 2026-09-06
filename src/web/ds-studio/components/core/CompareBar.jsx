import React from 'react';

/** A figure against the largest venue on screen. Log-scaled, because linear flattens a
 *  60-unit book against a 3,200-unit one into nothing. Not a progress bar — there is no
 *  target here, only a comparison. */
export function CompareBar({ value, max, call = 'ticker', width, opacity }) {
  const pct = (value === null || value === undefined || !max) ? 0
    : Math.min(100, Math.log10(value + 1) / Math.log10(max + 1) * 100);
  return (
    <span aria-hidden="true" style={{
      display: 'block', width: width || 'var(--bar-w)', height: 'var(--bar-h)',
      background: 'var(--tint-neutral)', opacity
    }}>
      <i style={{ display: 'block', height: '100%', width: pct.toFixed(1) + '%', background: 'var(--bar-' + call + ')' }} />
    </span>
  );
}
