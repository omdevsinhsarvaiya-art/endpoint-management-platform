import { describe, expect, it } from 'vitest'
import {
  REVEAL_LIFETIME_MS,
  activeEscrowFor,
  describeEscrow,
  escrowStatus,
  escrowStatusLabel,
  escrowStatusTone,
  formatRecoveryPasswordInput,
  hasExpired,
  looksLikeRecoveryPassword,
  secondsRemaining,
} from '../../pages/escrowView'
import type { EscrowRow } from '../../api/client'

/**
 * The console's half of recovery-key escrow.
 *
 * Two things carry weight here. **A revealed key has a bounded lifetime** — it
 * lives in component state and is dropped after a minute, so an operator who
 * walks away does not leave a disk encryption key on a screen behind them.
 * And **nothing in this module can render a key it was not explicitly given**:
 * the status, the summary and the labels are all derived from metadata, so the
 * panel can describe an escrow completely without the key existing in the page.
 */

const VOLUME = '\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\'
const OTHER = '\\\\?\\Volume{22222222-2222-2222-2222-222222222222}\\'

function escrow(overrides: Partial<EscrowRow> = {}): EscrowRow {
  return {
    id: 'e1',
    volumeDeviceIdentifier: VOLUME,
    keyProtectorId: '3f2504e0-4f89-11d3-9a0c-0305e82c3301',
    driveLetter: 'C:',
    isActive: true,
    escrowedAt: '2026-08-28T10:00:00Z',
    escrowedBy: 'admin@company.local',
    supersededAt: null,
    revealedCount: 0,
    lastRevealedAt: null,
    ...overrides,
  }
}

describe('escrow status', () => {
  it('reports a volume with a live key as escrowed', () => {
    expect(escrowStatus([escrow()], VOLUME)).toBe('Escrowed')
    expect(escrowStatusTone('Escrowed')).toBe('ok')
    expect(escrowStatusLabel('Escrowed')).toContain('escrowed')
  })

  /**
   * The state that matters operationally: a machine whose key was never filed
   * has no recovery path, so it reads as a warning rather than as neutral.
   */
  it('reports a volume with no key as a warning', () => {
    expect(escrowStatus([], VOLUME)).toBe('NotEscrowed')
    expect(escrowStatusTone('NotEscrowed')).toBe('warn')
  })

  /**
   * Superseded records are kept deliberately — a machine restored from an older
   * backup may need the key that was current then — but a volume with only
   * superseded keys has no current one, and must not read as covered.
   */
  it('distinguishes only-superseded from escrowed', () => {
    const superseded = [escrow({ isActive: false, supersededAt: '2026-08-28T11:00:00Z' })]

    expect(escrowStatus(superseded, VOLUME)).toBe('Superseded')
    expect(escrowStatus(superseded, VOLUME)).not.toBe('Escrowed')
  })

  it('does not confuse one volume with another', () => {
    expect(escrowStatus([escrow()], OTHER)).toBe('NotEscrowed')
    expect(activeEscrowFor([escrow()], OTHER)).toBeUndefined()
  })

  it('finds only the live escrow for a volume', () => {
    const rows = [
      escrow({ id: 'old', isActive: false }),
      escrow({ id: 'current', isActive: true }),
    ]

    expect(activeEscrowFor(rows, VOLUME)?.id).toBe('current')
  })
})

describe('describing an escrow without its key', () => {
  it('says who filed it, when, and how often it has been read', () => {
    const text = describeEscrow(escrow({ revealedCount: 3 }))

    expect(text).toContain('admin@company.local')
    expect(text).toContain('revealed 3 times')
  })

  it('distinguishes never-revealed from revealed once', () => {
    expect(describeEscrow(escrow({ revealedCount: 0 }))).toContain('never revealed')
    expect(describeEscrow(escrow({ revealedCount: 1 }))).toContain('revealed once')
  })

  /**
   * The summary is built entirely from metadata, so there is no path by which a
   * key could reach it even if one were present on the row.
   */
  it('contains nothing shaped like a recovery password', () => {
    expect(describeEscrow(escrow())).not.toMatch(/\d{6}-\d{6}/)
  })
})

describe('recovery password entry', () => {
  const valid = Array.from({ length: 8 }, () => '011000').join('-')

  it('accepts a well-formed password', () => {
    expect(looksLikeRecoveryPassword(valid)).toBe(true)
  })

  /**
   * Mirrors the server rule rather than approximating it. A client check that
   * disagreed with the server would either block a valid key or promise that an
   * invalid one will be accepted.
   */
  it('rejects a group that fails the checksum', () => {
    expect(looksLikeRecoveryPassword(`011001-${Array.from({ length: 7 }, () => '011000').join('-')}`))
      .toBe(false)
  })

  it.each([
    '',
    '011000',
    '011000-011000-011000-011000-011000-011000-011000',
    '01100a-011000-011000-011000-011000-011000-011000-011000',
    '0'.repeat(48),
  ])('rejects %j', (candidate) => {
    expect(looksLikeRecoveryPassword(candidate)).toBe(false)
  })

  it('groups digits as they are typed', () => {
    expect(formatRecoveryPasswordInput('011000011000')).toBe('011000-011000')
    expect(formatRecoveryPasswordInput('011000-011000')).toBe('011000-011000')
  })

  it('ignores non-digits and never grows past a full key', () => {
    expect(formatRecoveryPasswordInput('abc011000def011000')).toBe('011000-011000')
    expect(formatRecoveryPasswordInput('9'.repeat(80)).replace(/-/g, '')).toHaveLength(48)
  })
})

describe('revealed key lifetime', () => {
  const revealedAt = 1_000_000

  it('is bounded to a minute', () => {
    expect(REVEAL_LIFETIME_MS).toBe(60_000)
  })

  it('counts down while the key is on screen', () => {
    expect(secondsRemaining(revealedAt, revealedAt)).toBe(60)
    expect(secondsRemaining(revealedAt, revealedAt + 30_000)).toBe(30)
  })

  /**
   * The assertion that matters: once the lifetime is up the key is gone, and the
   * countdown never reports time that is not there.
   */
  it('expires exactly at the deadline and never reports negative time', () => {
    expect(hasExpired(revealedAt, revealedAt + REVEAL_LIFETIME_MS - 1)).toBe(false)
    expect(hasExpired(revealedAt, revealedAt + REVEAL_LIFETIME_MS)).toBe(true)

    expect(secondsRemaining(revealedAt, revealedAt + REVEAL_LIFETIME_MS)).toBe(0)
    expect(secondsRemaining(revealedAt, revealedAt + 999_999)).toBe(0)
  })
})
