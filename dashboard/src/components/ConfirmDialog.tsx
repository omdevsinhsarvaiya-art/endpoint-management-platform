import type { ReactNode } from 'react'
import { useDialogDismiss } from './useDialogDismiss'

interface ConfirmDialogProps {
  title: string
  children: ReactNode
  /** Verb for the confirming button. Say what will happen, never just "OK". */
  confirmLabel?: string
  onCancel: () => void
  onConfirm: () => void
}

/**
 * Confirmation for an action that reaches a real Windows machine.
 *
 * Cancel is the plain button and confirm is the destructive one, in that order,
 * so the safe choice is where the eye lands first and the dangerous one has to
 * be aimed at. Escape and Cancel do the same thing; nothing dismisses this
 * dialog by confirming it.
 */
export function ConfirmDialog({
  title,
  children,
  confirmLabel = 'Confirm',
  onCancel,
  onConfirm,
}: ConfirmDialogProps) {
  useDialogDismiss(onCancel)

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="confirm-dialog-title">
      <div className="dialog" style={{ maxWidth: 460 }}>
        <div className="dialog-header">
          <h2 id="confirm-dialog-title">{title}</h2>
        </div>
        <div className="dialog-body">
          <p className="muted" style={{ margin: 0, fontSize: 13.5 }}>
            {children}
          </p>
        </div>
        <div className="dialog-footer">
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="button" className="btn-danger" onClick={onConfirm}>
            {confirmLabel}
          </button>
        </div>
      </div>
    </div>
  )
}
