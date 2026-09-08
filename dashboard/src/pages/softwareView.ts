import type { SoftwareInstallation, SoftwareTitle } from '../api/client'

/**
 * Pure view logic for the software inventory browser.
 *
 * Kept out of the component so the rules can be tested without a DOM, in the
 * same shape as agentReleaseView and elevationView.
 */

/**
 * Field separator for composite keys: ASCII Unit Separator.
 *
 * A character no application name, version or publisher can contain, so
 * ("A", "B|C") and ("A|B", "C") cannot collapse into one key and hide a title.
 */
const SEPARATOR = String.fromCharCode(31)

/** Marker for an absent field, distinct from a field that is genuinely empty text. */
const ABSENT = String.fromCharCode(0)

/**
 * A stable key for one title.
 *
 * A title is (name, version, publisher) — the same triple the server groups on.
 * An absent version or publisher is part of the identity, not a wildcard, so it
 * gets its own marker rather than colliding with a title whose value is literally
 * empty.
 */
export function titleKey(title: Pick<SoftwareTitle, 'name' | 'version' | 'publisher'>): string {
  const part = (value: string | null) => (value === null ? ABSENT : value)
  return [title.name, part(title.version), part(title.publisher)].join(SEPARATOR)
}

export function isSameTitle(
  a: Pick<SoftwareTitle, 'name' | 'version' | 'publisher'> | null,
  b: Pick<SoftwareTitle, 'name' | 'version' | 'publisher'>,
): boolean {
  return a !== null && titleKey(a) === titleKey(b)
}

/**
 * How an installation reached the machine, in words an operator can act on.
 *
 * A per-user install names its account because that is what makes it removable:
 * uninstalling it for one person leaves it running for everyone else. Agents
 * older than 1.5.0 could not tell, and that is said plainly rather than guessed
 * at — reporting "All users" for an unknown scope would be a claim the platform
 * has not earned.
 */
export function scopeLabel(
  installation: Pick<SoftwareInstallation, 'installationScope' | 'installedForUser'>,
): string {
  if (installation.installationScope === 'User') {
    return installation.installedForUser
      ? `Per-user — ${installation.installedForUser}`
      : 'Per-user'
  }

  if (installation.installationScope === 'Machine') {
    return 'All users'
  }

  return 'Scope not reported'
}

/**
 * What the registry view actually tells us.
 *
 * Deliberately NOT called architecture. The value is the uninstall key the entry
 * was found under, and 64-bit products routinely register under WOW6432Node —
 * Chrome, Edge and Brave all report x86. Presenting that as the binary's
 * architecture would state something the platform has not determined.
 */
export function registryViewLabel(architecture: string | null): string {
  if (architecture === 'x64') return '64-bit registry'
  if (architecture === 'x86') return '32-bit registry'
  return '—'
}

/**
 * One line summarising who has a title, for the detail header.
 *
 * Devices and installations are counted separately because they differ once
 * per-user installs exist: three people with the same product on one machine is
 * one device and three installations, and collapsing the two would either
 * overstate fleet coverage or hide work that still has to be done per user.
 */
export function installationSummary(
  installations: readonly SoftwareInstallation[],
  totalCount: number,
): string {
  if (totalCount === 0) return 'Not installed on any device in scope'

  const devices = new Set(installations.map((i) => i.deviceId)).size
  const deviceText = `${devices} device${devices === 1 ? '' : 's'}`

  if (totalCount === devices) return `Installed on ${deviceText}`

  return `${totalCount} installations across ${deviceText}`
}

/**
 * Whether Force Stop can be offered for an installed application.
 *
 * Force Stop resolves an application to its running processes by install path,
 * because a display name cannot be turned into an image name safely — "Microsoft
 * Visual Studio Code" runs as Code.exe. Without a path there is no evidence, so
 * the action is not offered rather than guessed at.
 *
 * The server re-checks this and is the authority; this only avoids showing a
 * control that is certain to report "unavailable".
 */
export function canForceStop(installLocation: string | null): boolean {
  return installLocation !== null && installLocation.trim().length > 0
}

/**
 * What a Force Stop attempt did, in words.
 *
 * Each outcome is distinct on purpose: "not running" and "cannot be resolved"
 * look the same to an operator who is only told it did not work, but only the
 * second means the action will never work for that application.
 */
export function forceStopMessage(application: string, outcome: string): string {
  switch (outcome) {
    case 'Queued':
      // "Asked to stop", not "stopped": the device checks which processes are
      // actually running and terminates them on its next check-in, so claiming
      // completion here would be a claim the console cannot support.
      return `${application} was asked to stop. The device checks which processes are running before stopping them.`
    case 'NotRunning':
      return `${application} is not running on this device.`
    case 'NotInstalled':
      return `${application} is not installed on this device.`
    case 'Unresolvable':
      return `Force Stop is unavailable for ${application}: it reports no usable install location.`
    case 'NotEligible':
      return `${application} could not be stopped: the device is retired or its agent is too old.`
    default:
      return `${application} could not be stopped.`
  }
}

/** Devices that can be acted on: a retired device is not a deployment target. */
export function activeInstallations(
  installations: readonly SoftwareInstallation[],
): SoftwareInstallation[] {
  return installations.filter((i) => i.deviceStatus === 'Active')
}

/**
 * The categories the endpoint labels as not-an-application: present on the
 * machine, collected, and hidden from the default view. Kept as the same list
 * the server hides for the fleet-wide titles, so the two views agree.
 */
export const SYSTEM_CATEGORIES: readonly string[] = ['FrameworkOrResource', 'InboxApp', 'Component']

/**
 * Whether a row belongs to the default view.
 *
 * A row with no category came from an agent that predates discovery and could
 * not say; it is shown rather than hidden by a label it never had.
 */
export function isApplicationRow(category: string | null | undefined): boolean {
  return category === null || category === undefined || !SYSTEM_CATEGORIES.includes(category)
}

/**
 * What kind of thing a row is, in words. The raw value is shown for anything
 * the console does not know, rather than nothing: a label a newer agent adds
 * is still information.
 */
export function categoryLabel(category: string | null | undefined): string {
  switch (category) {
    case 'Application':
      return 'Application'
    case 'InboxApp':
      return 'Windows app'
    case 'RuntimeOrSdk':
      return 'Runtime / SDK'
    case 'FrameworkOrResource':
      return 'Framework / resources'
    case 'Component':
      return 'System component'
    case 'Observed':
      return 'Running only'
    case 'Transient':
      return 'Installer / updater'
    case null:
    case undefined:
      return 'Not reported'
    default:
      return category
  }
}

/**
 * How the endpoint knows the application is there.
 *
 * "Observed" is the one that matters to say plainly: nothing installed it, a
 * process is the only witness, and it may be gone at the next inventory. An
 * agent older than 1.9.0 reported only installation records, so an absent
 * value is "Installed" in fact but is labelled as the older agent's silence.
 */
export function confidenceLabel(confidence: string | null | undefined): string {
  switch (confidence) {
    case 'Installed':
      return 'Installed'
    case 'Observed':
      return 'Running only'
    case null:
    case undefined:
      return 'Installed (not stated)'
    default:
      return confidence
  }
}

/** Badge tone for a confidence: an observed row is a warning, an installed one is neutral. */
export function confidenceTone(confidence: string | null | undefined): 'neutral' | 'warn' {
  return confidence === 'Observed' ? 'warn' : 'neutral'
}

/**
 * Which discovery source an evidence entry names, in words an operator can
 * act on. The three installation records are called what they are; the rest
 * are hints and are named as such.
 */
export function evidenceSourceLabel(source: string): string {
  switch (source) {
    case 'WindowsInstaller':
      return 'Windows Installer product'
    case 'UninstallRegistry':
      return 'Uninstall registration'
    case 'PackageRegistration':
      return 'Package registration'
    case 'AppPaths':
      return 'Execution alias'
    case 'StartMenuShortcut':
      return 'Start Menu shortcut'
    case 'ExecutableMetadata':
      return 'Executable metadata'
    case 'RunningProcess':
      return 'Running process'
    default:
      return source
  }
}

/**
 * What a signature status means for the signer shown next to it.
 *
 * Presence, not trust: "Signed" says the file carries a signature naming this
 * subject, or that Windows verified a package as this publisher. It does not
 * say the certificate chains to a trusted root, and the label must not imply
 * that it does.
 */
export function signerLabel(
  signerSubject: string | null | undefined,
  signatureStatus: string | null | undefined,
): string {
  if (signatureStatus === 'Signed' && signerSubject) return commonName(signerSubject)
  if (signatureStatus === 'Signed') return 'Signed'
  if (signatureStatus === 'Unsigned') return 'Unsigned'
  if (signatureStatus === 'Unreadable') return 'Signature unreadable'
  return '—'
}

/** The CN of a subject, or the subject itself when it has none; quoted values are unwrapped. */
export function commonName(subject: string): string {
  const at = subject.search(/CN=/i)
  if (at < 0) return subject

  const rest = subject.slice(at + 3).trimStart()
  if (rest.startsWith('"')) {
    const close = rest.indexOf('"', 1)
    return close < 0 ? rest.slice(1) : rest.slice(1, close)
  }

  const comma = rest.indexOf(',')
  return (comma < 0 ? rest : rest.slice(0, comma)).trim() || subject
}
