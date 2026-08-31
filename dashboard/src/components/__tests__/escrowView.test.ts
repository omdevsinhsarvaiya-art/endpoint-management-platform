import { describe, expect, it } from 'vitest'
import {
  REVEAL_LIFETIME_MS,
  attemptFor,
  autoEscrowLabel,
  autoEscrowState,
  autoEscrowTone,
  canReset,
  describeAttempt,
  failureLabel,
  type EscrowAttemptRow,
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

/**
 * Automatic escrow, as the console presents it.
 *
 * The state that gets the most attention here is the one that is not a failure:
 * a device with no pinned sealing key. It reads as "unavailable, re-enrollment
 * required" rather than "not escrowed", because nothing is wrong with the machine
 * and telling an operator to investigate would waste their time.
 */
describe('automatic escrow state', () => {
  const VOL = VOLUME
  const PROT = '3f2504e0-4f89-11d3-9a0c-0305e82c3301'

  function attempt(overrides: Partial<EscrowAttemptRow> = {}): EscrowAttemptRow {
    return {
      id: 'a1',
      deviceId: 'd1',
      volumeDeviceIdentifier: VOL,
      keyProtectorId: PROT,
      state: 'Pending',
      attemptCount: 0,
      maxAttempts: 5,
      lastFailure: 'None',
      nextAttemptAt: null,
      lastAttemptAt: null,
      escrowedAt: null,
      ...overrides,
    }
  }

  it('reports an ineligible device as unavailable rather than as a failure', () => {
    expect(autoEscrowState([], VOL, PROT, false)).toBe('Unavailable')
    expect(autoEscrowLabel('Unavailable')).toContain('re-enrollment required')

    // Not a warning colour: nothing is wrong with the machine.
    expect(autoEscrowTone('Unavailable')).toBe('neutral')
  })

  it('distinguishes never-attempted from attempted-and-failed', () => {
    expect(autoEscrowState([], VOL, PROT, true)).toBe('NotEscrowed')
    expect(autoEscrowState([attempt({ state: 'Failed' })], VOL, PROT, true)).toBe('Failed')
  })

  /** Exhausted is worse than failed: nothing happens next without a person. */
  it('separates exhausted from failed, and escalates it', () => {
    expect(autoEscrowState([attempt({ state: 'RetryExhausted' })], VOL, PROT, true))
      .toBe('RetryExhausted')

    expect(autoEscrowTone('RetryExhausted')).toBe('crit')
    expect(autoEscrowTone('Failed')).toBe('warn')
  })

  it('reports a collected key as escrowed', () => {
    expect(autoEscrowState([attempt({ state: 'Escrowed' })], VOL, PROT, true)).toBe('Escrowed')
    expect(autoEscrowTone('Escrowed')).toBe('ok')
  })

  it('does not confuse one protector with another', () => {
    const other = '7c9e6679-7425-40de-944b-e07fc1f90ae7'

    expect(autoEscrowState([attempt({ state: 'Escrowed' })], VOL, other, true)).toBe('NotEscrowed')
    expect(attemptFor([attempt()], VOL, other)).toBeUndefined()
  })

  it('matches protector ids across brace and case differences', () => {
    expect(attemptFor([attempt()], VOL, `{${PROT.toUpperCase()}}`)?.id).toBe('a1')
  })

  /** Only a stopped protector can be re-armed; offering it otherwise is noise. */
  it('offers a reset only where there is something to re-arm', () => {
    expect(canReset('RetryExhausted')).toBe(true)
    expect(canReset('Failed')).toBe(true)
    expect(canReset('Escrowed')).toBe(false)
    expect(canReset('Pending')).toBe(false)
    expect(canReset('Unavailable')).toBe(false)
  })
})

describe('describing an automatic attempt', () => {
  const base: EscrowAttemptRow = {
    id: 'a1',
    deviceId: 'd1',
    volumeDeviceIdentifier: 'v',
    keyProtectorId: 'p',
    state: 'Failed',
    attemptCount: 2,
    maxAttempts: 5,
    lastFailure: 'WindowsRefused',
    nextAttemptAt: '2026-08-31T10:00:00Z',
    lastAttemptAt: '2026-08-31T09:00:00Z',
    escrowedAt: null,
  }

  it('says where the schedule has got to and what happens next', () => {
    const text = describeAttempt(base)

    expect(text).toContain('2 of 5')
    expect(text).toContain('Windows refused')
  })

  it('says plainly that an exhausted protector will not resume on its own', () => {
    expect(describeAttempt({ ...base, state: 'RetryExhausted' }))
      .toContain('will not resume until it is reset')
  })

  it('translates every failure category into something readable', () => {
    expect(failureLabel('FingerprintMismatch')).toContain('sealing key')
    expect(failureLabel('ProtectorGone')).toContain('no longer exists')
    expect(failureLabel('something-new')).toContain('not recorded')
  })

  /**
   * The description is built from counts, categories and timestamps, so there is
   * no path by which key material could reach it.
   */
  it('never renders anything shaped like a recovery password', () => {
    for (const state of ['Pending', 'Failed', 'RetryExhausted', 'Escrowed']) {
      expect(describeAttempt({ ...base, state })).not.toMatch(/\d{6}-\d{6}/)
    }
  })
})
