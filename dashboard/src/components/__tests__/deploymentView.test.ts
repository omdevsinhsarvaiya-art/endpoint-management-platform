import { describe, expect, it } from 'vitest'
import {
  hasCancellableWork,
  hasRetryableWork,
  hasWorkToDo,
  isSettled,
  matchesSearch,
  planLines,
  reasonLabel,
  selectableTargets,
  statusTone,
  tallySummary,
  type TargetCandidate,
} from '../../pages/deploymentView'
import type { DeploymentPlan, DeploymentTally } from '../../api/client'

function device(overrides: Partial<TargetCandidate> = {}): TargetCandidate {
  return {
    id: 'd1',
    hostname: 'PC-001',
    displayName: null,
    status: 'Active',
    agentVersion: '1.5.0',
    lastSeenAt: '2026-09-04T09:00:00Z',
    ...overrides,
  }
}

function plan(overrides: Partial<DeploymentPlan> = {}): DeploymentPlan {
  return {
    packageId: 'p1',
    packageName: 'Contoso App',
    packageVersion: '2.0.0',
    targeted: 0,
    needsInstall: 0,
    alreadyInstalled: 0,
    newerInstalled: 0,
    retired: 0,
    notComparable: 0,
    ...overrides,
  }
}

function tally(overrides: Partial<DeploymentTally> = {}): DeploymentTally {
  return {
    total: 0, pending: 0, installing: 0, succeeded: 0, failed: 0, expired: 0, skipped: 0,
    offline: 0, cancelled: 0,
    ...overrides,
  }
}

describe('target selection', () => {
  /**
   * A retired device can never receive a task, so offering it would be offering
   * an action that silently does nothing. The server enforces this too.
   */
  it('never offers a retired device as a target', () => {
    const devices = [
      device({ id: 'a', status: 'Active' }),
      device({ id: 'b', status: 'Retired' }),
    ]

    expect(selectableTargets(devices).map((d) => d.id)).toEqual(['a'])
  })

  it('searches hostname and display name, case-insensitively', () => {
    expect(matchesSearch(device({ hostname: 'PC-FINANCE-01' }), 'finance')).toBe(true)
    expect(matchesSearch(device({ displayName: "Alice's laptop" }), 'ALICE')).toBe(true)
    expect(matchesSearch(device({ hostname: 'PC-001' }), 'zzz')).toBe(false)
  })

  it('matches everything when nothing is typed', () => {
    expect(matchesSearch(device(), '   ')).toBe(true)
  })
})

describe('deployment plan', () => {
  /**
   * Every targeted device must be accounted for in exactly one line: a
   * resolution that dropped devices silently would let an operator believe a
   * deployment covered machines it never touched.
   */
  it('accounts for every targeted device', () => {
    const p = plan({
      targeted: 10, needsInstall: 4, alreadyInstalled: 3,
      newerInstalled: 1, retired: 1, notComparable: 1,
    })

    const lines = planLines(p)
    const total = lines.find((l) => l.label === 'Target devices')!.value
    const accounted = lines
      .filter((l) => l.label !== 'Target devices')
      .reduce((sum, l) => sum + l.value, 0)

    expect(total).toBe(10)
    expect(accounted).toBe(10)
  })

  /** Zero-valued edge cases are omitted rather than shown as noise. */
  it('omits categories that did not occur', () => {
    const labels = planLines(plan({ targeted: 2, needsInstall: 2 })).map((l) => l.label)

    expect(labels).toEqual(['Target devices', 'Installation needed', 'Already installed'])
    expect(labels).not.toContain('Retired, excluded')
  })

  /**
   * Everything already being correct is a success, but submitting would create
   * an empty deployment — so the dialog must not offer to.
   */
  it('knows when there is nothing to deploy', () => {
    expect(hasWorkToDo(plan({ targeted: 5, alreadyInstalled: 5 }))).toBe(false)
    expect(hasWorkToDo(plan({ targeted: 5, needsInstall: 1, alreadyInstalled: 4 }))).toBe(true)
    expect(hasWorkToDo(null)).toBe(false)
  })
})

describe('deployment progress', () => {
  /** Settled means nothing is still moving — never a stored flag. */
  it('is settled only when nothing is pending, installing or offline', () => {
    expect(isSettled(tally({ total: 3, succeeded: 2, failed: 1 }))).toBe(true)
    expect(isSettled(tally({ total: 3, succeeded: 2, pending: 1 }))).toBe(false)
    expect(isSettled(tally({ total: 3, succeeded: 2, installing: 1 }))).toBe(false)
    // An offline device still owes work: the task runs if it returns in time.
    expect(isSettled(tally({ total: 3, succeeded: 2, offline: 1 }))).toBe(false)
  })

  /**
   * Retry acts on work that never succeeded. Expired and cancelled qualify —
   * neither ever ran — while skipped does not: a skipped device was deliberately
   * sent nothing and retrying would skip it again.
   */
  it('offers retry only for work that failed, expired or was cancelled', () => {
    expect(hasRetryableWork(tally({ total: 2, succeeded: 1, failed: 1 }))).toBe(true)
    expect(hasRetryableWork(tally({ total: 2, succeeded: 1, expired: 1 }))).toBe(true)
    expect(hasRetryableWork(tally({ total: 2, succeeded: 1, cancelled: 1 }))).toBe(true)
    expect(hasRetryableWork(tally({ total: 2, succeeded: 1, skipped: 1 }))).toBe(false)
    expect(hasRetryableWork(tally({ total: 1, succeeded: 1 }))).toBe(false)
  })

  /** Only queued work can be cancelled; finished work cannot be undone. */
  it('offers cancel only while work is still queued', () => {
    expect(hasCancellableWork(tally({ total: 2, succeeded: 1, pending: 1 }))).toBe(true)
    expect(hasCancellableWork(tally({ total: 2, succeeded: 1, offline: 1 }))).toBe(true)
    expect(hasCancellableWork(tally({ total: 2, succeeded: 1, installing: 1 }))).toBe(false)
    expect(hasCancellableWork(tally({ total: 2, succeeded: 2 }))).toBe(false)
  })

  it('summarises only the categories that occurred', () => {
    expect(tallySummary(tally({ total: 4, succeeded: 3, failed: 1 })))
      .toBe('3 succeeded, 1 failed')
    expect(tallySummary(tally())).toBe('No devices')
  })

  it('does not colour a skipped device as a failure', () => {
    // Skipped usually means the device was already correct.
    expect(statusTone('Skipped')).toBe('neutral')
    expect(statusTone('Succeeded')).toBe('ok')
    expect(statusTone('Failed')).toBe('crit')
    expect(statusTone('Installing')).toBe('info')
    expect(statusTone('Expired')).toBe('warn')
  })

  /** An unknown status must not be dressed up as a real outcome. */
  it('treats an unrecognised status neutrally', () => {
    expect(statusTone('Unknown')).toBe('neutral')
  })
})

describe('skip reasons', () => {
  it('explains each reason in words, not enum names', () => {
    expect(reasonLabel('AlreadyInstalled')).toBe('Already at this version')
    expect(reasonLabel('NewerInstalled')).toMatch(/not downgraded/i)
    expect(reasonLabel('VersionNotComparable')).toMatch(/could not be compared/i)
    expect(reasonLabel('Retired')).toMatch(/retired/i)
  })

  /** Never render a bare enum the operator would have to decode. */
  it('falls back to the raw reason rather than showing nothing', () => {
    expect(reasonLabel('SomethingNew')).toBe('SomethingNew')
  })
})
