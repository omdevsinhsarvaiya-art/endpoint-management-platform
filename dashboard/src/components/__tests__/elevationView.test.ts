import { describe, expect, it } from 'vitest'
import {
  describeEnforcement,
  ineligibilityReason,
  isBuiltInAdministrator,
  isEligibleTarget,
  isLive,
  liveElevationFor,
  pendingElevationFor,
  remaining,
} from '../../pages/elevationView'
import type { ElevationRow, LocalUserRow } from '../../api/client'

/**
 * The decisions the elevation console makes.
 *
 * Two of these carry real weight. **Eligibility** decides which accounts are
 * offered as targets, and getting it wrong would present the built-in
 * Administrator or an existing administrator as elevatable — the second being
 * worse, because the agent deliberately does not adopt an existing
 * administrator, so ending such an "elevation" would not lower anything and the
 * console would have implied otherwise.
 *
 * **Enforcement** decides whether the reader is told that an account is still an
 * administrator after its authorization ended. A console that reported only
 * "Expired" would hide exactly the failure an operator needs to act on.
 */

const NOW = new Date('2026-08-28T12:00:00Z')
const MACHINE = 'S-1-5-21-1-2-3'

function account(overrides: Partial<LocalUserRow> = {}): LocalUserRow {
  return {
    sid: `${MACHINE}-1001`,
    name: 'sarah',
    fullName: null,
    description: null,
    enabled: true,
    passwordRequired: true,
    passwordExpires: true,
    lastLogon: null,
    isLocalAdministrator: false,
    collectedAt: NOW.toISOString(),
    ...overrides,
  }
}

function elevation(overrides: Partial<ElevationRow> = {}): ElevationRow {
  return {
    id: 'e1',
    deviceId: 'd1',
    targetSid: `${MACHINE}-1001`,
    targetUsername: 'sarah',
    state: 'Active',
    isLive: true,
    justification: 'Signed vendor driver.',
    requestedAt: '2026-08-28T11:00:00Z',
    requestedBy: 'admin@company.local',
    approvedAt: '2026-08-28T11:00:00Z',
    approvedBy: 'admin@company.local',
    activatedAt: '2026-08-28T11:01:00Z',
    expiresAt: '2026-08-28T13:00:00Z',
    revokedAt: null,
    decisionNote: null,
    failureReason: null,
    ...overrides,
  }
}

describe('liveness', () => {
  it('is judged from the clock, not from the label alone', () => {
    const e = elevation({ expiresAt: '2026-08-28T11:59:59Z' })

    // Still labelled Active because nothing has swept it, and conferring nothing.
    expect(e.state).toBe('Active')
    expect(isLive(e, NOW)).toBe(false)
  })

  it('ends exactly at the deadline rather than a moment after', () => {
    const e = elevation({ expiresAt: NOW.toISOString() })
    expect(isLive(e, NOW)).toBe(false)
    expect(isLive(elevation({ expiresAt: '2026-08-28T12:00:01Z' }), NOW)).toBe(true)
  })

  it.each(['Requested', 'Rejected', 'Expired', 'Revoked', 'Failed'] as const)(
    'is false for %s regardless of the deadline',
    (state) => {
      expect(isLive(elevation({ state, expiresAt: '2027-01-01T00:00:00Z' }), NOW)).toBe(false)
    },
  )
})

describe('eligibility', () => {
  it('offers an enabled standard account', () => {
    expect(isEligibleTarget(account())).toBe(true)
  })

  /**
   * Matched on the RID, never the name: renaming the built-in Administrator is
   * standard hardening, and a name-based rule would stop protecting it silently.
   */
  it.each([`${MACHINE}-500`, 's-1-5-21-9-9-9-500'])('never offers RID 500 (%s)', (sid) => {
    expect(isBuiltInAdministrator(sid)).toBe(true)
    expect(isEligibleTarget(account({ sid, name: 'RenamedRoot' }))).toBe(false)
  })

  /**
   * An existing administrator is never offered.
   *
   * The agent does not adopt an account that was already an administrator, so
   * ending such an elevation would lower nothing. Offering it would promise a
   * revocation the platform will not perform.
   */
  it('never offers an account that is already an administrator', () => {
    expect(isEligibleTarget(account({ isLocalAdministrator: true }))).toBe(false)
    expect(ineligibilityReason(account({ isLocalAdministrator: true }))).toContain(
      'Already an administrator',
    )
  })

  it('never offers a disabled account', () => {
    expect(isEligibleTarget(account({ enabled: false }))).toBe(false)
  })

  it('explains why an ineligible account is missing from the list', () => {
    expect(ineligibilityReason(account({ sid: `${MACHINE}-500` }))).toContain('Built-in')
    expect(ineligibilityReason(account({ enabled: false }))).toContain('Disabled')
    expect(ineligibilityReason(account())).toBeNull()
  })
})

describe('authorization versus enforcement', () => {
  it('reports Applied when the endpoint confirms the rights', () => {
    expect(describeEnforcement(elevation(), account({ isLocalAdministrator: true }), NOW))
      .toBe('Applied')
  })

  it('reports Pending while the endpoint has not applied it', () => {
    expect(describeEnforcement(elevation(), account({ isLocalAdministrator: false }), NOW))
      .toBe('Pending')
  })

  it('reports Pending when the endpoint has said nothing about the account', () => {
    expect(describeEnforcement(elevation(), undefined, NOW)).toBe('Pending')
  })

  /**
   * The case the whole separation exists for.
   *
   * The authorization ended and the account is still an administrator, which
   * means de-elevation failed. Reporting only "Expired" would let an operator
   * assume the rights went away.
   */
  it('reports Drifted when an ended elevation left the account elevated', () => {
    const expired = elevation({ state: 'Expired', isLive: false, expiresAt: '2026-08-28T11:00:00Z' })

    expect(describeEnforcement(expired, account({ isLocalAdministrator: true }), NOW))
      .toBe('Drifted')
  })

  it('reports Drifted after a revoke that did not take effect', () => {
    const revoked = elevation({
      state: 'Revoked',
      isLive: false,
      expiresAt: '2026-08-28T11:30:00Z',
      revokedAt: '2026-08-28T11:30:00Z',
    })

    expect(describeEnforcement(revoked, account({ isLocalAdministrator: true }), NOW))
      .toBe('Drifted')
  })

  /**
   * An elevation that never took effect does not claim credit for rights it
   * never granted.
   */
  it('does not report Drifted for an elevation that was never applied', () => {
    const neverApplied = elevation({
      state: 'Expired',
      isLive: false,
      activatedAt: null,
      expiresAt: '2026-08-28T11:00:00Z',
    })

    expect(describeEnforcement(neverApplied, account({ isLocalAdministrator: true }), NOW))
      .toBe('NotApplicable')
  })

  it('says nothing when the elevation ended and the account is standard', () => {
    const expired = elevation({ state: 'Expired', isLive: false, expiresAt: '2026-08-28T11:00:00Z' })

    expect(describeEnforcement(expired, account({ isLocalAdministrator: false }), NOW))
      .toBe('NotApplicable')
  })
})

describe('remaining time', () => {
  it('is floored, so the stated time is always available', () => {
    expect(remaining('2026-08-28T12:12:59Z', NOW)).toBe('12m left')
    expect(remaining('2026-08-28T14:30:30Z', NOW)).toBe('2h 30m left')
  })

  it.each([null, '2026-08-28T11:59:59Z'])('handles %s without pretending time is left', (value) => {
    expect(remaining(value, NOW)).toMatch(/—|expired/)
  })
})

describe('finding an account\'s elevation', () => {
  it('matches the SID case-insensitively, as Windows does', () => {
    const rows = [elevation()]

    expect(liveElevationFor(rows, `${MACHINE}-1001`.toUpperCase(), NOW)?.id).toBe('e1')
  })

  it('ignores an elevation that is no longer live', () => {
    const rows = [elevation({ state: 'Expired', expiresAt: '2026-08-28T11:00:00Z' })]

    expect(liveElevationFor(rows, `${MACHINE}-1001`, NOW)).toBeUndefined()
  })

  it('finds a request still awaiting a decision', () => {
    const rows = [elevation({ state: 'Requested', expiresAt: null })]

    expect(pendingElevationFor(rows, `${MACHINE}-1001`)?.id).toBe('e1')
    expect(liveElevationFor(rows, `${MACHINE}-1001`, NOW)).toBeUndefined()
  })
})
