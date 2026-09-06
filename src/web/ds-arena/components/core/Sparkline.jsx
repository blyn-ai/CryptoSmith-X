import React from 'react';

/** One hourly aggregate per point — a line, not a candle. Only the four metrics the backend
 *  actually rolls up hourly get one (spread, funding, open interest, depth 25bps) plus the
 *  price close series; asking for OHLC here would be asking for data that does not exist.
 *  Full strength where the series ran over the window, half strength otherwise. */
export function Sparkline({ values, call = 'ticker', hot = false, width = 60, height = 11, opacity }) {
  if (!values || values.length < 2) return <span style={{ display: 'block', width, height }} />;
  let lo = Math.min.apply(null, values), hi = Math.max.apply(null, values);
  if (hi === lo) { hi += Math.abs(hi) * 0.02 || 1; lo -= Math.abs(lo) * 0.02 || 1; }
  const y = v => (height - 1.5) - ((v - lo) / (hi - lo)) * (height - 3);
  const step = (width - 1) / (values.length - 1);
  const d = values.map((v, i) => (i ? 'L' : 'M') + (i * step + 0.5).toFixed(1) + ' ' + y(v).toFixed(1)).join('');
  return (
    <svg aria-hidden="true" viewBox={'0 0 ' + width + ' ' + height} width={width} height={height} style={{ display: 'block', opacity }}>
      <path d={d} fill="none" strokeWidth="1" stroke={'var(--spark-' + call + (hot ? '-hot' : '') + ')'} />
    </svg>
  );
}
