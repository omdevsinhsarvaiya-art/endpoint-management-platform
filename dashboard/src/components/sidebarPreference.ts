/**
 * Whether the administrator has collapsed the sidebar, remembered between
 * visits.
 *
 * Kept as a separate module rather than inline in the shell for one reason:
 * every read and write here has to survive storage being unavailable, and that
 * is worth testing directly. `localStorage` is not merely absent in some
 * contexts — in a private window, or with site data blocked, the accessor
 * itself throws on access rather than returning null. A navigation sidebar that
 * refuses to render because a browser setting is unusual would be a poor trade
 * for remembering a preference.
 *
 * So every failure here resolves to "expanded". That is the current layout and
 * the one that shows the most information, which makes it the right answer when
 * we genuinely do not know what the person wanted.
 */

/** Namespaced so it cannot collide with anything else on the origin. */
export const SIDEBAR_PREFERENCE_KEY = 'endpoint-platform.sidebar'

/**
 * Words rather than `"1"` / `"0"`.
 *
 * Somebody reading this value in devtools while diagnosing a layout complaint
 * should not have to guess which way round the flag runs.
 */
const COLLAPSED = 'collapsed'
const EXPANDED = 'expanded'

/**
 * The slice of the Storage API this needs.
 *
 * Narrow on purpose: it lets a test supply a plain object, including one whose
 * accessors throw, without standing up a whole fake `Storage`.
 */
export interface PreferenceStorage {
  getItem(key: string): string | null
  setItem(key: string, value: string): void
}

/**
 * Reads the stored preference, defaulting to expanded.
 *
 * Returns false — expanded — for a missing value, an unreadable store, and any
 * value we did not write. The last case matters for a value left behind by an
 * older build or edited by hand: an unrecognised string is not evidence that
 * the person wanted the sidebar collapsed.
 */
export function readSidebarCollapsed(storage: PreferenceStorage | null | undefined): boolean {
  if (!storage) {
    return false
  }

  try {
    return storage.getItem(SIDEBAR_PREFERENCE_KEY) === COLLAPSED
  } catch {
    // Blocked or unavailable storage. See the note at the top of the file.
    return false
  }
}

/**
 * Records the preference, and does nothing at all if it cannot.
 *
 * Failing silently is correct here in a way it rarely is: the person has
 * already seen the sidebar collapse, the interaction succeeded, and the only
 * thing lost is that it will not be remembered next time. Surfacing an error
 * for that would be noise about something they did not ask for.
 */
export function writeSidebarCollapsed(
  storage: PreferenceStorage | null | undefined,
  collapsed: boolean,
): void {
  if (!storage) {
    return
  }

  try {
    storage.setItem(SIDEBAR_PREFERENCE_KEY, collapsed ? COLLAPSED : EXPANDED)
  } catch {
    // See above.
  }
}

/**
 * The browser's own storage, or null where touching it would throw.
 *
 * The probe is deliberate. Some browsers expose `localStorage` and then throw
 * on first access, so a truthiness check is not enough to know it is usable.
 */
export function browserPreferenceStorage(): PreferenceStorage | null {
  try {
    return globalThis.localStorage ?? null
  } catch {
    return null
  }
}

/** The accessible label for the toggle, which names the action, not the state. */
export function toggleLabel(collapsed: boolean): string {
  return collapsed ? 'Expand sidebar' : 'Collapse sidebar'
}
