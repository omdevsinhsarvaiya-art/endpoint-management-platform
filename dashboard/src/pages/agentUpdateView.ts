import type { AgentReleaseRow } from '../api/client'

/**
 * Which releases a device may actually be moved to, and why not otherwise.
 *
 * The rules live here rather than in the component because they are the whole
 * feature: an agent update runs an installer as SYSTEM on the target machine, so
 * offering an ineligible target is worse than offering none. The server enforces
 * every rule independently -- this exists so the console does not invite a click
 * that will be refused, and so the reason can be shown rather than a bare error.
 */

/** Why a device cannot receive a particular release. */
export type IneligibilityReason =
  | 'retired'
  | 'unpublished'
  | 'revoked'
  | 'not-newer'
  | 'unknown-version'

export interface DeviceUpdateCandidate {
  deviceId: string
  hostname: string
  agentVersion: string
  status: string
}

/**
 * Compares two three-part versions.
 *
 * Mirrors the server's `AgentVersionNumber`. Anything that does not parse as
 * three numeric parts is treated as unknown rather than guessed at: a device
 * whose reported version cannot be read must not be assumed to be behind.
 */
export function compareVersions(left: string, right: string): number | null {
  const parse = (v: string): number[] | null => {
    const core = v.split('+')[0].trim()
    const parts = core.split('.')

    if (parts.length !== 3) return null
    if (!parts.every((p) => /^\d+$/.test(p))) return null

    return parts.map(Number)
  }

  const a = parse(left)
  const b = parse(right)

  if (a === null || b === null) return null

  for (let i = 0; i < 3; i++) {
    if (a[i] !== b[i]) return a[i] - b[i]
  }

  return 0
}

/** Whether `release` is strictly newer than `installed`. */
export function isNewer(release: string, installed: string): boolean {
  const c = compareVersions(release, installed)
  return c !== null && c > 0
}

/**
 * Why this device cannot take this release, or null when it can.
 *
 * Deliberately returns the first blocking reason rather than a list: the
 * operator needs to know what to fix, and the reasons are not independent.
 */
export function ineligibilityReason(
  device: DeviceUpdateCandidate,
  release: AgentReleaseRow,
): IneligibilityReason | null {
  // Checked first: an offboarded machine is not a target whatever its version.
  if (device.status === 'Retired') return 'retired'

  // A revoked release was published once and has since been withdrawn; a draft
  // never was. Distinguished because the operator's next step differs.
  if (release.status === 'Revoked') return 'revoked'
  if (release.status !== 'Published') return 'unpublished'

  if (compareVersions(release.version, device.agentVersion) === null) {
    return 'unknown-version'
  }

  // Covers both same-version reinstalls and downgrades, which the platform
  // does not offer.
  if (!isNewer(release.version, device.agentVersion)) return 'not-newer'

  return null
}

export function isEligible(device: DeviceUpdateCandidate, release: AgentReleaseRow): boolean {
  return ineligibilityReason(device, release) === null
}

export function describeIneligibility(reason: IneligibilityReason): string {
  switch (reason) {
    case 'retired':
      return 'Retired devices cannot be updated'
    case 'unpublished':
      return 'That release is not published'
    case 'revoked':
      return 'That release has been revoked'
    case 'not-newer':
      return 'Already on this version or newer'
    case 'unknown-version':
      return 'The reported agent version could not be read'
  }
}

/**
 * The devices a bulk update would actually touch.
 *
 * The single most important function here. "Update all" must never mean every
 * row in the database: it means every device the operator can see, that is not
 * retired, and that is genuinely behind the chosen release. Anything else is
 * excluded and counted, so the confirmation can say what was left out instead of
 * silently narrowing.
 */
export function eligibleDevices(
  devices: DeviceUpdateCandidate[],
  release: AgentReleaseRow,
): DeviceUpdateCandidate[] {
  return devices.filter((d) => isEligible(d, release))
}

/** Releases this device could move to, newest first. */
export function upgradeTargets(
  device: DeviceUpdateCandidate,
  releases: AgentReleaseRow[],
): AgentReleaseRow[] {
  return releases
    .filter((r) => isEligible(device, r))
    .sort((a, b) => compareVersions(b.version, a.version) ?? 0)
}

/** A short summary of what a bulk action will and will not do. */
export function describeSelection(
  devices: DeviceUpdateCandidate[],
  release: AgentReleaseRow,
): string {
  const eligible = eligibleDevices(devices, release).length
  const skipped = devices.length - eligible

  if (eligible === 0) {
    return `No selected device can take ${release.version}.`
  }

  const plural = eligible === 1 ? 'device' : 'devices'
  const tail = skipped > 0 ? `, ${skipped} skipped as ineligible` : ''

  return `${eligible} ${plural} will be updated to ${release.version}${tail}.`
}

/**
 * Whether a release carries an Authenticode signature.
 *
 * Surfaced rather than enforced here: the agent verifies the signature itself
 * before installing, and that check is not something the console can or should
 * relax. Showing it means an operator can see why an unsigned development build
 * will be refused on the endpoint, instead of discovering it as a failed task.
 */
export function isSigned(release: AgentReleaseRow): boolean {
  return release.signerSubject !== null && release.signerSubject.trim().length > 0
}

export function signingLabel(release: AgentReleaseRow): string {
  return isSigned(release)
    ? `Signed by ${release.signerSubject}`
    : 'Unsigned — the agent will refuse to install this'
}

/**
 * Adapts a device list row to the shape the targeting rules work on.
 *
 * Exists so the console has one definition of "which device" rather than two
 * that can drift: the detail page and the estate list both go through here.
 */
export function toCandidate(device: {
  id: string
  hostname: string
  agentVersion: string
  status: string
}): DeviceUpdateCandidate {
  return {
    deviceId: device.id,
    hostname: device.hostname,
    agentVersion: device.agentVersion,
    status: device.status,
  }
}

/** Releases that may be chosen as a bulk target, newest first. */
export function publishedReleases(releases: AgentReleaseRow[]): AgentReleaseRow[] {
  return releases
    .filter((r) => r.status === 'Published')
    .sort((a, b) => compareVersions(b.version, a.version) ?? 0)
}

/** The outcome of queueing one device's update. */
export interface UpdateOutcome {
  hostname: string
  error: string | null
}

/**
 * What to tell the operator after a bulk run.
 *
 * Failures are named rather than counted away. A partial success is the normal
 * case -- the server re-checks every rule independently and may refuse a device
 * the console thought was eligible -- so it must read as a partial success and
 * not as either a clean pass or a total failure.
 */
export function summariseResults(results: UpdateOutcome[]): string {
  const failed = results.filter((r) => r.error !== null)
  const queued = results.length - failed.length

  if (results.length === 0) return 'Nothing was queued.'

  if (failed.length === 0) {
    return `Update queued on ${queued} device${queued === 1 ? '' : 's'}.`
  }

  if (queued === 0) {
    return `No update was queued. ${failed.length} device${failed.length === 1 ? '' : 's'} refused.`
  }

  return `Update queued on ${queued} of ${results.length} devices; ${failed.length} refused.`
}
