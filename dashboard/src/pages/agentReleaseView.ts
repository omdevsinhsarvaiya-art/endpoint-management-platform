import type { AgentReleaseRow } from '../api/client'
import { compareVersions, isSigned } from './agentUpdateView'

/**
 * Which release is "the latest", asked three different ways.
 *
 * The page used to have one answer -- the first Published row in a list ordered
 * by upload time -- and that answer was wrong twice over. It ordered by when a
 * release was uploaded rather than by its version, so publishing an older build
 * after a newer one would have made the older one "latest". And it collapsed
 * three genuinely different questions into one, so a build that existed but was
 * not published was indistinguishable from one that did not exist at all.
 *
 * The three questions an operator actually has:
 *
 *   - What is the newest build we hold?      (newestUploaded)
 *   - What is the newest build we offer?     (newestPublished)
 *   - What would a fleet update install?     (newestUpdateEligible)
 *
 * They can all differ, and when they do the difference is the useful part.
 */

/** Highest version among the given releases, or null when none has a readable version. */
function highestVersion(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  const readable = releases.filter((r) => compareVersions(r.version, '0.0.0') !== null)

  if (readable.length === 0) return null

  return readable.reduce((best, candidate) =>
    (compareVersions(candidate.version, best.version) ?? 0) > 0 ? candidate : best,
  )
}

/**
 * The newest build the platform holds, whatever its status.
 *
 * Includes drafts. A build that has been uploaded but deliberately not published
 * is exactly the case the old logic could not show, and it is the case that
 * matters when an operator is asking "did the new agent ever get registered?".
 */
export function newestUploaded(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  return highestVersion(releases.filter((r) => r.status !== 'Revoked'))
}

/** The newest build currently offered for download and self-update. */
export function newestPublished(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  return highestVersion(releases.filter((r) => r.status === 'Published'))
}

/**
 * The newest build a device could actually be moved to.
 *
 * Mirrors the server: a release is targetable when it is Published and its
 * version parses. Deliberately <em>not</em> narrowed by signature, because
 * narrowing it here would be a lie -- the platform will queue and install an
 * unsigned published release, and a console that quietly hid that would leave an
 * operator believing a protection exists that does not. The signature is
 * reported instead, by {@link deploymentWarning}.
 */
export function newestUpdateEligible(releases: AgentReleaseRow[]): AgentReleaseRow | null {
  return newestPublished(releases)
}

/** How the three answers relate, for a reader who has not thought about it. */
export type ReleaseGapReason = 'none' | 'unpublished-newer' | 'nothing-published' | 'no-releases'

/**
 * Whether the newest build we hold is the newest build we would deploy.
 *
 * The whole point of the summary. A newer draft sitting above the published one
 * is a normal, deliberate state -- it is where an unsigned build belongs -- but
 * it must be visible rather than inferred from two cards showing two numbers.
 */
export function releaseGap(releases: AgentReleaseRow[]): ReleaseGapReason {
  const uploaded = newestUploaded(releases)
  const eligible = newestUpdateEligible(releases)

  if (uploaded === null) return 'no-releases'
  if (eligible === null) return 'nothing-published'

  return (compareVersions(uploaded.version, eligible.version) ?? 0) > 0
    ? 'unpublished-newer'
    : 'none'
}

export function describeReleaseGap(
  reason: ReleaseGapReason,
  uploaded: AgentReleaseRow | null,
  eligible: AgentReleaseRow | null,
): string {
  switch (reason) {
    case 'no-releases':
      return 'No agent releases have been registered.'
    case 'nothing-published':
      return `${uploaded?.version ?? 'A release'} is registered but nothing is published, so no device can be updated.`
    case 'unpublished-newer':
      return `${uploaded?.version} is registered but not published. Fleet updates still target ${eligible?.version}.`
    case 'none':
      return 'The newest registered release is the one devices will be updated to.'
  }
}

/**
 * What an operator must know before publishing this build.
 *
 * Returns null when there is nothing to say. The unsigned case is the one that
 * matters: this platform does not refuse to publish an unsigned release, and the
 * agent installs one on hash verification alone while logging a warning. That is
 * a deliberate, documented development stance -- but it means "unsigned" is a
 * property the operator has to weigh, not one the system will weigh for them.
 */
export function deploymentWarning(release: AgentReleaseRow): string | null {
  if (release.status !== 'Published' && !isSigned(release)) {
    return 'This release is unsigned. Publishing it makes it installable fleet-wide on '
      + 'hash verification alone, with no signature check.'
  }

  if (release.status === 'Published' && !isSigned(release)) {
    return 'This published release is unsigned. Devices will install it after verifying '
      + 'its SHA-256 only.'
  }

  return null
}

/** Whether publishing this release needs an explicit acknowledgement first. */
export function requiresUnsignedAcknowledgement(release: AgentReleaseRow): boolean {
  return !isSigned(release)
}
