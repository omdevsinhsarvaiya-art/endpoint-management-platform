import { useId, useRef, type ReactNode } from 'react'
import { useDialogDismiss } from './useDialogDismiss'
import { useDialogFocus } from './dialogFocus'

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
 *
 * Focus moves onto the dialog itself when it opens and goes back to whatever
 * opened it when it closes. Without that, the caret stays behind the overlay on
 * a control the person can no longer see — a row's Actions button, say, where
 * Enter re-opens the menu underneath the dialog and Tab walks the table instead
 * of reaching Cancel. The container is what takes focus, not a button, so the
 * question is announced before the answers and no keystroke arrives already
 * aimed at the destructive one.
 *
 * The ids are generated rather than fixed: dialogs stack in this console, and
 * two mounted at once with the same id would leave a screen reader naming one
 * of them for both.
 */
export function ConfirmDialog({
  title,
  children,
  confirmLabel = 'Confirm',
  onCancel,
  onConfirm,
}: ConfirmDialogProps) {
  const titleId = useId()
  const bodyId = useId()
  const container = useRef<HTMLDivElement>(null)

  useDialogDismiss(onCancel)
  useDialogFocus(container)

  return (
    <div
      ref={container}
      className="overlay"
      role="dialog"
      aria-modal="true"
      aria-labelledby={titleId}
      aria-describedby={bodyId}
      // Focusable by script only: the dialog holds focus while it is open
      // without adding a stop to the page's tab order.
      tabIndex={-1}
    >
      <div className="dialog" style={{ maxWidth: 460 }}>
        <div className="dialog-header">
          <h2 id={titleId}>{title}</h2>
        </div>
        <div className="dialog-body">
          <p id={bodyId} className="muted" style={{ margin: 0, fontSize: 13.5 }}>
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
