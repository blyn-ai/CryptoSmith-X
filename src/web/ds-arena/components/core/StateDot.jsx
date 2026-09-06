import React from 'react';

const FILL = { observed: 'var(--state-running)', stale: 'var(--state-stale)', none: 'transparent' };
const EDGE = { observed: 'var(--state-running)', stale: 'var(--state-stale)', none: 'var(--state-stopped)' };

/** The feed's operational state, as a dot. Filled ink = observed; a hollow ring = nothing has
 *  been observed. Colour is never the only signal — keep the word in the title. */
export function StateDot({ state = 'observed', size, title }) {
  return (
    <i aria-hidden="true" title={title} style={{
      width: size || 'var(--dot)', height: size || 'var(--dot)', flex: 'none', display: 'block',
      borderRadius: 'var(--radius-circle)',
      background: FILL[state] || 'transparent',
      border: '1px solid ' + (EDGE[state] || 'transparent')
    }} />
  );
}
