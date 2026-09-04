import { describe, expect, it } from 'vitest'
import {
  activeInstallations,
  installationSummary,
  isSameTitle,
  registryViewLabel,
  scopeLabel,
  canForceStop,
  forceStopMessage,
  titleKey,
} from '../../pages/softwareView'
import type { SoftwareInstallation } from '../../api/client'

function install(overrides: Partial<SoftwareInstallation> = {}): SoftwareInstallation {
  return {
    deviceId: 'd1',
    hostname: 'PC-001',
    displayName: null,
    deviceStatus: 'Active',
    lastSeenAt: '2026-09-04T09:00:00Z',
    installationScope: 'Machine',
    installedForUser: null,
    architecture: 'x64',
    installLocation: null,
    productCode: null,
    collectedAt: '2026-09-04T09:00:00Z',
    ...overrides,
  }
}

describe('title identity', () => {
  it('treats an absent version as part of the identity, not a wildcard', () => {
    const withVersion = { name: 'Zoom Workplace', version: '7.1.5', publisher: 'Zoom' }
    const withoutVersion = { name: 'Zoom Workplace', version: null, publisher: 'Zoom' }

    expect(titleKey(withVersion)).not.toBe(titleKey(withoutVersion))
    expect(isSameTitle(withVersion, withoutVersion)).toBe(false)
  })

  /**
   * A null field and an empty-string field are different facts: one was not
   * reported, the other was reported as blank. Collapsing them would merge two
   * titles and hide one from the inventory.
   */
  it('distinguishes an absent field from an empty one', () => {
    expect(titleKey({ name: 'App', version: null, publisher: 'P' }))
      .not.toBe(titleKey({ name: 'App', version: '', publisher: 'P' }))
  })

  /** Field boundaries must not be forgeable by putting a separator in a name. */
  it('cannot be collided by crafted field values', () => {
    expect(titleKey({ name: 'A', version: 'B', publisher: 'C' }))
      .not.toBe(titleKey({ name: 'A', version: null, publisher: 'BC' }))
  })

  it('matches a title against itself and rejects a different publisher', () => {
    const title = { name: 'Chrome', version: '152', publisher: 'Google LLC' }

    expect(isSameTitle(title, { ...title })).toBe(true)
    expect(isSameTitle(title, { ...title, publisher: 'Someone Else' })).toBe(false)
    expect(isSameTitle(null, title)).toBe(false)
  })
})

describe('installation scope', () => {
  it('names the account a per-user install belongs to', () => {
    expect(scopeLabel(install({ installationScope: 'User', installedForUser: 'PC-001\\alice' })))
      .toBe('Per-user — PC-001\\alice')
  })

  it('describes a machine-wide install as covering all users', () => {
    expect(scopeLabel(install({ installationScope: 'Machine' }))).toBe('All users')
  })

  /**
   * Agents older than 1.5.0 did not report scope. Defaulting the unknown case to
   * "All users" would assert something the platform never determined, and would
   * read as a machine-wide install when it may well be per-user.
   */
  it('says scope is unknown rather than assuming machine-wide', () => {
    const label = scopeLabel(install({ installationScope: null, installedForUser: null }))

    expect(label).toBe('Scope not reported')
    expect(label).not.toMatch(/all users/i)
  })

  it('still reports per-user when the account could not be resolved', () => {
    expect(scopeLabel(install({ installationScope: 'User', installedForUser: null }))).toBe('Per-user')
  })
})

describe('registry view', () => {
  /**
   * The regression guard for a field that used to be presented as architecture.
   * Chrome, Edge and Brave are 64-bit but register under WOW6432Node and report
   * x86, so labelling this "32-bit application" would be false on the fleet's
   * most common browsers.
   */
  it('describes where the entry was found, never the binary architecture', () => {
    expect(registryViewLabel('x86')).toBe('32-bit registry')
    expect(registryViewLabel('x64')).toBe('64-bit registry')
    expect(registryViewLabel('x86')).not.toMatch(/application|binary|bit app/i)
  })

  it('has nothing to show when the view was not recorded', () => {
    expect(registryViewLabel(null)).toBe('—')
  })
})

describe('installation summary', () => {
  it('counts devices, not rows, when one device has several per-user installs', () => {
    const rows = [
      install({ deviceId: 'd1', installationScope: 'User', installedForUser: 'a' }),
      install({ deviceId: 'd1', installationScope: 'User', installedForUser: 'b' }),
      install({ deviceId: 'd2', installationScope: 'User', installedForUser: 'c' }),
    ]

    expect(installationSummary(rows, 3)).toBe('3 installations across 2 devices')
  })

  it('says it plainly when installations and devices agree', () => {
    expect(installationSummary([install({ deviceId: 'd1' })], 1)).toBe('Installed on 1 device')
  })

  it('reports an empty result rather than rendering a zero', () => {
    expect(installationSummary([], 0)).toBe('Not installed on any device in scope')
  })
})

describe('deployment targeting', () => {
  /** A retired device must never be offered as a target. */
  it('excludes retired devices', () => {
    const rows = [
      install({ deviceId: 'd1', deviceStatus: 'Active' }),
      install({ deviceId: 'd2', deviceStatus: 'Retired' }),
    ]

    expect(activeInstallations(rows).map((i) => i.deviceId)).toEqual(['d1'])
  })
})

describe('force stop', () => {
  /**
   * A display name cannot be turned into an image name safely, so Force Stop
   * needs an install path as evidence. Without one the action is not offered.
   */
  it('is offered only when the application reports an install location', () => {
    expect(canForceStop('C:\\Program Files\\Google\\Chrome\\Application')).toBe(true)
    expect(canForceStop(null)).toBe(false)
    expect(canForceStop('   ')).toBe(false)
  })

  /**
   * "Not running" and "cannot be resolved" look identical to an operator who is
   * only told it failed, but only the second means it will never work.
   */
  it('distinguishes not-running from permanently unavailable', () => {
    expect(forceStopMessage('Chrome', 'NotRunning', 0)).toMatch(/not running/i)
    expect(forceStopMessage('Chrome', 'Unresolvable', 0)).toMatch(/unavailable/i)
    expect(forceStopMessage('Chrome', 'NotInstalled', 0)).toMatch(/not installed/i)
    expect(forceStopMessage('Chrome', 'NotEligible', 0)).toMatch(/retired|too old/i)
  })

  it('reports how many processes were asked to stop', () => {
    expect(forceStopMessage('Chrome', 'Queued', 1)).toBe('Chrome was asked to stop.')
    expect(forceStopMessage('Chrome', 'Queued', 3)).toContain('3 processes')
  })

  /**
   * Asked to stop, not stopped: the task is queued and the agent acts on its
   * next poll, so claiming completion here would be a claim the console cannot
   * support.
   */
  it('does not claim the application has already stopped', () => {
    expect(forceStopMessage('Chrome', 'Queued', 1)).not.toMatch(/has stopped|was stopped|terminated/i)
  })

  it('falls back to a plain failure rather than showing a raw enum', () => {
    expect(forceStopMessage('Chrome', 'SomethingNew', 0)).toBe('Chrome could not be stopped.')
  })
})
