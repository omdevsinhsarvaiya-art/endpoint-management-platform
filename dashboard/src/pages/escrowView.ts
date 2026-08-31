import type { EscrowRow } from '../api/client'

/**
 * Presentation logic for recovery-key escrow, kept out of the component so the
 * rules that protect the key can be asserted directly.
 *
 * The one that matters most is {@link REVEAL_LIFETIME_MS}: a revealed key is
 * held in component state for a bounded time and then dropped. Everything else
 * here exists so the panel can describe an escrow without ever needing the key
 * itself.
 */

/**
 * How long a revealed key stays on screen.
 *
 * Short deliberately. The key is in browser memory for exactly as long as
 * somebody is reading it, and an operator who walks away does not leave a disk
 * encryption key on a screen behind them. Long enough to read a 48-digit number
 * aloud or copy it; not long enough to forget it is there.
 */
export const REVEAL_LIFETIME_MS = 60_000

/** How a volume's escrow state should read in the panel. */
export type EscrowStatus = 'Escrowed' | 'NotEscrowed' | 'Superseded'

export function escrowStatus(rows: EscrowRow[], volumeDeviceIdentifier: string): EscrowStatus {
  const forVolume = manualEscrows(rows).filter(
    (r) => r.volumeDeviceIdentifier === volumeDeviceIdentifier,
  )

  if (forVolume.some((r) => r.isActive)) return 'Escrowed'
  return forVolume.length > 0 ? 'Superseded' : 'NotEscrowed'
}

/**
 * Only the escrows an administrator filed.
 *
 * The manual card is built from this rather than from every escrow, because an
 * automatically collected key has no administrator behind it: showing it there
 * claimed someone had vouched for it, and offered Replace and Delete for a
 * record nobody owns.
 */
export function manualEscrows(rows: EscrowRow[]): EscrowRow[] {
  return rows.filter((r) => r.origin === 'Manual')
}

/** Only the escrows the endpoint collected. */
export function automaticEscrows(rows: EscrowRow[]): EscrowRow[] {
  return rows.filter((r) => r.origin === 'Automatic')
}

export function escrowStatusTone(status: EscrowStatus): 'ok' | 'warn' | 'neutral' {
  switch (status) {
    case 'Escrowed':
      return 'ok'
    case 'Superseded':
      return 'neutral'
    default:
      return 'warn'
  }
}

export function escrowStatusLabel(status: EscrowStatus): string {
  switch (status) {
    case 'Escrowed':
      return 'Recovery key escrowed'
    case 'Superseded':
      return 'Only superseded keys'
    default:
      return 'No recovery key escrowed'
  }
}

/** The live escrow for a volume, if any. */
export function activeEscrowFor(
  rows: EscrowRow[],
  volumeDeviceIdentifier: string,
): EscrowRow | undefined {
  return manualEscrows(rows).find(
    (r) => r.volumeDeviceIdentifier === volumeDeviceIdentifier && r.isActive,
  )
}

/**
 * The live automatically collected escrow for a protector, if one exists.
 *
 * Matched on protector as well as volume: a volume can carry several protectors,
 * and a key collected for one does not unlock another.
 */
export function activeAutomaticEscrowFor(
  rows: EscrowRow[],
  volumeDeviceIdentifier: string,
  keyProtectorId: string,
): EscrowRow | undefined {
  return automaticEscrows(rows).find(
    (r) =>
      r.volumeDeviceIdentifier === volumeDeviceIdentifier &&
      r.isActive &&
      sameProtector(r.keyProtectorId, keyProtectorId),
  )
}

/**
 * Client-side format check, for immediate feedback only.
 *
 * The server validates independently and is the control: this exists so a typo
 * is caught before a round trip, not so the server can trust the client. It
 * deliberately mirrors the server rule -- eight groups of six digits, each a
 * multiple of 11 with a quotient inside 16 bits -- so the two do not disagree
 * and confuse an operator.
 */
export function looksLikeRecoveryPassword(candidate: string): boolean {
  const groups = candidate.trim().split('-')

  if (groups.length !== 8) return false

  return groups.every((group) => {
    if (!/^\d{6}$/.test(group)) return false

    const value = Number(group)
    return value % 11 === 0 && value / 11 <= 65535
  })
}

/**
 * Groups digits into the canonical hyphenated form as somebody types.
 *
 * Formatting only. It never stores, echoes or transmits what it is given beyond
 * returning the reformatted string to the input it came from.
 */
export function formatRecoveryPasswordInput(raw: string): string {
  const digits = raw.replace(/\D/g, '').slice(0, 48)

  const groups: string[] = []
  for (let i = 0; i < digits.length; i += 6) {
    groups.push(digits.slice(i, i + 6))
  }

  return groups.join('-')
}

/** Seconds left before a revealed key is dropped from memory. */
export function secondsRemaining(revealedAt: number, now: number): number {
  const remaining = Math.ceil((revealedAt + REVEAL_LIFETIME_MS - now) / 1000)
  return remaining > 0 ? remaining : 0
}

export function hasExpired(revealedAt: number, now: number): boolean {
  return now - revealedAt >= REVEAL_LIFETIME_MS
}

/**
 * How an escrow record should be summarised without the key.
 *
 * Everything a reader needs to decide whether to reveal -- who filed it, when,
 * and how often it has been read -- and nothing that would help them skip the
 * reveal. The reveal count is shown because a key nobody has ever needed and a
 * key read fifteen times are different situations.
 */
export function describeEscrow(row: EscrowRow): string {
  const when = new Date(row.escrowedAt).toLocaleString()
  const reveals =
    row.revealedCount === 0
      ? 'never revealed'
      : row.revealedCount === 1
        ? 'revealed once'
        : `revealed ${row.revealedCount} times`

  return `Escrowed by ${row.escrowedBy} on ${when} — ${reveals}`
}


// ---------------------------------------------------------------- automatic

/**
 * Where automatic collection has got to for one protector.
 *
 * `Unavailable` is not a server state -- it is derived from the device having no
 * pinned sealing key, which is every device enrolled before automatic escrow
 * existed. It reads differently from a failure on purpose: nothing is wrong with
 * the machine, it simply has to re-enroll before it can participate.
 */
export type AutoEscrowState =
  | 'NotEscrowed'
  | 'Pending'
  | 'Escrowed'
  | 'Failed'
  | 'RetryExhausted'
  | 'Unavailable'

export interface EscrowAttemptRow {
  id: string
  deviceId: string
  volumeDeviceIdentifier: string
  keyProtectorId: string
  state: string
  attemptCount: number
  maxAttempts: number
  lastFailure: string
  nextAttemptAt: string | null
  lastAttemptAt: string | null
  escrowedAt: string | null
}

/** The automatic state for one protector, given what the server reported. */
export function autoEscrowState(
  attempts: EscrowAttemptRow[],
  volumeDeviceIdentifier: string,
  keyProtectorId: string,
  eligible: boolean,
): AutoEscrowState {
  // Checked before anything else: an ineligible device has no meaningful
  // per-protector state, and showing "not escrowed" would imply it was going to be.
  if (!eligible) return 'Unavailable'

  const match = attempts.find(
    (a) =>
      a.volumeDeviceIdentifier === volumeDeviceIdentifier &&
      sameProtector(a.keyProtectorId, keyProtectorId),
  )

  if (!match) return 'NotEscrowed'

  switch (match.state) {
    case 'Escrowed':
      return 'Escrowed'
    case 'Failed':
      return 'Failed'
    case 'RetryExhausted':
      return 'RetryExhausted'
    default:
      return 'Pending'
  }
}

export function autoEscrowLabel(state: AutoEscrowState): string {
  switch (state) {
    case 'Escrowed':
      return 'Escrowed automatically'
    case 'Pending':
      return 'Collection pending'
    case 'Failed':
      return 'Collection failed'
    case 'RetryExhausted':
      return 'Retry exhausted'
    case 'Unavailable':
      return 'Automatic escrow unavailable — re-enrollment required'
    default:
      return 'Not escrowed automatically'
  }
}

export function autoEscrowTone(state: AutoEscrowState): 'ok' | 'warn' | 'crit' | 'neutral' {
  switch (state) {
    case 'Escrowed':
      return 'ok'
    case 'Pending':
      return 'neutral'
    case 'RetryExhausted':
      // Distinct from Failed: nothing further happens without an administrator.
      return 'crit'
    case 'Failed':
      return 'warn'
    case 'Unavailable':
      return 'neutral'
    default:
      return 'warn'
  }
}

/**
 * How an attempt reads to an operator deciding whether to intervene.
 *
 * Deliberately says what happens next rather than only what happened, because the
 * question in front of someone looking at this is "do I need to do something".
 */
export function describeAttempt(attempt: EscrowAttemptRow): string {
  if (attempt.state === 'Escrowed') {
    return attempt.escrowedAt
      ? `Collected automatically on ${new Date(attempt.escrowedAt).toLocaleString()}`
      : 'Collected automatically'
  }

  if (attempt.state === 'RetryExhausted') {
    return `Stopped after ${attempt.attemptCount} attempts (${failureLabel(attempt.lastFailure)}). ` +
      'Automatic collection will not resume until it is reset.'
  }

  if (attempt.state === 'Failed') {
    const next = attempt.nextAttemptAt
      ? `retrying ${new Date(attempt.nextAttemptAt).toLocaleString()}`
      : 'retry scheduled'

    return `Attempt ${attempt.attemptCount} of ${attempt.maxAttempts} failed ` +
      `(${failureLabel(attempt.lastFailure)}); ${next}.`
  }

  return 'Waiting for the endpoint to collect this key.'
}

/** Plain-English failure categories. The server never sends a message, only a category. */
export function failureLabel(category: string): string {
  switch (category) {
    case 'WindowsRefused':
      return 'Windows refused the request'
    case 'MalformedPassword':
      return 'the value returned was not a valid recovery password'
    case 'FingerprintMismatch':
      return 'the sealing key did not match this device'
    case 'NotEligible':
      return 'the device is not eligible'
    case 'SealingFailed':
      return 'sealing failed on the endpoint'
    case 'UploadFailed':
      return 'the upload did not complete'
    case 'ProtectorGone':
      return 'the protector no longer exists'
    default:
      return 'reason not recorded'
  }
}

/** Whether an operator can re-arm this protector. */
export function canReset(state: AutoEscrowState): boolean {
  return state === 'RetryExhausted' || state === 'Failed'
}

function sameProtector(left: string, right: string): boolean {
  return normalise(left) === normalise(right)
}

function normalise(value: string): string {
  return value.trim().replace(/^\{|\}$/g, '').toLowerCase()
}

/** The attempt row for one protector, if the server has recorded one. */
export function attemptFor(
  attempts: EscrowAttemptRow[],
  volumeDeviceIdentifier: string,
  keyProtectorId: string,
): EscrowAttemptRow | undefined {
  return attempts.find(
    (a) =>
      a.volumeDeviceIdentifier === volumeDeviceIdentifier &&
      sameProtector(a.keyProtectorId, keyProtectorId),
  )
}
