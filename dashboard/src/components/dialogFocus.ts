import { useEffect, type RefObject } from 'react'

/**
 * Somewhere focus can be handed to, and whether it is still on the page.
 *
 * A DOM element satisfies this. The interface exists so the rules below can be
 * exercised without a fake DOM, the same way sidebarPreference takes its
 * storage: what is worth testing here is when focus is given back and when it
 * deliberately is not, and neither of those needs a browser to be decided.
 */
export interface FocusTarget {
  focus(): void
  /** False once the element has been removed from the document. */
  readonly isConnected: boolean
}

/** Where focus is right now. Injected so the capture rule can be tested. */
export interface FocusEnvironment {
  activeElement(): FocusTarget | null
}

/** Hands focus back to whoever had it. Safe to call when nobody did. */
export type RestoreFocus = () => void

/**
 * Remembers who holds focus now, so it can be given back later.
 *
 * A modal that opens without taking focus strands the keyboard behind it: the
 * caret stays on whatever opened the dialog, under the overlay, where Enter
 * re-triggers that control and Tab walks the page the person can no longer see.
 * Taking focus is only half of the exchange, though — a dialog that never gives
 * it back drops the caret at the top of the document on close, so someone who
 * cancels out of one row's menu has to tab through the whole table to reach the
 * next row.
 *
 * The opener is read once, when the dialog opens. Reading it at close time
 * would return whatever the dialog itself last focused, which is about to be
 * removed from the page.
 */
export function captureFocus(environment: FocusEnvironment): RestoreFocus {
  const opener = environment.activeElement()

  return () => {
    // A dialog routinely outlives what opened it: confirming a removal can take
    // the row, and with it the Actions button, out of the table. Focusing a
    // detached element does nothing at all in the browser, so the check is not
    // about safety but about honesty — there is nothing to restore, and the
    // caller must not be told otherwise by a call that quietly did nothing.
    if (opener === null || !opener.isConnected) return
    opener.focus()
  }
}

/** The live document, as the rule above sees it. */
export const documentFocus: FocusEnvironment = {
  activeElement: () => {
    const active = document.activeElement
    // A document with nothing focused reports <body>. Handing focus back to it
    // is indistinguishable from doing nothing, and would undo any placement the
    // surrounding page made for itself while the dialog was open.
    return active instanceof HTMLElement && active !== document.body ? active : null
  },
}

/**
 * Moves focus into a dialog when it opens and returns it when the dialog closes.
 *
 * The container takes focus rather than one of the buttons: it carries the
 * dialog's role and its accessible name, so a screen reader announces what the
 * dialog is asking before offering the answers, and the first Tab lands on
 * Cancel because Cancel comes first in the markup. Focusing the confirming
 * button instead would put a destructive action one stray Enter away.
 *
 * Dialogs stack in this console, and each keeps its own opener, so closing an
 * inner dialog returns focus to the outer one's control rather than to the
 * page behind both.
 */
export function useDialogFocus(container: RefObject<HTMLElement | null>): void {
  useEffect(() => {
    const restore = captureFocus(documentFocus)
    container.current?.focus()
    return restore
  }, [container])
}
