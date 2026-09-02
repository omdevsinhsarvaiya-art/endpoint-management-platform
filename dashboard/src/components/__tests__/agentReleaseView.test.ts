import { describe, expect, it } from 'vitest'
import {
  deploymentWarning,
  describeReleaseGap,
  downloadHint,
  isDownloadable,
  newestPublished,
  newestUpdateEligible,
  newestUploaded,
  releaseGap,
  publishWillBeRefused,
  trustModeLabel,
} from '../../pages/agentReleaseView'
import {
  eligibleDevices,
  isEligible,
  upgradeTargets,
  type DeviceUpdateCandidate,
} from '../../pages/agentUpdateView'
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

  /**
   * Internal is the production model. Signatures are not read, so nothing about
   * them is a reason to refuse, and the console must not pretend otherwise.
   */
  it('under Internal an unsigned draft is publishable and carries no warning', () => {
    const draft = release({ version: '1.4.1', status: 'Draft', signerSubject: null })

    expect(publishWillBeRefused(draft, 'Internal')).toBe(false)
    expect(deploymentWarning(draft, 'Internal')).toBeNull()
  })

  it('under Public an unsigned draft is refused, with the fix named', () => {
    const draft = release({ version: '1.4.1', status: 'Draft', signerSubject: null })

    expect(publishWillBeRefused(draft, 'Public')).toBe(true)
    const warning = deploymentWarning(draft, 'Public')
    expect(warning).toContain('Public')
    expect(warning).toContain('Authenticode-signed')
    expect(warning).not.toContain('hash verification alone')
  })

  it('has nothing to warn about for a signed release in either mode', () => {
    expect(deploymentWarning(release(), 'Internal')).toBeNull()
    expect(deploymentWarning(release(), 'Public')).toBeNull()
    expect(publishWillBeRefused(release(), 'Public')).toBe(false)
  })

  /** The badge must say what the model is, and must not call an Internal build a problem. */
  it('describes the trust model accurately', () => {
    const internal = trustModeLabel('Internal')
    expect(internal).toContain('Internal release')
    expect(internal).toContain('SHA-256 verified')
    expect(internal).toContain('not required')
    expect(internal).not.toMatch(/warn|cannot|missing/i)

    expect(trustModeLabel('Public')).toContain('Authenticode signature required')
  })

  /**
   * A published release is, by construction, one the server verified. The console
   * still computes eligibility from status alone -- the server is the gate and has
   * already applied it -- so a Published row with no signer (which the gate no
   * longer produces) would still be treated as the server said, not second-guessed.
   */
  it('trusts the server on what is published', () => {
    const releases = [release({ version: '1.4.1', signerSubject: null })]

    expect(newestUpdateEligible(releases)?.version).toBe('1.4.1')
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

describe('downloading a build', () => {
  /**
   * The distinction the whole change rests on. Downloading is an administrator
   * fetching an artifact to install by hand; publishing is the platform pushing it
   * onto machines. A Draft must be downloadable without being deployable.
   */
  it('allows a draft to be downloaded', () => {
    expect(isDownloadable(release({ status: 'Draft' }))).toBe(true)
  })

  it('allows a published release to be downloaded', () => {
    expect(isDownloadable(release({ status: 'Published' }))).toBe(true)
  })

  /** Revoked is withdrawn: nothing may download or install it any more. */
  it('refuses a revoked release', () => {
    expect(isDownloadable(release({ status: 'Revoked' }))).toBe(false)
  })

  /** Downloadable must not mean deployable. */
  it('a downloadable draft is still not a fleet target', () => {
    const draft = release({ version: '1.4.1', status: 'Draft', signerSubject: null })

    expect(isDownloadable(draft)).toBe(true)
    expect(newestUpdateEligible([draft])).toBeNull()
    expect(isEligible(
      { deviceId: 'd', hostname: 'PC', agentVersion: '1.1.4', status: 'Active' }, draft)).toBe(false)
  })

  it('says a draft is manual-install only, and flags an unsigned one', () => {
    expect(downloadHint(release({ status: 'Draft' })))
      .toContain('Not offered to devices until published')
    expect(downloadHint(release({ status: 'Draft', signerSubject: null })))
      .toContain('unsigned')
  })

  it('has nothing to add for a published release', () => {
    expect(downloadHint(release({ status: 'Published' }))).toBeNull()
  })
})
