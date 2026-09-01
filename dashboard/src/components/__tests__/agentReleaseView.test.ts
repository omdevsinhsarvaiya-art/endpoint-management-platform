import { describe, expect, it } from 'vitest'
import {
  deploymentWarning,
  describeReleaseGap,
  newestPublished,
  newestUpdateEligible,
  newestUploaded,
  releaseGap,
  requiresUnsignedAcknowledgement,
} from '../../pages/agentReleaseView'
import { eligibleDevices, upgradeTargets, type DeviceUpdateCandidate } from '../../pages/agentUpdateView'
import type { AgentReleaseRow } from '../../api/client'

/**
 * Which release is "the latest", and what that entitles it to.
 *
 * Written against the real production shape, because the bug this file exists
 * for was invisible in the abstract: the estate holds 1.1.0 to 1.1.4 published,
 * and a 1.4.1 build that was made and installed by hand but never registered.
 * The page reported 1.1.4 as the latest release while the controlled device was
 * running 1.4.1, and both statements were true at once -- which is exactly why
 * one number cannot answer the question.
 */

function release(over: Partial<AgentReleaseRow> = {}): AgentReleaseRow {
  return {
    id: 'r-' + (over.version ?? '1.0.0'),
    version: '1.0.0',
    platform: 'Windows',
    architecture: 'x64',
    fileName: 'EndpointPlatformAgent-1.0.0-x64.msi',
    sha256: 'a'.repeat(64),
    signerSubject: 'CN=Example Corp',
    releaseNotes: null,
    contentSizeBytes: 1024,
    status: 'Published',
    createdByDisplay: 'admin@test.local',
    createdAt: '2026-09-01T00:00:00Z',
    publishedAt: '2026-09-01T01:00:00Z',
    revokedAt: null,
    ...over,
  }
}

/** The production estate: 1.1.0-1.1.4 published, 1.4.1 registered but unsigned and draft. */
function estateWith141Registered(): AgentReleaseRow[] {
  return [
    release({ id: 'r-141', version: '1.4.1', status: 'Draft', signerSubject: null, publishedAt: null }),
    release({ id: 'r-114', version: '1.1.4' }),
    release({ id: 'r-113', version: '1.1.3' }),
    release({ id: 'r-110', version: '1.1.0' }),
  ]
}

const estateBefore141 = (): AgentReleaseRow[] =>
  estateWith141Registered().filter((r) => r.version !== '1.4.1')

describe('the three answers', () => {
  it('distinguishes newest registered from newest published from newest deployable', () => {
    const releases = estateWith141Registered()

    expect(newestUploaded(releases)?.version).toBe('1.4.1')
    expect(newestPublished(releases)?.version).toBe('1.1.4')
    expect(newestUpdateEligible(releases)?.version).toBe('1.1.4')
  })

  /**
   * The original defect. The list arrives newest-created first and the page took
   * the first Published row, so publishing an older build after a newer one made
   * the older one "latest". Ordering by version rather than arrival fixes it.
   */
  it('orders by version, not by upload order', () => {
    const outOfOrder = [
      release({ id: 'late', version: '1.1.2', createdAt: '2026-09-09T00:00:00Z' }),
      release({ id: 'early', version: '1.1.4', createdAt: '2026-01-01T00:00:00Z' }),
    ]

    expect(newestPublished(outOfOrder)?.id).toBe('early')
  })

  it('ignores revoked releases when reporting what is held', () => {
    const releases = [...estateBefore141(), release({ version: '2.0.0', status: 'Revoked' })]

    expect(newestUploaded(releases)?.version).toBe('1.1.4')
    expect(newestPublished(releases)?.version).toBe('1.1.4')
  })

  it('ignores releases whose version cannot be read', () => {
    const releases = [...estateBefore141(), release({ version: 'nightly', status: 'Draft' })]

    expect(newestUploaded(releases)?.version).toBe('1.1.4')
  })

  it('answers nothing for an empty estate', () => {
    expect(newestUploaded([])).toBeNull()
    expect(newestPublished([])).toBeNull()
    expect(newestUpdateEligible([])).toBeNull()
  })
})

describe('the gap between held and deployed', () => {
  it('reports the 1.4.1 case as a newer unpublished build', () => {
    const releases = estateWith141Registered()

    expect(releaseGap(releases)).toBe('unpublished-newer')

    const text = describeReleaseGap(
      releaseGap(releases), newestUploaded(releases), newestUpdateEligible(releases))

    expect(text).toContain('1.4.1')
    expect(text).toContain('1.1.4')
    expect(text).toContain('not published')
  })

  it('reports no gap when the newest build is the published one', () => {
    expect(releaseGap(estateBefore141())).toBe('none')
  })

  it('reports an estate that has registered but published nothing', () => {
    const drafts = [release({ version: '1.4.1', status: 'Draft' })]

    expect(releaseGap(drafts)).toBe('nothing-published')
    expect(describeReleaseGap('nothing-published', drafts[0], null)).toContain('no device can be updated')
  })

  it('reports an empty estate', () => {
    expect(releaseGap([])).toBe('no-releases')
    expect(describeReleaseGap('no-releases', null, null)).toContain('No agent releases')
  })
})

describe('unsigned releases', () => {
  /**
   * The rule the feature turns on. Registering 1.4.1 must not make it a fleet
   * target; only publishing does that, and it stays a Draft until someone
   * decides otherwise.
   */
  it('a registered unsigned draft is not deployable to anything', () => {
    const releases = estateWith141Registered()
    const draft = releases.find((r) => r.version === '1.4.1')!

    expect(newestUpdateEligible(releases)?.version).not.toBe('1.4.1')

    const device: DeviceUpdateCandidate = {
      deviceId: 'd1', hostname: 'PC-1', agentVersion: '1.1.4', status: 'Active',
    }

    expect(eligibleDevices([device], draft)).toHaveLength(0)
    expect(upgradeTargets(device, releases).map((r) => r.version)).not.toContain('1.4.1')
  })

  it('requires an explicit acknowledgement before an unsigned build is published', () => {
    expect(requiresUnsignedAcknowledgement(release({ signerSubject: null }))).toBe(true)
    expect(requiresUnsignedAcknowledgement(release({ signerSubject: '   ' }))).toBe(true)
    expect(requiresUnsignedAcknowledgement(release())).toBe(false)
  })

  it('says what publishing an unsigned build would actually do', () => {
    const warning = deploymentWarning(release({ version: '1.4.1', status: 'Draft', signerSubject: null }))

    expect(warning).toContain('unsigned')
    expect(warning).toContain('hash verification alone')
  })

  it('keeps saying so once it is published', () => {
    const warning = deploymentWarning(release({ status: 'Published', signerSubject: null }))

    expect(warning).toContain('unsigned')
    expect(warning).toContain('SHA-256')
  })

  it('has nothing to warn about for a signed release', () => {
    expect(deploymentWarning(release())).toBeNull()
  })

  /**
   * Deliberately not narrowed by signature. The platform will queue and install
   * an unsigned published release; a console that hid that would imply a
   * protection which does not exist. The signature is surfaced, not enforced here.
   */
  it('does not pretend an unsigned published release is undeployable', () => {
    const releases = [release({ version: '1.4.1', signerSubject: null })]

    expect(newestUpdateEligible(releases)?.version).toBe('1.4.1')
    expect(deploymentWarning(releases[0])).not.toBeNull()
  })
})

describe('existing 1.1.x behaviour is unchanged', () => {
  it('still reports 1.1.4 as the deployable release', () => {
    expect(newestUpdateEligible(estateBefore141())?.version).toBe('1.1.4')
  })

  it('still offers 1.1.4 to a device behind it', () => {
    const device: DeviceUpdateCandidate = {
      deviceId: 'd1', hostname: 'MSI', agentVersion: '1.1.1', status: 'Active',
    }

    expect(upgradeTargets(device, estateBefore141()).map((r) => r.version)).toEqual(['1.1.4', '1.1.3'])
  })
})

describe('a device already ahead of every published release', () => {
  /**
   * The controlled device. It runs 1.4.1 while the newest published build is
   * 1.1.4, so it is ahead of the fleet rather than behind it -- and must never
   * be offered a downgrade, whether or not 1.4.1 is registered.
   */
  const controlled: DeviceUpdateCandidate = {
    deviceId: 'd-controlled', hostname: 'OMDEVSINH-TECHS', agentVersion: '1.4.1', status: 'Active',
  }

  it('is offered nothing while 1.4.1 is unregistered', () => {
    expect(upgradeTargets(controlled, estateBefore141())).toHaveLength(0)
    expect(eligibleDevices([controlled], newestUpdateEligible(estateBefore141())!)).toHaveLength(0)
  })

  it('is still offered nothing once 1.4.1 is registered as a draft', () => {
    expect(upgradeTargets(controlled, estateWith141Registered())).toHaveLength(0)
  })

  it('is not counted as behind in a bulk selection targeting 1.1.4', () => {
    const fleet = [
      controlled,
      { deviceId: 'd-msi', hostname: 'MSI', agentVersion: '1.1.1', status: 'Active' },
      { deviceId: 'd-pjcc', hostname: 'DESKTOP-PJCC143', agentVersion: '1.1.2', status: 'Active' },
      { deviceId: 'd-old', hostname: 'AWS-VERIFY-PC', agentVersion: '1.0.0', status: 'Retired' },
    ]

    const chosen = eligibleDevices(fleet, newestUpdateEligible(estateBefore141())!).map((d) => d.hostname)

    expect(chosen).toEqual(['MSI', 'DESKTOP-PJCC143'])
    expect(chosen).not.toContain('OMDEVSINH-TECHS')
    expect(chosen).not.toContain('AWS-VERIFY-PC')
  })
})
