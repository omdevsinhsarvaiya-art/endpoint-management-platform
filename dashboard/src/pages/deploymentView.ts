import type { DeploymentPlan, DeploymentTally } from '../api/client'

/**
 * Pure view logic for software deployment.
 *
 * Separated from the components so the rules an operator relies on — what a
 * deployment will actually do, and whether it is finished — are proven without a
 * DOM, in the same shape as softwareView and agentReleaseView.
 */

/** A device the operator can pick as a deployment target. */
export interface TargetCandidate {
  id: string
  hostname: string
  displayName: string | null
  status: string
  agentVersion: string
  lastSeenAt: string | null
}

/**
 * Devices that may be selected as targets.
 *
 * Retired devices are removed rather than shown disabled: they can never receive
 * a task, so offering one is offering an action that silently does nothing. The
 * server enforces this regardless — this only avoids presenting the choice.
 */
export function selectableTargets(devices: readonly TargetCandidate[]): TargetCandidate[] {
  return devices.filter((d) => d.status !== 'Retired')
}

/** Case-insensitive search over the names an operator actually sees. */
export function matchesSearch(device: TargetCandidate, search: string): boolean {
  const term = search.trim().toLowerCase()
  if (term === '') return true

  return device.hostname.toLowerCase().includes(term)
    || (device.displayName ?? '').toLowerCase().includes(term)
}

/**
 * The dialog's summary of what deploying would do.
 *
 * Every targeted device is accounted for in exactly one line. A resolution that
 * dropped devices silently would let an operator believe a deployment covered
 * machines it never touched.
 */
export function planLines(plan: DeploymentPlan): { label: string; value: number }[] {
  const lines = [
    { label: 'Target devices', value: plan.targeted },
    { label: 'Installation needed', value: plan.needsInstall },
    { label: 'Already installed', value: plan.alreadyInstalled },
  ]

  if (plan.newerInstalled > 0) {
    lines.push({ label: 'Newer version installed', value: plan.newerInstalled })
  }

  if (plan.notComparable > 0) {
    lines.push({ label: 'Version could not be compared', value: plan.notComparable })
  }

  if (plan.retired > 0) {
    lines.push({ label: 'Retired, excluded', value: plan.retired })
  }

  return lines
}

/**
 * Whether deploying would actually do anything.
 *
 * Everything already being correct is a success, not an error — but submitting
 * would create an empty deployment, so the button says so instead.
 */
export function hasWorkToDo(plan: DeploymentPlan | null): boolean {
  return plan !== null && plan.needsInstall > 0
}

/**
 * A deployment is finished when nothing is still moving.
 *
 * Derived from the tally rather than stored, so this can never claim completion
 * for a deployment whose tasks are still outstanding.
 */
export function isSettled(tally: DeploymentTally): boolean {
  // Offline counts as outstanding: the task is still queued and will run if the
  // device comes back before its TTL. Calling it settled would tell an operator
  // the deployment is finished while work is still owed.
  return tally.pending === 0 && tally.installing === 0 && tally.offline === 0
}

/**
 * Whether there is failed work a retry could act on.
 *
 * Expired and cancelled count: neither ever ran, so both are worth another
 * attempt. Skipped does not — a skipped device was deliberately not sent
 * anything, and retrying it would just skip it again.
 */
export function hasRetryableWork(tally: DeploymentTally): boolean {
  return tally.failed > 0 || tally.expired > 0 || tally.cancelled > 0
}

/** Whether anything is still queued and therefore cancellable. */
export function hasCancellableWork(tally: DeploymentTally): boolean {
  return tally.pending > 0 || tally.offline > 0
}

/** One line describing where a deployment has got to. */
export function tallySummary(tally: DeploymentTally): string {
  if (tally.total === 0) return 'No devices'

  const parts: string[] = []
  if (tally.succeeded > 0) parts.push(`${tally.succeeded} succeeded`)
  if (tally.installing > 0) parts.push(`${tally.installing} installing`)
  if (tally.pending > 0) parts.push(`${tally.pending} pending`)
  if (tally.offline > 0) parts.push(`${tally.offline} offline`)
  if (tally.failed > 0) parts.push(`${tally.failed} failed`)
  if (tally.expired > 0) parts.push(`${tally.expired} expired`)
  if (tally.cancelled > 0) parts.push(`${tally.cancelled} cancelled`)
  if (tally.skipped > 0) parts.push(`${tally.skipped} skipped`)

  return parts.join(', ')
}

/** The badge tone for a per-device status. */
export function statusTone(status: string): 'ok' | 'warn' | 'crit' | 'info' | 'neutral' {
  switch (status) {
    case 'Succeeded':
      return 'ok'
    case 'Installing':
      return 'info'
    case 'Failed':
      return 'crit'
    case 'Expired':
      return 'warn'
    // Waiting on a machine that is not answering. Not a failure, but not
    // progress either, so it must not read as either.
    case 'Offline':
      return 'warn'
    // Skipped is not a problem: it usually means the device was already correct.
    default:
      return 'neutral'
  }
}

/**
 * Why a device was skipped, in words rather than an enum name.
 *
 * "AlreadyInstalled" is the common and entirely good case, so it reads as a
 * statement of fact rather than a failure.
 */
export function reasonLabel(reason: string): string {
  switch (reason) {
    case 'InstallRequired':
      return 'Not installed'
    case 'UpdateRequired':
      return 'Older version present'
    case 'AlreadyInstalled':
      return 'Already at this version'
    case 'NewerInstalled':
      return 'Newer version present — not downgraded'
    case 'VersionNotComparable':
      return 'Installed version could not be compared'
    case 'Retired':
      return 'Device retired or not eligible'
    case 'NotPermitted':
      return 'Not permitted'
    case 'AlreadyInProgress':
      return 'An install of this package is already under way'
    default:
      return reason
  }
}
