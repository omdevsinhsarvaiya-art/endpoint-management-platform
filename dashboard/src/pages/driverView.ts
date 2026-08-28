import type { BitLockerVolumeRow, DriverRow } from '../api/client'

/**
 * Presentation decisions for the Driver and BitLocker panels, kept out of the
 * components so they can be asserted directly.
 *
 * The rules that matter are the ones about *absence*. A driver whose problem
 * state could not be read, and a volume on a machine that refused the BitLocker
 * query, must both render as unknown — never as healthy, and never as
 * unencrypted. The server already draws those distinctions; the console's job is
 * to carry them through to the screen rather than flatten them into a green tick.
 */

/** Badge tones available in the stylesheet. */
export type Tone = 'ok' | 'warn' | 'crit' | 'neutral' | 'info'

// ---------------------------------------------------------------- drivers

/**
 * How a driver health state should read.
 *
 * `Disabled` is deliberately `neutral`, not a warning. This platform disables
 * devices itself — USB storage restriction is exactly that — so a restricted
 * stick showing an amber "problem" badge would make correct enforcement look
 * like damage, on every managed endpoint at once.
 *
 * `Unknown` is `warn` rather than `neutral`: it is not a fault, but it is a gap
 * in what we know, and a reader should feel the difference between "fine" and
 * "we could not tell".
 */
export function driverHealthTone(health: string): Tone {
  switch (health) {
    case 'Healthy':
      return 'ok'
    case 'Problem':
      return 'crit'
    case 'Disabled':
      return 'neutral'
    default:
      return 'warn'
  }
}

export function driverHealthLabel(health: string): string {
  switch (health) {
    case 'Healthy':
      return 'Healthy'
    case 'Problem':
      return 'Problem'
    case 'Disabled':
      return 'Disabled'
    default:
      return 'Unknown'
  }
}

/**
 * What a fault is attributable to, phrased as the remedy rather than the label.
 *
 * An operator reading this is deciding what to do next, and "the driver is at
 * fault" and "the hardware is at fault" lead to different actions. Where Windows
 * does not say, neither do we.
 */
export function faultKindLabel(faultKind: string): string | null {
  switch (faultKind) {
    case 'Driver':
      return 'Driver fault'
    case 'Device':
      return 'Hardware fault'
    case 'Indeterminate':
      return 'Unattributed'
    default:
      return null
  }
}

/**
 * Signature state as three values, because it is three-valued.
 *
 * Null means the question could not be answered — commonly, and reporting it as
 * "unsigned" would slander a correctly signed driver.
 */
export function signedLabel(isSigned: boolean | null): { text: string; tone: Tone } {
  if (isSigned === true) return { text: 'Signed', tone: 'ok' }
  if (isSigned === false) return { text: 'Unsigned', tone: 'crit' }
  return { text: 'Unknown', tone: 'neutral' }
}

/**
 * Orders drivers so the ones needing action come first.
 *
 * Problems, then unknowns, then disabled, then healthy; within a group, by name.
 * A reader scrolling a few hundred devices should meet the actionable rows
 * immediately rather than hunting for them.
 */
export function compareDrivers(a: DriverRow, b: DriverRow): number {
  const rank = (d: DriverRow) =>
    d.health === 'Problem' ? 0 : d.health === 'Unknown' ? 1 : d.health === 'Disabled' ? 2 : 3

  const byRank = rank(a) - rank(b)
  if (byRank !== 0) return byRank

  return a.deviceName.localeCompare(b.deviceName, undefined, { sensitivity: 'base' })
}

/** A driver date is only shown when the endpoint reported one. */
export function formatDriverDate(value: string | null): string {
  if (!value) return '—'

  const date = new Date(value)
  return Number.isNaN(date.getTime()) ? '—' : date.toLocaleDateString()
}

// -------------------------------------------------------------- BitLocker

/**
 * Whether BitLocker answers can be trusted at all.
 *
 * The distinction this preserves is the one the whole availability field exists
 * for: `AccessDenied` and `Error` mean the endpoint would not say, which is not
 * the same as a machine with nothing encrypted. Rendering either as "no volumes"
 * would show an encrypted estate as plaintext.
 */
export function availabilityNotice(availability: string): { text: string; tone: Tone } | null {
  switch (availability) {
    case 'Available':
      return null
    case 'AccessDenied':
      return {
        text:
          'The endpoint refused the BitLocker query, which normally means the agent is not '
          + 'running elevated. Volume state below is the last that could be read, and may be stale.',
        tone: 'warn',
      }
    case 'NotAvailable':
      return {
        text: 'BitLocker is not available on this edition of Windows.',
        tone: 'neutral',
      }
    case 'Error':
      return {
        text: 'The BitLocker query failed on the endpoint. Encryption state is unknown, not absent.',
        tone: 'warn',
      }
    default:
      return {
        text: 'This endpoint has not reported BitLocker state yet.',
        tone: 'neutral',
      }
  }
}

export function readinessTone(readiness: string): Tone {
  switch (readiness) {
    case 'Protected':
      return 'ok'
    case 'Suspended':
      return 'crit'
    case 'TpmNotReady':
      return 'warn'
    case 'EncryptionInProgress':
      return 'info'
    case 'ReadyToEncrypt':
    case 'NotEncrypted':
      return 'warn'
    case 'NotSupported':
      return 'neutral'
    default:
      return 'warn'
  }
}

export function readinessLabel(readiness: string): string {
  switch (readiness) {
    case 'Protected':
      return 'Protected'
    case 'Suspended':
      return 'Protection suspended'
    case 'EncryptionInProgress':
      return 'Conversion in progress'
    case 'ReadyToEncrypt':
      return 'Not encrypted — ready to encrypt'
    case 'TpmNotReady':
      return 'Not encrypted — TPM not ready'
    case 'NotEncrypted':
      return 'Not encrypted'
    case 'NotSupported':
      return 'Not supported on this edition'
    default:
      return 'Unknown'
  }
}

/**
 * Per-volume state.
 *
 * `Suspended` is `crit` and never `ok`: the volume is encrypted on disk but its
 * key is available without the protectors, which is materially weaker than
 * protected and is a state somebody chose to leave it in.
 */
export function volumeStateTone(state: string): Tone {
  switch (state) {
    case 'Protected':
      return 'ok'
    case 'Suspended':
      return 'crit'
    case 'NotEncrypted':
      return 'warn'
    case 'EncryptionInProgress':
    case 'DecryptionInProgress':
      return 'info'
    default:
      return 'warn'
  }
}

export function volumeStateLabel(state: string): string {
  switch (state) {
    case 'Protected':
      return 'Protected'
    case 'Suspended':
      return 'Suspended'
    case 'NotEncrypted':
      return 'Not encrypted'
    case 'EncryptionInProgress':
      return 'Encrypting'
    case 'DecryptionInProgress':
      return 'Decrypting'
    default:
      return 'Unknown'
  }
}

/** Win32_EncryptableVolume VolumeType. */
export function volumeTypeLabel(volumeType: number | null): string {
  switch (volumeType) {
    case 0:
      return 'Operating system'
    case 1:
      return 'Fixed data'
    case 2:
      return 'Removable data'
    default:
      return 'Unknown'
  }
}

/**
 * Win32_EncryptableVolume GetEncryptionMethod.
 *
 * An unrecognised value is shown as its number rather than hidden: a cipher this
 * console does not know the name of is still a fact worth putting on screen.
 */
export function encryptionMethodLabel(method: number | null): string {
  switch (method) {
    case null:
    case undefined:
      return '—'
    case 0:
      return 'None'
    case 1:
      return 'AES 128 with diffuser'
    case 2:
      return 'AES 256 with diffuser'
    case 3:
      return 'AES 128'
    case 4:
      return 'AES 256'
    case 5:
      return 'Hardware encryption'
    case 6:
      return 'XTS-AES 128'
    case 7:
      return 'XTS-AES 256'
    default:
      return `Method ${method}`
  }
}

/**
 * Encryption progress, shown only when the endpoint reported it.
 *
 * A missing percentage renders as an em dash rather than 0%, because zero
 * percent reads as "not encrypted at all" and that is a different claim from
 * "we do not know how far it got".
 */
export function encryptionProgress(volume: BitLockerVolumeRow): string {
  if (volume.encryptionPercentage === null || volume.encryptionPercentage === undefined) {
    return '—'
  }

  return `${volume.encryptionPercentage}%`
}

/**
 * How the recovery protector is described.
 *
 * Presence and identity only. There is deliberately no branch here that could
 * render key material: the API does not return any, the agent never reads any,
 * and this function has nothing to reveal even if asked.
 */
export function recoveryProtectorSummary(volume: BitLockerVolumeRow): string {
  if (volume.hasRecoveryPasswordProtector === true) {
    const count = volume.recoveryProtectorIds?.length ?? 0
    return count === 1 ? '1 recovery protector' : `${count} recovery protectors`
  }

  if (volume.hasRecoveryPasswordProtector === false) {
    return 'None'
  }

  return 'Unknown'
}

/** Volumes ordered by drive letter, unlettered volumes last. */
export function compareVolumes(a: BitLockerVolumeRow, b: BitLockerVolumeRow): number {
  if (!a.driveLetter && !b.driveLetter) {
    return a.deviceIdentifier.localeCompare(b.deviceIdentifier)
  }

  if (!a.driveLetter) return 1
  if (!b.driveLetter) return -1

  return a.driveLetter.localeCompare(b.driveLetter)
}
