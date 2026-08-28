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
  const forVolume = rows.filter((r) => r.volumeDeviceIdentifier === volumeDeviceIdentifier)

  if (forVolume.some((r) => r.isActive)) return 'Escrowed'
  return forVolume.length > 0 ? 'Superseded' : 'NotEscrowed'
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
  return rows.find((r) => r.volumeDeviceIdentifier === volumeDeviceIdentifier && r.isActive)
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
