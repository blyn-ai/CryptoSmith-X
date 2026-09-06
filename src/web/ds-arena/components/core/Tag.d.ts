import type { ReactElement, ReactNode } from 'react';

export type TagTone = 'neutral' | 'spot' | 'perp' | 'tight' | 'wide' | 'alarm' | 'best' | 'worst';

/**
 * Mono-caps categorical label — unframed except for `best`, which is a chip because it marks
 * a winner rather than describing a value.
 */
export interface TagProps {
  tone?: TagTone;
  children: ReactNode;
  title?: string;
}

export function Tag(props: TagProps): ReactElement;
