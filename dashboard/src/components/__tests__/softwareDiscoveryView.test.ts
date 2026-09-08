import { describe, expect, it } from 'vitest'
import {
  SYSTEM_CATEGORIES,
  categoryLabel,
  commonName,
  confidenceLabel,
  confidenceTone,
  evidenceSourceLabel,
  isApplicationRow,
  signerLabel,
} from '../../pages/softwareView'

/**
 * The words the console uses for what application discovery reports: what a
 * row is, how the endpoint knows it is there, who signed it, and why.
 */

describe('default view', () => {
  it('hides frameworks, inbox apps and components and shows everything else', () => {
    for (const hidden of SYSTEM_CATEGORIES) {
      expect(isApplicationRow(hidden)).toBe(false)
    }
    expect(isApplicationRow('Application')).toBe(true)
    expect(isApplicationRow('RuntimeOrSdk')).toBe(true)
    expect(isApplicationRow('Observed')).toBe(true)
  })

  /**
   * A row from an agent older than 1.9.0 has no category. Hiding it would make
   * an upgrade of the server look like software vanishing from every device
   * that has not yet been updated.
   */
  it('shows a row whose category was never reported', () => {
    expect(isApplicationRow(null)).toBe(true)
    expect(isApplicationRow(undefined)).toBe(true)
  })

  it('agrees with the server about which categories are system ones', () => {
    expect([...SYSTEM_CATEGORIES].sort()).toEqual(['Component', 'FrameworkOrResource', 'InboxApp'])
  })
})

describe('category label', () => {
  it('names every category the endpoint can produce', () => {
    expect(categoryLabel('Application')).toBe('Application')
    expect(categoryLabel('InboxApp')).toBe('Windows app')
    expect(categoryLabel('RuntimeOrSdk')).toBe('Runtime / SDK')
    expect(categoryLabel('FrameworkOrResource')).toBe('Framework / resources')
    expect(categoryLabel('Component')).toBe('System component')
    expect(categoryLabel('Observed')).toBe('Running only')
    expect(categoryLabel('Transient')).toBe('Installer / updater')
  })

  it('says when no category was reported and shows an unknown one as itself', () => {
    expect(categoryLabel(null)).toBe('Not reported')
    expect(categoryLabel('SomethingNewer')).toBe('SomethingNewer')
  })
})

describe('confidence', () => {
  it('says plainly when a row is only a running process', () => {
    expect(confidenceLabel('Observed')).toBe('Running only')
    expect(confidenceTone('Observed')).toBe('warn')
  })

  it('treats an installation record as installed', () => {
    expect(confidenceLabel('Installed')).toBe('Installed')
    expect(confidenceTone('Installed')).toBe('neutral')
  })

  /** Older agents reported only installation records; the absence is theirs, not a doubt. */
  it('does not present an older agent silence as doubt', () => {
    expect(confidenceLabel(null)).toBe('Installed (not stated)')
    expect(confidenceTone(null)).toBe('neutral')
  })
})

describe('evidence', () => {
  it('names installation records as records and hints as hints', () => {
    expect(evidenceSourceLabel('WindowsInstaller')).toBe('Windows Installer product')
    expect(evidenceSourceLabel('UninstallRegistry')).toBe('Uninstall registration')
    expect(evidenceSourceLabel('PackageRegistration')).toBe('Package registration')
    expect(evidenceSourceLabel('AppPaths')).toBe('Execution alias')
    expect(evidenceSourceLabel('StartMenuShortcut')).toBe('Start Menu shortcut')
    expect(evidenceSourceLabel('ExecutableMetadata')).toBe('Executable metadata')
    expect(evidenceSourceLabel('RunningProcess')).toBe('Running process')
    expect(evidenceSourceLabel('Future')).toBe('Future')
  })
})

describe('signer', () => {
  it('shows the common name of a signed executable, never a trust claim', () => {
    expect(signerLabel('CN=Google LLC, O=Google LLC, L=Mountain View', 'Signed')).toBe('Google LLC')
    expect(signerLabel('CN="Slack Technologies, LLC", O="Slack Technologies, LLC"', 'Signed')).toBe(
      'Slack Technologies, LLC',
    )
  })

  it('distinguishes unsigned from unreadable from unknown', () => {
    expect(signerLabel(null, 'Unsigned')).toBe('Unsigned')
    expect(signerLabel(null, 'Unreadable')).toBe('Signature unreadable')
    expect(signerLabel(null, null)).toBe('—')
    expect(signerLabel(null, 'Signed')).toBe('Signed')
  })

  it('reads a common name out of any subject shape', () => {
    expect(commonName('O=Contoso, CN=Contoso Ltd')).toBe('Contoso Ltd')
    expect(commonName('CN=Solo')).toBe('Solo')
    expect(commonName('O=No common name')).toBe('O=No common name')
  })
})
