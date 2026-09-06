import React from 'react';
import { TYPE } from '../core/Num.jsx';

/** One venue's price history. Candles get the real library — TradingView Lightweight Charts,
 *  self-hosted — because price is the only true OHLC series the backend stores. Mount one
 *  instance per venue and tie the time scales: the panels are stacked to be compared, so the
 *  same hour has to sit in the same place on every one of them. */
export function CandlePanel({ platform, symbol, range, onMount, height }) {
  const ref = React.useRef(null);
  React.useEffect(() => { if (onMount && ref.current) { return onMount(ref.current); } }, [onMount]);
  return (
    <section style={{ background: 'var(--surface-card)', border: 'var(--panel-border)' }}>
      <header style={{
        display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 14,
        padding: 'var(--pad-panel)', borderBottom: '1px solid var(--border-hairline)'
      }}>
        <h2 style={{ margin: 0, display: 'flex', alignItems: 'baseline', gap: 9, ...TYPE.panelTitle, color: 'var(--text-heading)' }}>
          {platform}
          <span className="csx-mono" style={{ fontSize: 'var(--fs-data)', textTransform: 'none', color: 'var(--call-ticker)' }}>{symbol}</span>
        </h2>
        <span className="csx-mono" style={{ fontSize: 'var(--fs-data)', color: 'var(--text-faint)' }}>{range}</span>
      </header>
      <div style={{ padding: '12px 14px 8px' }}>
        <div ref={ref} style={{ height: height || 'var(--chart-h)', width: '100%' }} />
      </div>
    </section>
  );
}
