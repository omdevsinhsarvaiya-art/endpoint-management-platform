import { useCallback, useEffect, useId, useLayoutEffect, useRef, useState, type KeyboardEvent } from 'react'
import { createPortal } from 'react-dom'
import { Icon } from './Icon'

/** One entry in the menu. A disabled entry stays in the list and says why. */
export interface RowActionsMenuItem<K extends string> {
  key: K
  label: string
  enabled: boolean
  /** Why the entry is unavailable; shown under the label and as its tooltip. */
  reason: string | null
  /** Styled as destructive, so "Remove" does not look like "Refresh". */
  destructive?: boolean
}

interface RowActionsMenuProps<K extends string> {
  /** The accessible name of the menu, e.g. `Actions for Google Chrome`. */
  label: string
  items: readonly RowActionsMenuItem<K>[]
  onSelect: (key: K) => void
  /** What the trigger says; changes to a progress word while the row is busy. */
  triggerLabel?: string
  /** Disables the trigger — while any row's request is in flight. */
  disabled?: boolean
}

/** How far below the trigger the menu sits, and how close to a viewport edge it may get. */
const GAP = 4
const MARGIN = 8

/**
 * A small menu of per-row actions behind one "Actions" button.
 *
 * Rendered into <body> at a fixed position rather than in the table cell: the
 * tables it serves scroll inside `.scroll-y`, whose overflow would clip a panel
 * positioned inside it at the card's edge, hiding the last rows' menus exactly
 * where a scrolled-down operator is looking. Being fixed, it cannot follow the
 * page when that scrolls, so any scroll or resize closes it instead of letting
 * it drift away from its row.
 *
 * Keyboard: Enter, Space and the arrow keys open it with the first (or last)
 * item focused; arrows move between items and wrap; Escape closes it and
 * returns focus to the trigger; Tab leaves it and closes it. A click anywhere
 * else closes it without moving focus. Disabled items keep their place and stay
 * focusable so the reason can be read and announced — an item that vanished
 * would leave an operator wondering whether it was a permission or the row.
 *
 * Not a dialog: it takes nothing from the shared Escape stack, and an item that
 * needs confirmation opens a ConfirmDialog after this has closed.
 */
export function RowActionsMenu<K extends string>({
  label,
  items,
  onSelect,
  triggerLabel = 'Actions',
  disabled = false,
}: RowActionsMenuProps<K>) {
  const menuId = useId()
  const triggerRef = useRef<HTMLButtonElement>(null)
  const menuRef = useRef<HTMLDivElement>(null)
  const [open, setOpen] = useState<null | { focus: 'first' | 'last' }>(null)
  const [position, setPosition] = useState<{ top: number; left: number } | null>(null)

  const close = useCallback((returnFocus: boolean) => {
    setOpen(null)
    setPosition(null)
    if (returnFocus) triggerRef.current?.focus()
  }, [])

  // Place the menu once it has a size: right-aligned under the trigger, flipped
  // above it when the viewport ends first, and never past the left edge.
  useLayoutEffect(() => {
    if (!open) return
    const trigger = triggerRef.current
    const menu = menuRef.current
    if (!trigger || !menu) return

    const anchor = trigger.getBoundingClientRect()
    const size = menu.getBoundingClientRect()

    let top = anchor.bottom + GAP
    if (top + size.height > window.innerHeight - MARGIN) {
      top = Math.max(MARGIN, anchor.top - GAP - size.height)
    }
    const left = Math.max(MARGIN, anchor.right - size.width)

    setPosition({ top, left })

    const focusables = menuItems(menu)
    const target = open.focus === 'first' ? focusables[0] : focusables[focusables.length - 1]
    target?.focus()
  }, [open])

  // Outside click, scroll and resize all close it. Scroll is listened for in
  // the capture phase because the interesting scroll is the table's own, which
  // never bubbles to the window.
  useEffect(() => {
    if (!open) return

    function onPointerDown(event: PointerEvent) {
      const target = event.target as Node
      if (menuRef.current?.contains(target) || triggerRef.current?.contains(target)) return
      close(false)
    }
    function onScrollOrResize() {
      close(false)
    }

    document.addEventListener('pointerdown', onPointerDown)
    window.addEventListener('scroll', onScrollOrResize, true)
    window.addEventListener('resize', onScrollOrResize)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      window.removeEventListener('scroll', onScrollOrResize, true)
      window.removeEventListener('resize', onScrollOrResize)
    }
  }, [open, close])

  function onTriggerKeyDown(event: KeyboardEvent<HTMLButtonElement>) {
    if (event.key === 'ArrowDown') {
      event.preventDefault()
      setOpen({ focus: 'first' })
    } else if (event.key === 'ArrowUp') {
      event.preventDefault()
      setOpen({ focus: 'last' })
    }
  }

  function onMenuKeyDown(event: KeyboardEvent<HTMLDivElement>) {
    const menu = menuRef.current
    if (!menu) return

    if (event.key === 'Escape') {
      event.preventDefault()
      // Stop here so a dialog further up the tree does not also close.
      event.stopPropagation()
      close(true)
      return
    }
    if (event.key === 'Tab') {
      // Focus goes back to the trigger first, so the browser's own Tab then
      // lands on the element after it (or before it, with Shift) instead of
      // starting over from <body> once the menu has unmounted.
      close(true)
      return
    }

    const focusables = menuItems(menu)
    const current = focusables.indexOf(document.activeElement as HTMLButtonElement)
    let next: number | null = null
    if (event.key === 'ArrowDown') next = current < 0 ? 0 : (current + 1) % focusables.length
    else if (event.key === 'ArrowUp') next = current <= 0 ? focusables.length - 1 : current - 1
    else if (event.key === 'Home') next = 0
    else if (event.key === 'End') next = focusables.length - 1

    if (next !== null) {
      event.preventDefault()
      focusables[next]?.focus()
    }
  }

  function choose(item: RowActionsMenuItem<K>) {
    if (!item.enabled) return
    close(true)
    onSelect(item.key)
  }

  return (
    <>
      <button
        ref={triggerRef}
        type="button"
        className="btn-sm"
        aria-haspopup="menu"
        aria-expanded={open !== null}
        aria-controls={open ? menuId : undefined}
        disabled={disabled}
        onClick={() => (open ? close(false) : setOpen({ focus: 'first' }))}
        onKeyDown={onTriggerKeyDown}
      >
        {triggerLabel}
        <Icon name="chevron-down" size={13} />
      </button>

      {open && createPortal(
        <div
          ref={menuRef}
          id={menuId}
          className="menu"
          role="menu"
          aria-label={label}
          // Measured before it is placed; invisible until then so it never
          // flashes at the corner of the viewport.
          style={position ? { top: position.top, left: position.left } : { top: 0, left: 0, visibility: 'hidden' }}
          onKeyDown={onMenuKeyDown}
        >
          {items.map((item) => (
            <button
              key={item.key}
              type="button"
              role="menuitem"
              className={`menu-item${item.destructive ? ' destructive' : ''}`}
              aria-disabled={item.enabled ? undefined : true}
              title={item.reason ?? undefined}
              onClick={() => choose(item)}
            >
              <span>{item.label}</span>
              {!item.enabled && item.reason && <span className="menu-item-reason">{item.reason}</span>}
            </button>
          ))}
        </div>,
        document.body,
      )}
    </>
  )
}

/** The menu's items in document order — all of them, disabled ones included. */
function menuItems(menu: HTMLElement): HTMLButtonElement[] {
  return Array.from(menu.querySelectorAll<HTMLButtonElement>('[role="menuitem"]'))
}
