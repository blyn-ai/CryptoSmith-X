import React from 'react';

/* The type roles as LONGHANDS. Do not collapse to `font: var(--type-num)`: a shorthand bakes
   the family at :root and silently kills every per-role override. */
export const TYPE = {
  num: { fontFamily: 'var(--font-mono)', fontSize: 'var(--fs-data)', fontWeight: 'var(--fw-regular)', lineHeight: 1 },
  age: { fontFamily: 'var(--font-mono)', fontSize: 'var(--fs-age)', fontWeight: 'var(--fw-regular)', lineHeight: 1, letterSpacing: 'var(--track-age)' },
  eyebrow: { fontFamily: 'var(--font-mono)', fontSize: 'var(--fs-eyebrow)', fontWeight: 'var(--fw-medium)', lineHeight: 1.2, letterSpacing: 'var(--track-label)', textTransform: 'uppercase' },
  label: { fontFamily: 'var(--font-mono)', fontSize: 'var(--fs-eyebrow)', fontWeight: 'var(--fw-medium)', lineHeight: 1, letterSpacing: 'var(--track-badge)', textTransform: 'uppercase' },
  data: { fontFamily: 'var(--font-mono)', fontSize: 'var(--fs-data)', fontWeight: 'var(--fw-regular)', lineHeight: 'var(--lh-mono)' },
  panelTitle: { fontFamily: 'var(--font-display)', fontSize: 'var(--fs-ui)', fontWeight: 400, lineHeight: 1.3, textTransform: 'uppercase' },
  statement: { fontFamily: 'var(--font-display)', fontSize: 'var(--fs-h1)', fontWeight: 400, lineHeight: 'var(--lh-h1)', letterSpacing: 'var(--track-h1)', textTransform: 'uppercase' },
  body: { fontFamily: 'var(--font-body)', fontSize: 'var(--fs-meta)', fontWeight: 'var(--fw-regular)', lineHeight: 'var(--lh-body)' }
};

const TONE = {
  data: 'var(--text-data)',
  muted: 'var(--text-muted)',
  faint: 'var(--text-faint)',
  ticker: 'var(--call-ticker)',
  oi: 'var(--call-oi)',
  depth: 'var(--call-depth)',
  alarm: 'var(--state-stale)'
};

/** Every figure on the market surface goes through here: mono, tabular, and honest about
 *  missing data — null renders an em dash in its own faint ink, which is never the ink a
 *  measured zero gets. A dash is not a zero and a zero is an observation. */
export function Num({ value, decimals = 2, signed = false, percent = false, unit, tone = 'data', size, align = 'right', title, opacity }) {
  const missing = value === null || value === undefined || Number.isNaN(value);
  const zero = !missing && Number(value) === 0;
  let color = TONE[tone] || TONE.data;
  if (missing) color = 'var(--text-unmeasured)';
  else if (zero) color = 'var(--text-zero)';

  const body = missing ? '—' : (() => {
    const n = Number(value);
    const s = n.toLocaleString('en-GB', { minimumFractionDigits: decimals, maximumFractionDigits: decimals });
    return (signed && n > 0 ? '+' : '') + s + (percent ? '%' : '');
  })();

  return (
    <span className="csx-num" title={title} style={{
      ...TYPE.num, fontSize: size, color, opacity,
      fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap', textAlign: align, display: 'inline-block'
    }}>
      {body}{!missing && unit ? <span style={{ color: 'var(--text-faint)', marginLeft: 4 }}>{unit}</span> : null}
    </span>
  );
}
