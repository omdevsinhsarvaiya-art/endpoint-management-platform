import { describe, expect, it } from 'vitest'
import {
  availabilityNotice,
  compareDrivers,
  compareVolumes,
  driverHealthLabel,
  driverHealthTone,
  encryptionMethodLabel,
  encryptionProgress,
  faultKindLabel,
  formatDriverDate,
  readinessLabel,
  readinessTone,
  recoveryProtectorSummary,
  signedLabel,
  volumeStateLabel,
  volumeStateTone,
  volumeTypeLabel,
} from '../../pages/driverView'
import type { BitLockerVolumeRow, DriverRow } from '../../api/client'

/**
 * What the Driver and BitLocker panels decide before anything reaches the screen.
 *
 * Three of these carry real weight. **A disabled device must not read as a
 * fault** — this platform disables devices itself, so getting it wrong would
 * paint every USB-restricted endpoint in the estate as broken. **Unknown must
 * not read as healthy or as unencrypted** — an endpoint that would not answer
 * has told us nothing, and a green tick there is a lie. And **suspended must not
 * read as protected** — the volume is encrypted but its key is sitting in the
 * clear, which is the whole point of distinguishing the two.
 */

function driver(overrides: Partial<DriverRow> = {}): DriverRow {
  return {
    instanceId: 'PCI\\VEN_8086&DEV_1234\\3&11583659&0&10',
    deviceName: 'Contoso NIC',
    deviceClass: 'Net',
    manufacturer: 'Contoso',
    driverProvider: 'Contoso Inc',
    driverVersion: '2.0.0.0',
    driverDate: '2026-01-15T00:00:00Z',
    infName: 'oem42.inf',
    problemCode: 0,
    health: 'Healthy',
    faultKind: 'None',
    problemDescription: 'This device is working properly.',
    isSigned: true,
    collectedAt: '2026-08-28T12:00:00Z',
    ...overrides,
  }
}

function volume(overrides: Partial<BitLockerVolumeRow> = {}): BitLockerVolumeRow {
  return {
    deviceIdentifier: '\\\\?\\Volume{11111111-1111-1111-1111-111111111111}\\',
    driveLetter: 'C:',
    persistentVolumeId: 'pv-1',
    volumeType: 0,
    conversionStatus: 1,
    protectionStatus: 1,
    state: 'Protected',
    encryptionPercentage: 100,
    encryptionMethod: 7,
    hasRecoveryPasswordProtector: true,
    recoveryProtectorIds: ['3f2504e0-4f89-11d3-9a0c-0305e82c3301'],
    collectedAt: '2026-08-28T12:00:00Z',
    ...overrides,
  }
}

describe('driver health presentation', () => {
  it('shows a healthy device as ok', () => {
    expect(driverHealthTone('Healthy')).toBe('ok')
    expect(driverHealthLabel('Healthy')).toBe('Healthy')
  })

  it('shows a faulted device as critical', () => {
    expect(driverHealthTone('Problem')).toBe('crit')
  })

  /**
   * Milestone 11a's USB storage restriction disables devices, which reports
   * CM_PROB_DISABLED. If that rendered as a warning or a fault, correctly
   * securing a fleet would light up every endpoint in it.
   */
  it('shows an administratively disabled device as neutral, never as a fault', () => {
    expect(driverHealthTone('Disabled')).toBe('neutral')
    expect(driverHealthTone('Disabled')).not.toBe('crit')
    expect(driverHealthTone('Disabled')).not.toBe('warn')
    expect(driverHealthLabel('Disabled')).toBe('Disabled')
  })

  /** Not a fault, but not reassurance either: it is a gap in what we know. */
  it('shows an unreadable device as a warning, never as ok', () => {
    expect(driverHealthTone('Unknown')).toBe('warn')
    expect(driverHealthTone('Unknown')).not.toBe('ok')
    expect(driverHealthLabel('Unknown')).toBe('Unknown')
  })

  it('names what a fault is attributable to, and stays silent when there is nothing to say', () => {
    expect(faultKindLabel('Driver')).toBe('Driver fault')
    expect(faultKindLabel('Device')).toBe('Hardware fault')
    expect(faultKindLabel('Indeterminate')).toBe('Unattributed')
    expect(faultKindLabel('None')).toBeNull()
  })

  it('reports signature state as three values rather than two', () => {
    expect(signedLabel(true)).toEqual({ text: 'Signed', tone: 'ok' })
    expect(signedLabel(false)).toEqual({ text: 'Unsigned', tone: 'crit' })

    // The common case, and reporting it as unsigned would slander a signed driver.
    expect(signedLabel(null)).toEqual({ text: 'Unknown', tone: 'neutral' })
  })

  it('puts the devices needing action first', () => {
    const rows = [
      driver({ deviceName: 'Healthy device', health: 'Healthy' }),
      driver({ deviceName: 'Disabled stick', health: 'Disabled' }),
      driver({ deviceName: 'Unreadable', health: 'Unknown' }),
      driver({ deviceName: 'Broken GPU', health: 'Problem' }),
    ]

    expect(rows.sort(compareDrivers).map((d) => d.health))
      .toEqual(['Problem', 'Unknown', 'Disabled', 'Healthy'])
  })

  it('shows a missing driver date as absent rather than as an epoch', () => {
    expect(formatDriverDate(null)).toBe('—')
    expect(formatDriverDate('not a date')).toBe('—')
    expect(formatDriverDate('2026-01-15T00:00:00Z')).not.toBe('—')
  })
})

describe('BitLocker availability', () => {
  it('says nothing when the endpoint answered', () => {
    expect(availabilityNotice('Available')).toBeNull()
  })

  /**
   * The case the whole availability field exists for. An agent that lost its
   * elevation must not leave a reader thinking the machine is unencrypted.
   */
  it('explains a refused query rather than implying the machine is unencrypted', () => {
    const notice = availabilityNotice('AccessDenied')

    expect(notice).not.toBeNull()
    expect(notice!.tone).toBe('warn')
    expect(notice!.text).toContain('elevated')
    expect(notice!.text.toLowerCase()).not.toContain('not encrypted')
  })

  it('treats an unsupported edition as an answer, not a failure', () => {
    expect(availabilityNotice('NotAvailable')!.tone).toBe('neutral')
  })

  it('flags a failed query as unknown state', () => {
    expect(availabilityNotice('Error')!.text).toContain('unknown, not absent')
  })

  it('explains an endpoint that has reported nothing', () => {
    expect(availabilityNotice('Unknown')).not.toBeNull()
  })
})

describe('BitLocker readiness presentation', () => {
  it('shows a protected endpoint as ok', () => {
    expect(readinessTone('Protected')).toBe('ok')
    expect(readinessLabel('Protected')).toBe('Protected')
  })

  /**
   * Suspended is encrypted-but-unprotected: the key is available without its
   * protectors. Materially weaker than protected, and somebody chose it.
   */
  it('shows suspended protection as critical, never as ok', () => {
    expect(readinessTone('Suspended')).toBe('crit')
    expect(readinessTone('Suspended')).not.toBe('ok')
    expect(readinessLabel('Suspended')).toContain('suspended')
  })

  it('separates ready-to-encrypt from blocked-by-TPM, because the remedy differs', () => {
    expect(readinessLabel('ReadyToEncrypt')).toContain('ready to encrypt')
    expect(readinessLabel('TpmNotReady')).toContain('TPM not ready')
    expect(readinessTone('TpmNotReady')).toBe('warn')
  })

  it('never shows an unknown endpoint as ok', () => {
    expect(readinessTone('Unknown')).not.toBe('ok')
    expect(readinessLabel('Unknown')).toBe('Unknown')
  })

  it('shows an unsupported edition as neutral rather than a problem', () => {
    expect(readinessTone('NotSupported')).toBe('neutral')
  })
})

describe('BitLocker volume presentation', () => {
  it('shows a protected volume as ok and a suspended one as critical', () => {
    expect(volumeStateTone('Protected')).toBe('ok')
    expect(volumeStateTone('Suspended')).toBe('crit')
    expect(volumeStateLabel('Suspended')).toBe('Suspended')
  })

  it('never shows an unknown volume as ok', () => {
    expect(volumeStateTone('Unknown')).not.toBe('ok')
    expect(volumeStateLabel('Unknown')).toBe('Unknown')
  })

  it('names the volume type', () => {
    expect(volumeTypeLabel(0)).toBe('Operating system')
    expect(volumeTypeLabel(1)).toBe('Fixed data')
    expect(volumeTypeLabel(2)).toBe('Removable data')
    expect(volumeTypeLabel(null)).toBe('Unknown')
  })

  it('names known encryption methods and shows unknown ones rather than hiding them', () => {
    expect(encryptionMethodLabel(7)).toBe('XTS-AES 256')
    expect(encryptionMethodLabel(4)).toBe('AES 256')
    expect(encryptionMethodLabel(0)).toBe('None')
    expect(encryptionMethodLabel(null)).toBe('—')
    expect(encryptionMethodLabel(99)).toBe('Method 99')
  })

  /**
   * Zero percent reads as "not encrypted at all", which is a different claim
   * from "we do not know how far it got".
   */
  it('shows an unread percentage as absent rather than as zero', () => {
    expect(encryptionProgress(volume({ encryptionPercentage: null }))).toBe('—')
    expect(encryptionProgress(volume({ encryptionPercentage: 0 }))).toBe('0%')
    expect(encryptionProgress(volume({ encryptionPercentage: 100 }))).toBe('100%')
  })

  it('orders volumes by drive letter, unlettered last', () => {
    const rows = [
      volume({ driveLetter: null, deviceIdentifier: '\\\\?\\Volume{z}\\' }),
      volume({ driveLetter: 'D:', deviceIdentifier: '\\\\?\\Volume{d}\\' }),
      volume({ driveLetter: 'C:', deviceIdentifier: '\\\\?\\Volume{c}\\' }),
    ]

    expect(rows.sort(compareVolumes).map((v) => v.driveLetter)).toEqual(['C:', 'D:', null])
  })
})

describe('recovery protectors', () => {
  it('reports presence and count, never key material', () => {
    expect(recoveryProtectorSummary(volume())).toBe('1 recovery protector')

    expect(recoveryProtectorSummary(volume({
      recoveryProtectorIds: ['a', 'b'],
    }))).toBe('2 recovery protectors')
  })

  it('distinguishes no protector from an unknown one', () => {
    expect(recoveryProtectorSummary(volume({ hasRecoveryPasswordProtector: false })))
      .toBe('None')

    expect(recoveryProtectorSummary(volume({ hasRecoveryPasswordProtector: null })))
      .toBe('Unknown')
  })

  /**
   * A structural check on the whole module: nothing it can produce resembles a
   * BitLocker recovery password. There is no branch that could render one — the
   * API returns no such field — and this asserts it stays that way.
   */
  it('produces nothing shaped like a recovery key, whatever it is handed', () => {
    const hostile = volume({
      driveLetter: '123456-654321-111111-222222-333333-444444-555555-666666',
      persistentVolumeId: '123456-654321-111111-222222-333333-444444-555555-666666',
      recoveryProtectorIds: ['123456-654321-111111-222222-333333-444444-555555-666666'],
    })

    const rendered = [
      recoveryProtectorSummary(hostile),
      encryptionProgress(hostile),
      volumeStateLabel(hostile.state),
      volumeTypeLabel(hostile.volumeType),
      encryptionMethodLabel(hostile.encryptionMethod),
    ].join(' | ')

    expect(rendered).not.toMatch(/\d{6}-\d{6}/)
  })
})
