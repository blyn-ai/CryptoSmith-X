import React from 'react';
import { TYPE } from '../core/Num.jsx';
import { Tag } from '../core/Tag.jsx';
import { FreshnessStrip } from './FreshnessStrip.jsx';

/** The sticky first column: who this row is, what the instrument is, and how fresh the row
 *  is as a whole. The platform name is the only 13px type in the table — everything else is
 *  a figure or a label. */
export function VenueCell({ platform, kind = 'spot', calls, windowSeconds, title }) {
  return (
    <span title={title} style={{
      position: 'sticky', left: 0, zIndex: 2, background: 'var(--surface-card)',
      padding: 'var(--pad-cell)', display: 'flex', flexDirection: 'column',
      justifyContent: 'center', gap: 'var(--gap-inline)', minWidth: 0
    }}>
      <span style={{ ...TYPE.data, fontSize: 'var(--fs-meta)', lineHeight: 1.1, color: 'var(--text-data)', whiteSpace: 'nowrap', overflow: 'hidden', textOverflow: 'ellipsis' }}>{platform}</span>
      <Tag tone={kind === 'perp' ? 'perp' : 'spot'}>{kind}</Tag>
      {calls ? <FreshnessStrip calls={calls} windowSeconds={windowSeconds} /> : null}
    </span>
  );
}
