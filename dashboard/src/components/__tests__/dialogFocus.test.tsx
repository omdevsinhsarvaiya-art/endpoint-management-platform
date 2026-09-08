import { describe, expect, it } from 'vitest'
import { renderToStaticMarkup } from 'react-dom/server'
import { captureFocus, type FocusEnvironment, type FocusTarget } from '../dialogFocus'
import { ConfirmDialog } from '../ConfirmDialog'

/**
 * Where the keyboard goes when a confirmation opens, and where it comes back to.
 *
 * The defect this guards: a row's Actions menu returned focus to its trigger
 * and then opened a ConfirmDialog over it, leaving the caret on a button behind
 * the overlay. Enter re-opened the menu underneath the dialog and Tab walked
 * the table, so the two controls the dialog exists for — Cancel and "Yes,
 * remove" — could not be reached by keyboard at all.
 *
 * The exchange has two halves and both are tested here: the dialog takes focus
 * when it opens (its container is focusable, and the safe control is the first
 * one a Tab reaches), and it gives focus back to whatever opened it when it
 * closes. The give-back half is pure with an injected environment, so these run
 * the real rule the dashboard ships rather than a mock of it — no fake DOM,
 * matching the rest of this suite.
 */

/** A page of controls that record when focus is put on them. */
function page() {
  const focused: string[] = []
  let active: FocusTarget | null = null

  /** One focusable control. `isConnected` is writable so a test can remove it. */
  function control(name: string) {
    const element = {
      isConnected: true,
      focus() {
        focused.push(name)
        active = element
      },
    }
    return element
  }

  const environment: FocusEnvironment = { activeElement: () => active }

  return { focused, control, environment }
}

describe('returning focus when a dialog closes', () => {
  /**
   * The whole point: the operator's place in the table is where they left it,
   * not the top of the document.
   */
  it('gives focus back to the control that opened the dialog', () => {
    const { focused, control, environment } = page()
    const trigger = control('actions-trigger')
    trigger.focus()

    const restore = captureFocus(environment)
    control('dialog').focus()
    restore()

    expect(focused).toEqual(['actions-trigger', 'dialog', 'actions-trigger'])
  })

  /**
   * The opener has to be read when the dialog opens. Read at close time it
   * would be whatever the dialog itself last focused — an element that is about
   * to be removed from the page, which is the same as restoring nothing.
   */
  it('remembers who had focus at open time, not at close time', () => {
    const { focused, control, environment } = page()
    const trigger = control('row-button')
    trigger.focus()

    const restore = captureFocus(environment)
    control('cancel-button').focus()
    control('confirm-button').focus()
    restore()

    expect(focused.at(-1)).toBe('row-button')
  })

  /**
   * Confirming a removal can take the row — and with it the Actions button —
   * out of the table before the dialog unmounts. There is then nothing to
   * restore, and the caret is left wherever the browser put it rather than
   * aimed at an element that is no longer on the page.
   */
  it('does nothing when the opener has left the page', () => {
    const { focused, control, environment } = page()
    const trigger = control('removed-row-button')
    trigger.focus()

    const restore = captureFocus(environment)
    trigger.isConnected = false
    restore()

    expect(focused).toEqual(['removed-row-button'])
  })

  /** A dialog opened from a page that had nothing focused has nothing to give back. */
  it('has nothing to restore when nothing held focus', () => {
    const { focused, environment } = page()

    const restore = captureFocus(environment)

    expect(() => restore()).not.toThrow()
    expect(focused).toEqual([])
  })

  /**
   * Dialogs stack in this console — a details panel with a confirmation over it
   * is routine. Each keeps its own opener, so closing the inner one returns to
   * the outer dialog rather than skipping past it to the page behind both.
   */
  it('unwinds a stack of dialogs one opener at a time', () => {
    const { focused, control, environment } = page()
    control('page-button').focus()

    const restoreOuter = captureFocus(environment)
    control('outer-dialog-button').focus()

    const restoreInner = captureFocus(environment)
    control('inner-dialog').focus()

    restoreInner()
    restoreOuter()

    expect(focused.slice(-2)).toEqual(['outer-dialog-button', 'page-button'])
  })
})

describe('taking focus when a dialog opens', () => {
  const markup = () =>
    renderToStaticMarkup(
      <ConfirmDialog title="Remove Google Chrome?" confirmLabel="Yes, remove" onCancel={() => {}} onConfirm={() => {}}>
        This uninstalls it.
      </ConfirmDialog>,
    )

  /**
   * Focus is placed on the dialog itself, which needs a tabindex to be able to
   * hold it. Negative, so holding focus does not also add a stop to the page's
   * tab order.
   */
  it('gives the dialog container somewhere for focus to land', () => {
    expect(markup()).toMatch(/role="dialog"[^>]*tabindex="-1"/)
  })

  /**
   * The container carries the role, the name and the description, so a screen
   * reader announces what is being asked before the answers to it.
   */
  it('names and describes the dialog on the element that takes focus', () => {
    const html = markup()

    expect(html).toMatch(/role="dialog"[^>]*aria-labelledby="/)
    expect(html).toMatch(/role="dialog"[^>]*aria-describedby="/)
  })

  /**
   * Cancel comes first in the markup, so the first Tab out of the container
   * reaches the safe control. A destructive action must never be the thing a
   * stray keystroke arrives at.
   */
  it('puts Cancel ahead of the destructive button', () => {
    const html = markup()

    expect(html.indexOf('Cancel')).toBeGreaterThan(-1)
    expect(html.indexOf('Cancel')).toBeLessThan(html.indexOf('Yes, remove'))
  })
})
