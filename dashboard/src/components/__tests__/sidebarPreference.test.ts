import { describe, expect, it } from 'vitest'
import {
  browserPreferenceStorage,
  readSidebarCollapsed,
  SIDEBAR_PREFERENCE_KEY,
  toggleLabel,
  writeSidebarCollapsed,
  type PreferenceStorage,
} from '../sidebarPreference'

/**
 * The sidebar preference, tested where it can actually go wrong.
 *
 * The interesting cases are not "does a boolean round-trip" but what happens
 * when storage misbehaves: absent, throwing on read, throwing on write, or
 * holding a value this code did not put there. Every one of those has to end up
 * with a usable sidebar, because a navigation rail that fails to render over a
 * browser privacy setting would be a bad trade for remembering a preference.
 *
 * Pure with injected storage, so these exercise the real functions the
 * dashboard ships — no fake DOM, matching the rest of this suite.
 */

/** A working store, plus a record of what was written to it. */
function fakeStorage(initial?: string) {
  const written: string[] = []
  let value: string | null = initial ?? null

  const storage: PreferenceStorage = {
    getItem: (key) => (key === SIDEBAR_PREFERENCE_KEY ? value : null),
    setItem: (key, next) => {
      expect(key).toBe(SIDEBAR_PREFERENCE_KEY)
      written.push(next)
      value = next
    },
  }

  return { storage, written, current: () => value }
}

/** A store that throws, as a private window or blocked site data does. */
const throwingStorage: PreferenceStorage = {
  getItem: () => {
    throw new DOMException('access denied', 'SecurityError')
  },
  setItem: () => {
    throw new DOMException('quota exceeded', 'QuotaExceededError')
  },
}

describe('reading the preference', () => {
  it('defaults to expanded when nothing has been stored', () => {
    const { storage } = fakeStorage()

    expect(readSidebarCollapsed(storage)).toBe(false)
  })

  it('reads back a collapsed preference', () => {
    const { storage } = fakeStorage('collapsed')

    expect(readSidebarCollapsed(storage)).toBe(true)
  })

  it('reads back an expanded preference', () => {
    const { storage } = fakeStorage('expanded')

    expect(readSidebarCollapsed(storage)).toBe(false)
  })

  /**
   * A value we did not write is not evidence of intent.
   *
   * Covers a leftover from an older build and a hand-edited value. Anything
   * unrecognised resolves to expanded — the layout that shows the most, which
   * is the right answer when the preference is genuinely unknown.
   */
  it.each(['1', '0', 'true', 'false', 'COLLAPSED', '', 'null', '{"collapsed":true}'])(
    'treats the unrecognised value %j as expanded',
    (stored) => {
      const { storage } = fakeStorage(stored)

      expect(readSidebarCollapsed(storage)).toBe(false)
    },
  )

  it('survives storage that throws on read', () => {
    expect(readSidebarCollapsed(throwingStorage)).toBe(false)
  })

  it.each([null, undefined])('survives storage being %s', (storage) => {
    expect(readSidebarCollapsed(storage)).toBe(false)
  })
})

describe('writing the preference', () => {
  it('stores both states under the namespaced key', () => {
    const { storage, written } = fakeStorage()

    writeSidebarCollapsed(storage, true)
    writeSidebarCollapsed(storage, false)

    expect(written).toEqual(['collapsed', 'expanded'])
  })

  it('round-trips through a real store', () => {
    const { storage } = fakeStorage()

    writeSidebarCollapsed(storage, true)
    expect(readSidebarCollapsed(storage)).toBe(true)

    writeSidebarCollapsed(storage, false)
    expect(readSidebarCollapsed(storage)).toBe(false)
  })

  /**
   * A failed write is not worth an error.
   *
   * The sidebar has already collapsed on screen and the interaction succeeded;
   * the only thing lost is that it will not be remembered. Surfacing that would
   * be noise about something nobody asked for.
   */
  it('does not throw when the store refuses the write', () => {
    expect(() => writeSidebarCollapsed(throwingStorage, true)).not.toThrow()
  })

  it.each([null, undefined])('does nothing when storage is %s', (storage) => {
    expect(() => writeSidebarCollapsed(storage, true)).not.toThrow()
  })
})

describe('the toggle label', () => {
  /**
   * The label names the action, not the state.
   *
   * "Collapse sidebar" tells someone what pressing it will do; "Sidebar
   * expanded" tells them what they can already see. `aria-expanded` carries the
   * state, so the label does not have to.
   */
  it('describes what pressing the button will do', () => {
    expect(toggleLabel(false)).toBe('Collapse sidebar')
    expect(toggleLabel(true)).toBe('Expand sidebar')
  })
})

describe('the browser store', () => {
  it('returns something usable or null, and never throws', () => {
    expect(() => browserPreferenceStorage()).not.toThrow()

    const storage = browserPreferenceStorage()
    if (storage !== null) {
      expect(typeof storage.getItem).toBe('function')
      expect(typeof storage.setItem).toBe('function')
    }
  })
})
