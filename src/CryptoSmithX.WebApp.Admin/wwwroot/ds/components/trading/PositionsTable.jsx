import React from 'react';
import { SideBadge } from '../core/SideBadge.jsx';
const th = { font: '500 10px var(--font-mono)', letterSpacing: '.16em', textTransform: 'uppercase', color: 'var(--text-muted)', textAlign: 'left', padding: '12px 20px', borderBottom: '1px solid var(--border-hairline)' };
const td = { padding: '12px 20px', borderBottom: '1px solid rgba(161,138,255,.06)', color: 'var(--text-body)', font: '400 12.5px var(--font-mono)' };
const num = { textAlign: 'right' };
export function PositionsTable({ rows = [], style }) {
  return (
    <table style={{ width: '100%', borderCollapse: 'collapse', ...style }}>
      <thead><tr>
        <th style={th}>Market</th><th style={th}>Venue</th><th style={th}>Side</th>
        <th style={{ ...th, ...num }}>Size</th><th style={{ ...th, ...num }}>Entry</th><th style={{ ...th, ...num }}>Mark</th><th style={{ ...th, ...num }}>uPnL</th>
      </tr></thead>
      <tbody>
        {rows.map((r, i) => {
          const up = String(r.upnl).trim().startsWith('+');
          const last = i === rows.length - 1;
          const cell = last ? { ...td, borderBottom: 0 } : td;
          return (
            <tr key={r.market + i}>
              <td style={cell}><b style={{ color: 'var(--text-heading)', fontWeight: 500 }}>{r.market}</b></td>
              <td style={cell}>{r.venue}</td>
              <td style={cell}><SideBadge side={r.side} /></td>
              <td style={{ ...cell, ...num }}>{r.size}</td>
              <td style={{ ...cell, ...num }}>{r.entry}</td>
              <td style={{ ...cell, ...num }}>{r.mark}</td>
              <td style={{ ...cell, ...num, color: up ? 'var(--pnl-up)' : 'var(--pnl-down)', fontWeight: 500 }}>{r.upnl}</td>
            </tr>
          );
        })}
      </tbody>
    </table>
  );
}
