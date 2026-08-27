import type { ElevationRow, LocalUserRow } from '../api/client'

/**
 * The decisions the elevation UI makes, kept out of the component so they can be
 * asserted directly.
 *
 * The important one is the separation between **authorization** and
 * **enforcement**. The elevation record says what the platform has permitted;
 * `DeviceLocalUser.isLocalAdministrator` says what Windows actually reports. They
 * can disagree — an expired elevation whose de-elevation failed leaves an account
 * still holding administrator rights — and a console that showed only one of them
 * would be lying in the case that matters most.
 */

/** Whether an elevation currently authorizes anything, judged from the clock. */
export function isLive(elevation: ElevationRow, now: Date): boolean {
  if (elevation.state !== 'Approved' && elevation.state !== 'Active') return false
  if (!elevation.expiresAt) return false
  return new Date(elevation.expiresAt).getTime() > now.getTime()
}

/**
 * What the endpoint is actually doing, as distinct from what was authorized.
 *
 * - `Applied` — authorized, and Windows reports the account as an administrator.
 * - `Pending` — authorized, but the endpoint has not applied it yet.
 * - `Drifted` — **not** authorized, yet Windows still reports administrator.
 * - `NotApplicable` — no live authorization and no rights: nothing to say.
 *
 * `Drifted` is the state worth designing for. It is what a failed de-elevation
 * looks like, and the platform must show it rather than reporting the elevation
 * as merely "Expired" and leaving an operator to assume the rights went away.
 */
export type ElevationEnforcement = 'Applied' | 'Pending' | 'Drifted' | 'NotApplicable'

export function describeEnforcement(
  elevation: ElevationRow,
  account: LocalUserRow | undefined,
  now: Date,
): ElevationEnforcement {
  const live = isLive(elevation, now)

  // No reported account means the endpoint has not told us anything about it,
  // which is not the same as the account being standard.
  if (!account) return live ? 'Pending' : 'NotApplicable'

  if (live) return account.isLocalAdministrator ? 'Applied' : 'Pending'

  // Authorization has ended. Still an administrator is drift — unless this
  // elevation never took effect in the first place, in which case the rights
  // belong to something else and are not this record's to explain.
  const everApplied = elevation.activatedAt !== null
  return account.isLocalAdministrator && everApplied ? 'Drifted' : 'NotApplicable'
}

/**
 * Whether an account may be offered as an elevation target.
 *
 * Three exclusions, each mirroring a refusal the server makes anyway — the UI
 * hides what it can as a courtesy, and the server remains the boundary.
 *
 * - The **built-in Administrator** (RID 500) is never offered. It already holds
 *   administrator rights, and the platform protects it from modification.
 * - An account that is **already an administrator** is never offered. Elevating
 *   it would do nothing, and the agent deliberately does not adopt such an
 *   account into its ledger — so ending the elevation would not lower rights the
 *   platform never granted. Offering it would imply otherwise.
 * - A **disabled** account is never offered, because nobody can sign in with it.
 */
export function isEligibleTarget(account: LocalUserRow): boolean {
  if (isBuiltInAdministrator(account.sid)) return false
  if (account.isLocalAdministrator) return false
  if (!account.enabled) return false
  return true
}

/** Why an account cannot be elevated, for the reader who expected to see it. */
export function ineligibilityReason(account: LocalUserRow): string | null {
  if (isBuiltInAdministrator(account.sid)) {
    return 'Built-in Administrator: protected by the platform and already an administrator.'
  }
  if (account.isLocalAdministrator) {
    return 'Already an administrator. Elevation grants temporary rights to standard users.'
  }
  if (!account.enabled) {
    return 'Disabled: nobody can sign in with this account.'
  }
  return null
}

/**
 * Matched on the RID, never the name.
 *
 * Renaming the built-in Administrator is a standard hardening step and the name
 * is localized, so a name-based rule would stop protecting it silently.
 */
export function isBuiltInAdministrator(sid: string): boolean {
  return /-500$/.test(sid.trim())
}

/**
 * Time left on a live elevation, floored.
 *
 * Floored so "12m left" always means at least twelve minutes. Rounding up would
 * let the console promise time the endpoint will not honour.
 */
export function remaining(expiresAt: string | null, now: Date): string {
  if (!expiresAt) return '—'

  const ms = new Date(expiresAt).getTime() - now.getTime()
  if (ms <= 0) return 'expired'

  const minutes = Math.floor(ms / 60000)
  if (minutes < 60) return `${minutes}m left`

  const hours = Math.floor(minutes / 60)
  return `${hours}h ${minutes % 60}m left`
}

/** The live elevation for an account, if any. */
export function liveElevationFor(
  elevations: ElevationRow[],
  sid: string,
  now: Date,
): ElevationRow | undefined {
  return elevations.find(
    (e) => e.targetSid.toLowerCase() === sid.toLowerCase() && isLive(e, now),
  )
}

/** A pending request awaiting a decision, if any. */
export function pendingElevationFor(
  elevations: ElevationRow[],
  sid: string,
): ElevationRow | undefined {
  return elevations.find(
    (e) => e.targetSid.toLowerCase() === sid.toLowerCase() && e.state === 'Requested',
  )
}
