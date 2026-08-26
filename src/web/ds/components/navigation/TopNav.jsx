import React from 'react';
import { Wordmark } from './Wordmark.jsx';
export function TopNav({ items = [], active, onNavigate, user, live = 'LIVE', markSrc, style }) {
  return (
    <header style={{ display: 'flex', alignItems: 'center', gap: 28, padding: '15px 30px', borderBottom: '1px solid var(--border-card)', background: 'var(--surface-page)', ...style }}>
      <span style={{ display: 'flex', alignItems: 'center', gap: 11 }}>
        {markSrc && <img src={markSrc} width="28" height="28" alt="" />}
        <Wordmark size={16} />
      </span>
      <nav style={{ display: 'flex', gap: 24, font: 'var(--type-nav)', letterSpacing: 'var(--track-nav)', textTransform: 'uppercase' }}>
        {items.map((it) => {
          const on = it === active;
          return <a key={it} href="#" onClick={(e) => { e.preventDefault(); onNavigate && onNavigate(it); }}
            style={{ color: on ? 'var(--lilac-100)' : 'var(--text-muted)', padding: '4px 0', borderBottom: on ? '2px solid var(--gold-400)' : '2px solid transparent' }}>{it}</a>;
        })}
      </nav>
      <span style={{ marginLeft: 'auto', display: 'flex', alignItems: 'center', gap: 16 }}>
        {live && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 7, font: '600 10px var(--font-mono)', letterSpacing: '.18em', color: 'var(--live)' }}>
            <s style={{ width: 7, height: 7, borderRadius: '50%', background: 'var(--live)', boxShadow: 'var(--shadow-live)', textDecoration: 'none' }}></s>{live}
          </span>
        )}
        {user && (
          <span style={{ display: 'flex', alignItems: 'center', gap: 9, padding: '6px 12px 6px 7px', border: '1px solid var(--border-input)', borderRadius: 'var(--radius-sm)', font: '500 12px var(--font-mono)', color: 'var(--lilac-200)' }}>
            <img src="../../assets/cryptosmith-coin.svg" width="20" height="20" alt="" />{user}
          </span>
        )}
      </span>
    </header>
  );
}
