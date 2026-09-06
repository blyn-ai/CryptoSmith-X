import React from 'react';
import { TYPE } from '../core/Num.jsx';

/** The header band that names which call each stretch of columns came from. Three calls,
 *  three fills — magenta ticker, green open interest, bronze depth — and the same three hues
 *  reappear on every bar, line and tick below, so the table reads as three vertical bands
 *  rather than twenty unrelated columns. */
export function CallBands({ template, venueSpan, tickerSpan, oiSpan, depthSpan, venueLabel = 'Venue', tickerLabel = 'Ticker call · one response', oiLabel = 'OI call', depthLabel = 'Depth sweep' }) {
  const cell = (span, background, color, label, sticky) => (
    <span style={{
      gridColumn: 'span ' + span, display: 'flex', alignItems: 'center', padding: 'var(--pad-cell)',
      background, color, ...TYPE.eyebrow,
      position: sticky ? 'sticky' : undefined, left: sticky ? 0 : undefined, zIndex: sticky ? 2 : undefined
    }}>{label}</span>
  );
  return (
    <div style={{
      display: 'grid', gridTemplateColumns: template, height: 'var(--row-band-h)',
      background: 'var(--surface-card)', borderBottom: '1px solid var(--border-hairline)'
    }}>
      {cell(venueSpan, 'var(--surface-card)', 'var(--eyebrow)', venueLabel, true)}
      {cell(tickerSpan, 'var(--band-ticker)', 'var(--band-ink)', tickerLabel)}
      {cell(oiSpan, 'var(--band-oi)', 'var(--band-ink)', oiLabel)}
      {cell(depthSpan, 'var(--band-depth)', 'var(--band-ink)', depthLabel)}
    </div>
  );
}
