import React from 'react';
export function Wordmark({ size = 16, descriptor = false, style }) {
  return (
    <span style={{ display: 'inline-flex', flexDirection: 'column', ...style }}>
      <b style={{ font: `600 ${size}px/1.05 var(--font-display)`, color: 'var(--text-heading)', letterSpacing: '-.01em', whiteSpace: 'nowrap' }}>
        CryptoSmith <i style={{ fontStyle: 'normal', color: 'var(--violet-400)' }}>X</i>
      </b>
      {descriptor && (
        <span style={{ display: 'flex', justifyContent: 'space-between', font: `500 ${Math.round(size * .4)}px var(--font-mono)`, letterSpacing: '.1em', color: 'var(--text-muted)', marginTop: Math.max(3, size * .12) }}>
          <span>PERPS &amp; CRYPTO</span><span>TRADE BOT</span>
        </span>
      )}
    </span>
  );
}
