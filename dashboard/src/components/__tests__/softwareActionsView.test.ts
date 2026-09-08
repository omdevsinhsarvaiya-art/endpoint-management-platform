import { describe, expect, it } from 'vitest'
import {
  MINIMUM_REMOVE_AGENT_VERSION,
  RUNNING_FILTERS,
  emptySoftwareListMessage,
  matchesRunningFilter,
  removability,
  removeAgentSupport,
  removeMessage,
  removeReasonLabel,
  reportsRunningState,
  rowActions,
  runningState,
  softwareRowKey,
} from '../../pages/softwareView'
import type { DeviceSoftwareItem } from '../../api/client'

/**
 * The rules behind a software row's Actions menu and the running-state filter:
 * what the last inventory could say about a row, which actions it supports,
 * and how a refusal is worded. All client-side hints — the server decides — so
 * what is guarded here is that the console never offers what is certain to be
 * refused, and never claims what the platform has not determined.
 */

function item(overrides: Partial<DeviceSoftwareItem> = {}): DeviceSoftwareItem {
  return {
    name: 'Google Chrome',
    version: '152.0.1',
    publisher: 'Google LLC',
    installDate: null,
    architecture: 'x86',
    installationScope: 'Machine',
    installedForUser: null,
    productCode: '{A1B2C3D4-0000-4000-8000-000000000001}',
    installLocation: 'C:\\Program Files\\Google\\Chrome\\Application',
    identityKind: 'WindowsInstaller',
    category: 'Application',
    confidence: 'Installed',
    ...overrides,
  }
}

const ANY = { canExecuteTasks: true, canDeploy: true }

/**
 * The two agent versions this feature turns on, and they are not the same
 * number. 1.10.0 carries the removal executor; 1.9.0 carries only the
 * discovery that makes a row *look* removable. A device on 1.9.0 is the case
 * the console used to get wrong.
 */
const CAN_REMOVE = '1.10.0'
const NO_EXECUTOR = '1.9.0'

describe('row key', () => {
  /**
   * One machine holds the same application once per user. A busy flag or an
   * open menu keyed by name alone lights up every duplicate when one is acted
   * on, which is exactly the row an operator is trying to tell apart.
   */
  it('separates per-user duplicates of the same application', () => {
    const alice = item({ installationScope: 'User', installedForUser: 'PC-001\\alice' })
    const bob = item({ installationScope: 'User', installedForUser: 'PC-001\\bob' })

    expect(softwareRowKey(alice)).not.toBe(softwareRowKey(bob))
    expect(softwareRowKey(alice)).toBe(softwareRowKey({ ...alice }))
  })

  it('separates two versions of the same application', () => {
    expect(softwareRowKey(item({ version: '1.0' }))).not.toBe(softwareRowKey(item({ version: '2.0' })))
  })
})

describe('running state', () => {
  it('takes the server’s answer when it gives one', () => {
    expect(runningState(item({ isRunning: true }))).toBe('running')
    expect(runningState(item({ isRunning: false }))).toBe('stopped')
  })

  /**
   * Null is the server saying the row has no evidence at all — an agent older
   * than 1.9.0. That is not "stopped", and reading it as stopped would list
   * every application on every un-upgraded device under Not running.
   */
  it('treats a null answer as unknown, not as stopped', () => {
    expect(runningState(item({ isRunning: null }))).toBe('unknown')
    expect(runningState(item({ isRunning: null, evidence: [{ source: 'RunningProcess', name: 'chrome.exe', detail: null }] })))
      .toBe('unknown')
  })

  /** A server that predates the field sends nothing; the evidence it does send is read the same way. */
  it('falls back to the evidence when the server did not say', () => {
    expect(runningState(item({ evidence: [{ source: 'RunningProcess', name: 'chrome.exe', detail: null }] })))
      .toBe('running')
    expect(runningState(item({ evidence: [{ source: 'WindowsInstaller', name: null, detail: null }] })))
      .toBe('stopped')
    expect(runningState(item({ evidence: [] }))).toBe('unknown')
    expect(runningState(item())).toBe('unknown')
  })
})

describe('running filter', () => {
  it('offers All, Running and Not running in that order', () => {
    expect(RUNNING_FILTERS.map((f) => f.key)).toEqual(['all', 'running', 'stopped'])
    expect(RUNNING_FILTERS.map((f) => f.label)).toEqual(['All', 'Running', 'Not running'])
  })

  it('matches running and stopped rows to their own view', () => {
    expect(matchesRunningFilter(item({ isRunning: true }), 'running')).toBe(true)
    expect(matchesRunningFilter(item({ isRunning: true }), 'stopped')).toBe(false)
    expect(matchesRunningFilter(item({ isRunning: false }), 'stopped')).toBe(true)
    expect(matchesRunningFilter(item({ isRunning: false }), 'running')).toBe(false)
  })

  /**
   * "Not running" is a claim. A row the inventory could not judge appears only
   * under All, so the filter never asserts a state the platform did not see.
   */
  it('shows a row of unknown state only under All', () => {
    const unknown = item({ isRunning: null })

    expect(matchesRunningFilter(unknown, 'all')).toBe(true)
    expect(matchesRunningFilter(unknown, 'running')).toBe(false)
    expect(matchesRunningFilter(unknown, 'stopped')).toBe(false)
  })

  it('All takes everything', () => {
    expect(matchesRunningFilter(item({ isRunning: true }), 'all')).toBe(true)
    expect(matchesRunningFilter(item({ isRunning: false }), 'all')).toBe(true)
  })
})

describe('removability', () => {
  it('offers a machine-wide Windows Installer product through the installer service', () => {
    expect(removability(item())).toEqual({ removable: true, method: 'WindowsInstaller', reason: null })
  })

  /**
   * The agent runs as SYSTEM and the Windows Installer service cannot see another
   * account's per-user product. Offering Remove would be a guaranteed refusal.
   */
  it('refuses a per-user Windows Installer product', () => {
    const perUser = item({ installationScope: 'User', installedForUser: 'PC-001\\alice' })

    expect(removability(perUser)).toEqual({ removable: false, method: null, reason: 'PerUserInstall' })
  })

  it('offers a package through the deployment engine', () => {
    const pkg = item({
      identityKind: 'Package',
      productCode: null,
      packageFamilyName: 'SpotifyAB.SpotifyMusic_zpdnekdrzrea0',
    })

    expect(removability(pkg)).toEqual({ removable: true, method: 'Package', reason: null })
  })

  /** Windows itself: an inbox publisher id, or a category the endpoint labelled as not-an-application. */
  it('refuses an inbox or system package', () => {
    const inbox = item({
      identityKind: 'Package',
      productCode: null,
      packageFamilyName: 'Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy',
      category: 'Application',
    })
    expect(removability(inbox).reason).toBe('SystemComponent')

    for (const category of ['InboxApp', 'FrameworkOrResource', 'Component']) {
      const system = item({
        identityKind: 'Package',
        productCode: null,
        packageFamilyName: 'Microsoft.VCLibs.140.00_8wekyb3d8bbwe',
        category,
      })
      expect(removability(system), category).toEqual({ removable: false, method: null, reason: 'SystemComponent' })
    }
  })

  /** The agent must never be offered its own removal, whatever its installer says. */
  it('refuses the endpoint agent itself', () => {
    const agent = item({ name: 'Endpoint Platform Agent', publisher: 'Techsara' })

    expect(removability(agent)).toEqual({ removable: false, method: null, reason: 'ProtectedAgent' })
  })

  /**
   * An EXE installer's registration, a portable executable and an observed
   * process all have an uninstaller that is a program, which the agent does not
   * launch (ADR-0005). A row from an agent older than 1.9.0 has no identity at all.
   */
  it('refuses everything whose uninstaller is a program', () => {
    for (const identityKind of ['Registered', 'Executable', 'Observed', null, undefined]) {
      const row = item({ identityKind, productCode: null })
      expect(removability(row), String(identityKind)).toEqual({
        removable: false,
        method: null,
        reason: 'NoInstallerIdentity',
      })
    }
  })

  it('needs a product code to call a row a Windows Installer product', () => {
    expect(removability(item({ productCode: null })).reason).toBe('NoInstallerIdentity')
  })
})

describe('remove reason', () => {
  it('words every reason the server can give', () => {
    expect(removeReasonLabel('PerUserInstall')).toMatch(/one user|per-user/i)
    expect(removeReasonLabel('SystemComponent')).toMatch(/windows/i)
    expect(removeReasonLabel('ProtectedAgent')).toMatch(/agent/i)
    expect(removeReasonLabel('NoInstallerIdentity')).toMatch(/does not launch/i)
  })

  /** A reason a newer server adds is still information; a missing one is said to be missing. */
  it('shows an unknown reason as itself and an absent one plainly', () => {
    expect(removeReasonLabel('SomethingNewer')).toBe('SomethingNewer')
    expect(removeReasonLabel(null)).toBe('no reason was given')
  })
})

describe('remove message', () => {
  /**
   * Queued, not removed: the device stops and uninstalls on its next check-in
   * and the task reports the result, so this may not claim the application is
   * gone.
   */
  it('does not claim the application has been removed when the task is only queued', () => {
    const message = removeMessage('Chrome', 'Queued', null)

    expect(message).toMatch(/queued/i)
    expect(message).not.toMatch(/has been removed|was removed|is removed|uninstalled\./i)
  })

  it('carries the server’s reason for a refusal', () => {
    expect(removeMessage('Chrome', 'NotRemovable', 'PerUserInstall')).toMatch(/^Chrome cannot be removed: .*per-user/i)
    expect(removeMessage('Chrome', 'NotRemovable', 'ProtectedAgent')).toMatch(/agent/i)
  })

  it('distinguishes not-installed from an ineligible device', () => {
    expect(removeMessage('Chrome', 'NotInstalled', null)).toMatch(/not installed/i)
    expect(removeMessage('Chrome', 'NotEligible', null)).toMatch(/retired|too old/i)
  })

  it('falls back to a plain failure rather than showing a raw enum', () => {
    expect(removeMessage('Chrome', 'SomethingNew', null)).toBe('Chrome could not be removed.')
  })
})

describe('row actions', () => {
  it('lists Force Stop then Remove, both enabled, for a stoppable removable row', () => {
    expect(rowActions(item(), ANY, CAN_REMOVE)).toEqual([
      { key: 'force-stop', label: 'Force Stop', enabled: true, reason: null },
      { key: 'remove', label: 'Remove…', enabled: true, reason: null },
    ])
  })

  /**
   * Hidden, not disabled, without the permission: the convention across the
   * console, and an operator who cannot act should not be told a row-level
   * reason that is not the real one.
   */
  it('omits an action the operator lacks permission for', () => {
    expect(rowActions(item(), { canExecuteTasks: true, canDeploy: false }, CAN_REMOVE).map((a) => a.key))
      .toEqual(['force-stop'])
    expect(rowActions(item(), { canExecuteTasks: false, canDeploy: true }, CAN_REMOVE).map((a) => a.key))
      .toEqual(['remove'])
    expect(rowActions(item(), { canExecuteTasks: false, canDeploy: false }, CAN_REMOVE)).toEqual([])
  })

  /** Force Stop needs an install path; without one it stays listed, disabled, with the reason it always had. */
  it('disables Force Stop with its reason when there is no install location', () => {
    const [forceStop] = rowActions(item({ installLocation: null }), ANY, CAN_REMOVE)

    expect(forceStop.key).toBe('force-stop')
    expect(forceStop.enabled).toBe(false)
    expect(forceStop.reason).toBe('No install location was reported for this application')
  })

  it('disables Remove with the worded reason when the row cannot be removed', () => {
    const [, remove] = rowActions(item({ identityKind: 'Registered', productCode: null }), ANY, CAN_REMOVE)

    expect(remove.key).toBe('remove')
    expect(remove.enabled).toBe(false)
    expect(remove.reason).toMatch(/^Cannot be removed: .*does not launch/i)
  })

  /** The two decisions are independent: a portable app can be stopped but not removed, and a package removed but not stopped. */
  it('decides each action on its own evidence', () => {
    const portable = rowActions(item({ identityKind: 'Executable', productCode: null }), ANY, CAN_REMOVE)
    expect(portable.map((a) => a.enabled)).toEqual([true, false])

    const pkg = rowActions(
      item({ identityKind: 'Package', productCode: null, installLocation: null, packageFamilyName: 'A.B_abc123' }),
      ANY,
      CAN_REMOVE,
    )
    expect(pkg.map((a) => a.enabled)).toEqual([false, true])
  })
})

/**
 * The defect this suite exists for.
 *
 * Two different agent boundaries meet on this row and the console used to know
 * only one of them. 1.9.0 is where application discovery shipped, so it decides
 * whether the platform can tell what a row *is*; 1.10.0 is where
 * RemoveApplicationExecutor shipped, so it decides whether the endpoint can do
 * anything about it. Between them sits a device whose inventory is rich enough
 * for `removability` to answer "removable" and whose agent cannot remove
 * anything: Remove was offered as enabled, an operator confirmed a destructive
 * action, and only then did the server answer NotEligible.
 *
 * Every case here is about the disabled state being reached *before* the
 * confirmation, and about the reason being the true one.
 */
describe('remove against the agent that has to carry it out', () => {
  const remove = (agentVersion: string | null | undefined, overrides: Partial<DeviceSoftwareItem> = {}) => {
    const [, action] = rowActions(item(overrides), ANY, agentVersion)
    expect(action.key).toBe('remove')
    return action
  }

  /**
   * The console's copy of the server's gate. It exists as a constant so the
   * mirror is one line to find and to change; DeviceTaskCatalog's
   * MinimumAgentVersion for RemoveApplication is the original, and the two have
   * to move together.
   */
  it('mirrors the catalogue’s minimum agent version', () => {
    expect(MINIMUM_REMOVE_AGENT_VERSION).toBe('1.10.0')
  })

  it('enables Remove on exactly the minimum', () => {
    expect(remove(CAN_REMOVE).enabled).toBe(true)
    expect(remove(CAN_REMOVE).reason).toBeNull()
  })

  it('enables Remove on every version above the minimum', () => {
    for (const version of ['1.10.1', '1.11.0', '2.0.0', '10.0.0']) {
      expect(remove(version).enabled, version).toBe(true)
    }
  })

  /**
   * 1.9.0 is the case the defect was about: rich inventory, no executor. The
   * row itself is perfectly removable, so nothing about the row can explain
   * this and the reason has to name the version.
   */
  it('disables Remove on an agent that discovers identity but cannot act on it', () => {
    const action = remove(NO_EXECUTOR)

    expect(action.enabled).toBe(false)
    expect(action.reason).toMatch(/^Cannot be removed: /)
    expect(action.reason).toContain('1.10.0')
    expect(action.reason).toMatch(/agent is older/i)
  })

  /** "1.9" must never sort above "1.10" — the comparison is numeric, not textual. */
  it('disables Remove on every version below the minimum', () => {
    for (const version of ['1.9.9', '1.6.0', '0.9.0', '1.2.3']) {
      const action = remove(version)
      expect(action.enabled, version).toBe(false)
      expect(action.reason, version).toContain('1.10.0')
    }
  })

  /**
   * Fails closed, deliberately. A version the console cannot read is a device
   * that has not demonstrated it can do the work, and the alternative — assume
   * it can, offer a destructive action, take a confirmation — is exactly the
   * failure being fixed. It is worded as unreadable rather than as too old,
   * because "older than 1.10.0" would be a claim about a version nothing here
   * managed to parse.
   */
  it('disables Remove when the reported version cannot be read', () => {
    for (const version of ['', '   ', 'unknown', '1.10', '1.10.0.0', 'v1.10.0', '1.10.0-beta.1', null, undefined]) {
      const action = remove(version)

      expect(action.enabled, String(version)).toBe(false)
      expect(action.reason, String(version)).toContain('could not be read')
      expect(action.reason, String(version)).toContain('1.10.0')
      expect(action.reason, String(version)).not.toMatch(/older than/i)
    }
  })

  /**
   * The row's reason outranks the version's, whatever the version is. It is the
   * permanent one: no agent update makes a Windows component or another
   * account's per-user install removable, so leading with the version would
   * send an operator to do an upgrade that changes nothing for that row.
   */
  it('keeps the row’s own reason at every version', () => {
    const rows = [
      { overrides: { identityKind: 'Registered', productCode: null }, expected: /does not launch/i },
      { overrides: { installationScope: 'User', installedForUser: 'PC-001\\alice' }, expected: /per-user|one user/i },
      { overrides: { name: 'Endpoint Platform Agent' }, expected: /endpoint agent/i },
      {
        overrides: {
          identityKind: 'Package',
          productCode: null,
          packageFamilyName: 'Microsoft.Windows.ShellExperienceHost_cw5n1h2txyewy',
        },
        expected: /part of Windows/i,
      },
    ]

    for (const version of [NO_EXECUTOR, CAN_REMOVE, '2.0.0', 'unknown', null]) {
      for (const { overrides, expected } of rows) {
        const action = remove(version, overrides)
        const where = `${String(version)} / ${JSON.stringify(overrides)}`

        expect(action.enabled, where).toBe(false)
        expect(action.reason, where).toMatch(expected)
        expect(action.reason, where).not.toContain('1.10.0')
      }
    }
  })

  /**
   * Force Stop has its own, older gate (the catalogue puts StopApplication at
   * 1.6.0) and is not this rule's business. An agent too old to remove software
   * can still be asked to stop it.
   */
  it('does not touch Force Stop', () => {
    for (const version of [NO_EXECUTOR, CAN_REMOVE, 'unknown', null]) {
      const [forceStop] = rowActions(item(), ANY, version)

      expect(forceStop.key, String(version)).toBe('force-stop')
      expect(forceStop.enabled, String(version)).toBe(true)
      expect(forceStop.reason, String(version)).toBeNull()
    }
  })

  /** The three answers the version alone can give, without a row in the way. */
  it('reads a version as supported, too old, or unreadable', () => {
    expect(removeAgentSupport('1.10.0')).toBe('supported')
    expect(removeAgentSupport('2.0.0')).toBe('supported')
    expect(removeAgentSupport('1.9.0')).toBe('too-old')
    expect(removeAgentSupport('1.9.11')).toBe('too-old')
    expect(removeAgentSupport('1.10')).toBe('unreadable')
    expect(removeAgentSupport(null)).toBe('unreadable')
    expect(removeAgentSupport(undefined)).toBe('unreadable')
  })
})

describe('whether a device reports running state at all', () => {
  /**
   * Process evidence arrived with agent 1.9.0. Every row an older agent sends
   * is unknown, which is not the same fact as every row being stopped, and the
   * console has to be able to tell the two apart to explain an empty list.
   */
  it('is false when no row on the device was ever judged', () => {
    expect(reportsRunningState([item(), item({ isRunning: null })])).toBe(false)
  })

  it('is true as soon as one row carries an answer', () => {
    expect(reportsRunningState([item({ isRunning: null }), item({ isRunning: false })])).toBe(true)
    expect(reportsRunningState([item({ isRunning: true })])).toBe(true)
  })

  it('is false for a device with no software at all', () => {
    expect(reportsRunningState([])).toBe(false)
  })
})

describe('an emptied software list', () => {
  const emptied = (input: Partial<Parameters<typeof emptySoftwareListMessage>[0]> = {}) =>
    emptySoftwareListMessage({
      filter: 'all',
      searching: false,
      hidingSystem: true,
      reportsRunningState: true,
      ...input,
    })

  /**
   * The defect this exists for. On a device whose agent predates process
   * evidence, every row is unknown and both filtered views exclude all of them,
   * so the table rendered a header with nothing under it — indistinguishable
   * from a page that failed to load, and silent about the one thing that would
   * explain it.
   */
  it('names the inventory’s silence when nothing on the device can be judged', () => {
    const message = emptied({ filter: 'running', reportsRunningState: false })

    expect(message.title).toMatch(/does not report running state/i)
    expect(message.detail).toMatch(/1\.9\.0/)
    expect(message.detail).toMatch(/All/)
  })

  /**
   * That case outranks every other: no change to the search box or the
   * components toggle can make a filtered view match a row the inventory never
   * judged, so pointing at the search would send someone to retype it.
   */
  it('blames the inventory even while a search is also narrowing the list', () => {
    expect(emptied({ filter: 'stopped', searching: true, reportsRunningState: false }).title)
      .toMatch(/does not report running state/i)
  })

  /** A device that does report state has a different, actionable answer. */
  it('blames the filter, not the agent, when other rows were judged', () => {
    const message = emptied({ filter: 'running' })

    expect(message.title).toBe('No applications match this filter')
    expect(message.detail).toMatch(/reported running at its last inventory/)
    expect(message.detail).not.toMatch(/1\.9\.0/)
  })

  /** "Not running" is what the last inventory reported, never a live claim. */
  it('words a not-running filter as something the inventory reported', () => {
    expect(emptied({ filter: 'stopped' }).detail).toMatch(/reported not running at its last inventory/)
  })

  it('points at the search when the search is what emptied the list', () => {
    expect(emptied({ searching: true }).title).toMatch(/search/i)
    expect(emptied({ searching: true, filter: 'running' }).detail).toMatch(/both the search and/i)
  })

  /** The rows exist and are one toggle away, so the toggle is named. */
  it('offers the components toggle when only components were reported', () => {
    expect(emptied({ hidingSystem: true }).detail).toMatch(/Show Windows components/)
  })

  /** Never a bare header: there is always something to say. */
  it('always has a title and a detail', () => {
    for (const filter of ['all', 'running', 'stopped'] as const) {
      for (const searching of [true, false]) {
        for (const hidingSystem of [true, false]) {
          for (const reports of [true, false]) {
            const message = emptySoftwareListMessage({
              filter, searching, hidingSystem, reportsRunningState: reports,
            })

            expect(message.title.length).toBeGreaterThan(0)
            expect(message.detail.length).toBeGreaterThan(0)
          }
        }
      }
    }
  })
})
