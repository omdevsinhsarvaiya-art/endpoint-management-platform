import { useEffect, useRef } from 'react'

/**
 * The stack of currently-open dialogs, innermost last.
 *
 * Dialogs stack in this console — opening a user's details and then confirming a
 * change leaves two mounted at once. A plain per-dialog key listener gets that
 * backwards: every listener fires, and the one registered first (the *outer*
 * dialog) wins, so Escape would dismiss the details panel and leave the
 * confirmation floating on its own. Routing every keypress through one shared
 * stack means Escape always closes the dialog on top, which is the only one the
 * person can actually see the front of.
 */
const openDialogs: Array<() => void> = []

let listening = false

function onKeyDown(event: KeyboardEvent) {
  if (event.key !== 'Escape') return
  const topmost = openDialogs[openDialogs.length - 1]
  if (!topmost) return
  event.preventDefault()
  topmost()
}

/**
 * Closes the topmost dialog when Escape is pressed.
 *
 * Escape always means "cancel": it is wired to the same handler as the Cancel
 * button and never to the confirming action, so it can never be the keystroke
 * that restarts a machine or deletes an account. A modal with no keyboard exit
 * is a defect rather than a stylistic choice, which is why every dialog here
 * takes this hook.
 */
export function useDialogDismiss(onDismiss: () => void): void {
  // The handler read at keypress time is always the newest one, but the entry on
  // the stack keeps one identity for the dialog's whole life. Without this, a
  // re-render of the outer dialog would re-push it above the inner one and
  // Escape would start closing the wrong thing.
  const latest = useRef(onDismiss)
  latest.current = onDismiss

  useEffect(() => {
    const entry = () => latest.current()
    openDialogs.push(entry)

    if (!listening) {
      window.addEventListener('keydown', onKeyDown)
      listening = true
    }

    return () => {
      const index = openDialogs.lastIndexOf(entry)
      if (index >= 0) openDialogs.splice(index, 1)

      if (openDialogs.length === 0 && listening) {
        window.removeEventListener('keydown', onKeyDown)
        listening = false
      }
    }
  }, [])
}
