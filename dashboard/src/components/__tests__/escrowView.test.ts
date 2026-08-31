import { describe, expect, it } from 'vitest'
import {
  REVEAL_LIFETIME_MS,
  activeAutomaticEscrowFor,
  automaticEscrows,
  manualEscrows,
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
    origin: 'Manual',
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

/**
 * Escrow origin decides which card a record belongs to.
 *
 * The bug this pins down: the manual card was built from every escrow for a
 * volume, so a key the endpoint had collected appeared under "Manual" as
 * "Recovery key escrowed" -- claiming an administrator had vouched for it, and
 * offering Replace and Delete for a record nobody owns. The automatic card had
 * been reporting the same key correctly at the same time, so the console showed
 * one key twice under two different stories.
 *
 * The two mechanisms carry different trust and different controls, so they are
 * separated at the data level rather than by how they are drawn.
 */
describe('escrow origin routing', () => {
  const VOL = VOLUME
  const PROT = '3f2504e0-4f89-11d3-9a0c-0305e82c3301'

  const manual = (over: Partial<EscrowRow> = {}): EscrowRow => ({
    id: 'manual-1',
    volumeDeviceIdentifier: VOL,
    keyProtectorId: PROT,
    driveLetter: 'C:',
    isActive: true,
    origin: 'Manual',
    escrowedAt: '2026-08-28T10:00:00Z',
    escrowedBy: 'admin@company.local',
    supersededAt: null,
    revealedCount: 0,
    lastRevealedAt: null,
    ...over,
  })

  const automatic = (over: Partial<EscrowRow> = {}): EscrowRow =>
    manual({
      id: 'auto-1',
      origin: 'Automatic',
      escrowedBy: 'OMDEVSINH-TECHS (agent)',
      ...over,
    })

  /** 1. The regression itself. */
  it('an automatic escrow never reaches the manual card', () => {
    const rows = [automatic()]

    expect(manualEscrows(rows)).toHaveLength(0)
    expect(activeEscrowFor(rows, VOL)).toBeUndefined()

    // ...and the manual badge must not claim a key is filed.
    expect(escrowStatus(rows, VOL)).toBe('NotEscrowed')
    expect(escrowStatusLabel('NotEscrowed')).toContain('No recovery key')
  })

  /**
   * 2. Replace and Delete are driven by the manual record. With none present,
   * there is nothing for those controls to act on.
   */
  it('an automatic escrow offers nothing for manual controls to target', () => {
    const rows = [automatic()]

    // Both manual mutations take the record returned here.
    expect(activeEscrowFor(rows, VOL)).toBeUndefined()

    // The automatic path still finds it, so it is not simply lost.
    expect(activeAutomaticEscrowFor(rows, VOL, PROT)?.id).toBe('auto-1')
  })

  /** 3. */
  it('a manual escrow appears only in the manual card', () => {
    const rows = [manual()]

    expect(activeEscrowFor(rows, VOL)?.id).toBe('manual-1')
    expect(escrowStatus(rows, VOL)).toBe('Escrowed')

    expect(automaticEscrows(rows)).toHaveLength(0)
    expect(activeAutomaticEscrowFor(rows, VOL, PROT)).toBeUndefined()
  })

  /** 4. Both origins for one protector: two records, two cards, no duplication. */
  it('when both exist each appears in exactly one card', () => {
    const rows = [manual(), automatic()]

    const inManual = activeEscrowFor(rows, VOL)
    const inAuto = activeAutomaticEscrowFor(rows, VOL, PROT)

    expect(inManual?.id).toBe('manual-1')
    expect(inAuto?.id).toBe('auto-1')

    // Distinct records, not one record rendered twice.
    expect(inManual?.id).not.toBe(inAuto?.id)
    expect(manualEscrows(rows)).toHaveLength(1)
    expect(automaticEscrows(rows)).toHaveLength(1)
  })

  /** 5. */
  it('an existing automatic escrow stays visible as escrowed automatically', () => {
    const rows = [automatic()]
    const found = activeAutomaticEscrowFor(rows, VOL, PROT)

    expect(found).toBeDefined()
    expect(found!.escrowedBy).toContain('(agent)')
    expect(autoEscrowLabel('Escrowed')).toBe('Escrowed automatically')
    expect(autoEscrowTone('Escrowed')).toBe('ok')
  })

  /** A collected key for one protector must not satisfy another. */
  it('does not match an automatic escrow across protectors', () => {
    const rows = [automatic()]

    expect(activeAutomaticEscrowFor(rows, VOL, '7c9e6679-7425-40de-944b-e07fc1f90ae7'))
      .toBeUndefined()
  })

  /** Superseded automatic records are history, not the current key. */
  it('ignores a superseded automatic escrow', () => {
    const rows = [automatic({ isActive: false, supersededAt: '2026-08-31T00:00:00Z' })]

    expect(activeAutomaticEscrowFor(rows, VOL, PROT)).toBeUndefined()
  })

  /** Neither card may render anything shaped like a key. */
  it('no rendered summary contains a recovery-password shape', () => {
    for (const row of [manual(), automatic()]) {
      expect(describeEscrow(row)).not.toMatch(/\d{6}-\d{6}/)
    }
  })
})
