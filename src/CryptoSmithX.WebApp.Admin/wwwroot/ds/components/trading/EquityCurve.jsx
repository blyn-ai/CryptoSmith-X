import React from 'react';
export function EquityCurve({ points = [4, 14, 8, 34, 28, 54, 46, 75, 65, 97, 87, 121, 113, 140], height = 240, showFill = true, style }) {
  const w = 800, h = height, pad = 12;
  const min = Math.min(...points), max = Math.max(...points);
  const xy = points.map((p, i) => [ (i / (points.length - 1)) * w, h - pad - ((p - min) / (max - min || 1)) * (h - pad * 2) ]);
  const line = xy.map(([x, y], i) => `${i ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`).join(' ');
  const uid = React.useId().replace(/:/g, '');
  return (
    <svg viewBox={`0 0 ${w} ${h}`} preserveAspectRatio="none" style={{ display: 'block', width: '100%', height: 'auto', ...style }}>
      <defs>
        <linearGradient id={`s${uid}`} x1="0" y1="0" x2="1" y2="0"><stop offset="0" stopColor="#F5B84F" /><stop offset=".55" stopColor="#C98F63" /><stop offset="1" stopColor="#6B4EDB" /></linearGradient>
        <linearGradient id={`f${uid}`} x1="0" y1="0" x2="0" y2="1"><stop offset="0" stopColor="#8C6BC9" stopOpacity=".22" /><stop offset="1" stopColor="#8C6BC9" stopOpacity="0" /></linearGradient>
      </defs>
      {showFill && <path d={`${line} L${w} ${h} L0 ${h} Z`} fill={`url(#f${uid})`} />}
      <path d={line} fill="none" stroke={`url(#s${uid})`} strokeWidth="2.5" />
    </svg>
  );
}
