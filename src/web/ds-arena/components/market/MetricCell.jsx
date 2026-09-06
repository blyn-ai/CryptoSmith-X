import React from 'react';
import { Num } from '../core/Num.jsx';
import { Tag } from '../core/Tag.jsx';
import { AgeLine } from '../core/AgeLine.jsx';
import { Sparkline } from '../core/Sparkline.jsx';
import { CompareBar } from '../core/CompareBar.jsx';
import { MirrorBar } from '../core/MirrorBar.jsx';

/** One figure, with everything the surface says about it, in a fixed vertical order:
 *  a 12px slot for the BEST mark, the figure, its hourly line OR its comparative bar, and
 *  the age of the call that wrote it. The slot is reserved whether or not the mark is there,
 *  so figures sit on one line across the row. Everything except the age fades with the age. */
export function MetricCell({
  value, decimals = 2, signed, percent, unit,
  bid, ask, max, call = 'ticker',
  series, hot, best, band, age, windowSeconds = 30, tint
}) {
  const opacity = age === null || age === undefined ? 1
    : Math.max(0.15, 1 - 0.85 * Math.pow(Math.min(age / windowSeconds, 1), 0.4));
  const pair = bid !== undefined;
  return (
    <span style={{
      padding: 'var(--pad-cell)', display: 'flex', flexDirection: 'column',
      alignItems: 'flex-end', justifyContent: 'center', gap: 'var(--gap-cell)',
      background: tint ? 'var(--tint-' + tint + ')' : undefined
    }}>
      <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'flex-end', gap: 6, height: 'var(--slot-mark)' }}>
        {band ? <Tag tone={band}>{band}</Tag> : null}
        {best ? <Tag tone="best">Best</Tag> : null}
      </span>
      {pair ? (
        <span style={{ display: 'flex', alignItems: 'center', gap: 'var(--gap-inline)' }}>
          <Num value={bid} decimals={0} opacity={opacity} />
          <span className="csx-mono" style={{ fontSize: 'var(--fs-data)', color: 'var(--text-faint)', opacity }}>/</span>
          <Num value={ask} decimals={0} opacity={opacity} />
        </span>
      ) : (
        <Num value={value} decimals={decimals} signed={signed} percent={percent} unit={unit} opacity={opacity} />
      )}
      {series ? <Sparkline values={series} call={call} hot={hot} opacity={opacity} />
        : pair ? <MirrorBar bid={bid} ask={ask} max={max} opacity={opacity} />
        : max ? <CompareBar value={value} max={max} call={call === 'depth' ? 'ticker' : call} opacity={opacity} />
        : <span style={{ display: 'block', height: 'var(--spark-h)' }} />}
      <AgeLine seconds={age} windowSeconds={windowSeconds} missing={value === null && !pair} />
    </span>
  );
}
