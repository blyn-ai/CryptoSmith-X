import React from 'react';
import { Button } from '../core/Button.jsx';
export function Dialog({ open = true, title, children, confirmLabel = 'Confirm', cancelLabel = 'Cancel', onConfirm, onCancel, danger, width = 440 }) {
  if (!open) return null;
  return (
    <div style={{ position: 'fixed', inset: 0, background: 'var(--surface-overlay)', display: 'grid', placeItems: 'center', zIndex: 100 }} onClick={onCancel}>
      <div onClick={(e) => e.stopPropagation()} style={{ width, maxWidth: '90vw', background: 'var(--surface-raised)', border: '1px solid var(--border-card)', borderRadius: 'var(--radius-lg)', boxShadow: 'var(--shadow-modal)', padding: '22px 24px' }}>
        <h3 style={{ margin: 0, font: 'var(--type-h3)', color: 'var(--text-heading)' }}>{title}</h3>
        <div style={{ margin: '12px 0 20px', font: 'var(--type-body)', color: 'var(--text-body)' }}>{children}</div>
        <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end' }}>
          {cancelLabel && <Button variant="ghost" onClick={onCancel}>{cancelLabel}</Button>}
          <Button variant={danger ? 'danger' : 'primary'} onClick={onConfirm}>{confirmLabel}</Button>
        </div>
      </div>
    </div>
  );
}
