import { describe, expect, it } from 'vitest'
import {
  activeInstallations,
  installationSummary,
  installationsSummary,
  installedFootprint,
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

describe('installed footprint', () => {
  /**
   * The title's own count is a count of devices, not of copies: the server
   * groups the fleet by (name, version, publisher) and counts the distinct
   * devices behind each group. Wording it as installations would overstate a
   * title that is installed once per user on a shared machine.
   */
  it('counts devices and says so', () => {
    expect(installedFootprint(10)).toBe('Installed on 10 devices')
    expect(installedFootprint(1)).toBe('Installed on 1 device')
    expect(installedFootprint(10)).not.toMatch(/installation/i)
  })

  it('reports nothing installed rather than rendering a zero', () => {
    expect(installedFootprint(0)).toBe('Not installed on any device in scope')
  })
})

describe('the drill-down installations line', () => {
  const title = (installCount: number) => ({ installCount })

  /**
   * The defect this exists for. The device list under the header is filtered on
   * the server *before* it is counted, so under "Running" its total is a count
   * of running installations. Reading the header off that total announced a
   * title installed on ten machines as installed on two — and one running
   * nowhere as "Not installed on any device in scope", which is the sentence
   * that closes a ticket that is still open.
   */
  it('keeps the installed footprint when the running filter matches nothing', () => {
    const line = installationsSummary(title(10), 'running', { items: [], totalCount: 0 })

    expect(line).toMatch(/^Installed on 10 devices/)
    expect(line).not.toMatch(/not installed/i)
    expect(line).toMatch(/no installation was reported running at the last inventory/)
  })

  it('does not shrink the footprint to the number of rows the filter kept', () => {
    const line = installationsSummary(title(10), 'running', {
      items: [install({ deviceId: 'd1' }), install({ deviceId: 'd2' })],
      totalCount: 2,
    })

    expect(line).toBe(
      'Installed on 10 devices — 2 installations were reported running at the last inventory',
    )
  })

  /**
   * Both numbers are named, because they count different populations: ten
   * devices and two installations. "2 of 10" would be arithmetic across two
   * units, and wrong on any title with per-user copies.
   */
  it('names what each number counts', () => {
    const line = installationsSummary(title(4), 'stopped', { items: [], totalCount: 1 })

    expect(line).toBe(
      'Installed on 4 devices — 1 installation was reported not running at the last inventory',
    )
  })

  /**
   * Running and not-running are both claims that need evidence, and a row from
   * an agent that reported none is in neither count. Dropping "reported" would
   * fold those silent rows into the number.
   */
  it('presents a running state as something the inventory reported', () => {
    expect(installationsSummary(title(3), 'running', { items: [], totalCount: 2 }))
      .toMatch(/were reported running at the last inventory/)
    expect(installationsSummary(title(3), 'stopped', { items: [], totalCount: 2 }))
      .toMatch(/were reported not running at the last inventory/)
  })

  /** Unfiltered, the loaded list is richer: it can tell copies and devices apart. */
  it('summarises the list itself when no filter is applied', () => {
    const rows = [
      install({ deviceId: 'd1', installedForUser: 'a' }),
      install({ deviceId: 'd1', installedForUser: 'b' }),
      install({ deviceId: 'd2' }),
    ]

    expect(installationsSummary(title(2), 'all', { items: rows, totalCount: 3 }))
      .toBe('3 installations across 2 devices')
  })

  /** Before the list lands there is still something true to say. */
  it('falls back to the title’s own count while the list is loading', () => {
    expect(installationsSummary(title(3), 'running', null)).toBe('Installed on 3 devices')
    expect(installationsSummary(title(0), 'all', null)).toBe('Not installed on any device in scope')
  })

  /** Nothing installed needs no qualifier: there are no installations to be in any state. */
  it('says nothing more about a title no device has', () => {
    expect(installationsSummary(title(0), 'running', { items: [], totalCount: 0 }))
      .toBe('Not installed on any device in scope')
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
    expect(forceStopMessage('Chrome', 'NotRunning')).toMatch(/not running/i)
    expect(forceStopMessage('Chrome', 'Unresolvable')).toMatch(/unavailable/i)
    expect(forceStopMessage('Chrome', 'NotInstalled')).toMatch(/not installed/i)
    expect(forceStopMessage('Chrome', 'NotEligible')).toMatch(/retired|too old/i)
  })

  it('says the device checks live process state before stopping', () => {
    expect(forceStopMessage('Chrome', 'Queued')).toMatch(/^Chrome was asked to stop./)
    expect(forceStopMessage('Chrome', 'Queued')).toMatch(/checks which processes are running/i)
  })

  /**
   * Asked to stop, not stopped: the task is queued and the agent acts on its
   * next poll, so claiming completion here would be a claim the console cannot
   * support.
   */
  it('does not claim the application has already stopped', () => {
    expect(forceStopMessage('Chrome', 'Queued')).not.toMatch(/has stopped|was stopped|terminated/i)
  })

  it('falls back to a plain failure rather than showing a raw enum', () => {
    expect(forceStopMessage('Chrome', 'SomethingNew')).toBe('Chrome could not be stopped.')
  })
})
