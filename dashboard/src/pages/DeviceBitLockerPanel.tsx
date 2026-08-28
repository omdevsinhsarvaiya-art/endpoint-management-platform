import { useCallback, useEffect, useState } from 'react'
import {
  deleteRecoveryKeyEscrow,
  getBitLockerEscrows,
  getDeviceBitLockerReadiness,
  getDeviceBitLockerVolumes,
  type BitLockerReadinessSummary,
  type BitLockerVolumeRow,
  type EscrowRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { EscrowKeyDialog, RevealKeyDialog } from './RecoveryKeyDialog'
import {
  activeEscrowFor,
  describeEscrow,
  escrowStatus,
  escrowStatusLabel,
  escrowStatusTone,
} from './escrowView'
import {
  availabilityNotice,
  compareVolumes,
  encryptionMethodLabel,
  encryptionProgress,
  readinessLabel,
  readinessTone,
  recoveryProtectorSummary,
  volumeStateLabel,
  volumeStateTone,
  volumeTypeLabel,
} from './driverView'

/**
 * BitLocker readiness and per-volume encryption state.
 *
 * Read-only, and structurally so: this panel has no control that encrypts,
 * decrypts, suspends or resumes anything. Those operations do not exist in the
 * platform yet, and when they arrive they will need their own permissions.
 *
 * **No recovery key is displayed and none can be requested.** What is shown is
 * that a recovery protector exists and the GUID identifying it. The agent never
 * reads the password, the API never returns it, and there is no control here
 * that could ask for it.
 *
 * The distinction the panel works hardest to preserve is between an unencrypted
 * machine and one that would not answer. An agent without elevation reports
 * AccessDenied, and rendering that as "no volumes" would show an encrypted
 * estate as plaintext.
 */
export function DeviceBitLockerPanel({ deviceId }: { deviceId: string }) {
  const { hasPermission } = useAuth()
  const canView = hasPermission('bitlocker.view')
  const canManageKeys = hasPermission('bitlocker.recovery_key.manage')
  const canReadKeys = hasPermission('bitlocker.recovery_key.read')

  const [summary, setSummary] = useState<BitLockerReadinessSummary | null>(null)
  const [volumes, setVolumes] = useState<BitLockerVolumeRow[]>([])
  const [escrows, setEscrows] = useState<EscrowRow[]>([])
  const [escrowing, setEscrowing] = useState<BitLockerVolumeRow | null>(null)
  const [revealing, setRevealing] = useState<EscrowRow | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      // Escrow METADATA only. Nothing here fetches a key: retrieval is a
      // separate route behind its own permission and a step-up password, and it
      // never happens on page load.
      const [readiness, rows, escrowRows] = await Promise.all([
        getDeviceBitLockerReadiness(deviceId),
        getDeviceBitLockerVolumes(deviceId),
        getBitLockerEscrows(deviceId),
      ])
      setSummary(readiness)
      setVolumes(rows)
      setEscrows(escrowRows)
    } catch {
      setError('BitLocker information could not be loaded.')
    } finally {
      setLoading(false)
    }
  }, [deviceId])

  useEffect(() => {
    if (canView) void load()
    else setLoading(false)
  }, [canView, load])

  if (!canView) {
    return (
      <div className="card">
        <p className="muted">You do not have permission to view BitLocker information.</p>
      </div>
    )
  }

  if (loading) return <p className="muted">Loading BitLocker state…</p>
  if (error) return <div className="warn-banner">{error}</div>
  if (!summary) return null

  const notice = availabilityNotice(summary.availability)
  const sorted = [...volumes].sort(compareVolumes)

  return (
    <>
      <div className="card">
        <h2>BitLocker readiness</h2>

        <div className="row-sub" style={{ marginBottom: 12 }}>
          <span className={`badge ${readinessTone(summary.readiness)}`}>
            {readinessLabel(summary.readiness)}
          </span>
          {summary.lastReportedAt && (
            <span className="muted">
              last reported {new Date(summary.lastReportedAt).toLocaleString()}
            </span>
          )}
        </div>

        {/* Carried beside the verdict rather than folded into it: a reader must be
            able to tell an unencrypted machine from one that refused the query. */}
        {notice && (
          <div className={notice.tone === 'neutral' ? 'muted' : 'warn-banner'} style={{ marginBottom: 12 }}>
            {notice.text}
          </div>
        )}

        <dl className="kv">
          <dt>Protected volumes</dt>
          <dd>{summary.protectedVolumeCount}</dd>

          <dt>Unprotected volumes</dt>
          <dd>{summary.unprotectedVolumeCount}</dd>

          <dt>Unknown volumes</dt>
          <dd>{summary.unknownVolumeCount}</dd>

          <dt>TPM</dt>
          <dd>
            {summary.tpmPresent === null
              ? 'Unknown'
              : summary.tpmPresent === false
                ? 'Not present'
                : summary.tpmEnabled === true
                  ? `Present and enabled${summary.tpmSpecVersion ? ` (${summary.tpmSpecVersion})` : ''}`
                  : summary.tpmEnabled === false
                    ? 'Present but disabled'
                    : 'Present, enabled state unknown'}
          </dd>

          {/* The long-standing security-posture field, shown so a reader comparing
              this against the compliance score sees the same value it was computed from. */}
          <dt>System drive (posture)</dt>
          <dd>{summary.systemDriveStatus ?? 'Unknown'}</dd>
        </dl>

        <p className="muted" style={{ marginTop: 10 }}>
          {summary.limitation}
        </p>
      </div>

      <div className="card">
        <div className="row-sub" style={{ justifyContent: 'space-between', marginBottom: 10 }}>
          <h2 style={{ margin: 0 }}>
            Volumes
            <span className="badge neutral plain" style={{ marginLeft: 8 }}>
              {sorted.length}
            </span>
          </h2>
          <button type="button" className="btn-ghost btn-sm" onClick={() => void load()}>
            <Icon name="refresh" size={14} />
            Reload
          </button>
        </div>

        {sorted.length === 0 ? (
          <p className="muted">
            {summary.availability === 'Available'
              ? 'The endpoint reported no encryptable volumes.'
              : 'No volume detail is available. This is not the same as the machine being unencrypted.'}
          </p>
        ) : (
          <div style={{ overflowX: 'auto' }}>
            <table className="table">
              <thead>
                <tr>
                  <th>Volume</th>
                  <th>Type</th>
                  <th>State</th>
                  <th>Encrypted</th>
                  <th>Method</th>
                  <th>Recovery protector</th>
                  <th>Escrowed key</th>
                </tr>
              </thead>
              <tbody>
                {sorted.map((v) => (
                  <tr key={v.deviceIdentifier}>
                    <td>
                      {v.driveLetter ?? 'No letter'}
                      <div className="muted" style={{ fontSize: '0.85em' }}>
                        {v.deviceIdentifier}
                      </div>
                    </td>
                    <td>{volumeTypeLabel(v.volumeType)}</td>
                    <td>
                      <span className={`badge ${volumeStateTone(v.state)}`}>
                        {volumeStateLabel(v.state)}
                      </span>
                      {v.state === 'Suspended' && (
                        <div className="muted" style={{ fontSize: '0.85em', marginTop: 4 }}>
                          Encrypted on disk, but the key is available without its protectors.
                        </div>
                      )}
                    </td>
                    <td>{encryptionProgress(v)}</td>
                    <td>{encryptionMethodLabel(v.encryptionMethod)}</td>
                    <td>
                      {recoveryProtectorSummary(v)}
                      {/* Identifiers only. A protector GUID names a protector; it
                          does not unlock anything, and the value that would is
                          never read from Windows in the first place. */}
                      {v.recoveryProtectorIds?.length > 0 && (
                        <div className="muted" style={{ fontSize: '0.8em', marginTop: 4 }}>
                          {v.recoveryProtectorIds.join(', ')}
                        </div>
                      )}
                    </td>
                    <td>
                      {/* Status only. The key itself is never rendered here and
                          is never fetched until somebody asks for it. */}
                      <span className={`badge ${escrowStatusTone(escrowStatus(escrows, v.deviceIdentifier))}`}>
                        {escrowStatusLabel(escrowStatus(escrows, v.deviceIdentifier))}
                      </span>

                      {activeEscrowFor(escrows, v.deviceIdentifier) && (
                        <div className="muted" style={{ fontSize: '0.8em', marginTop: 4 }}>
                          {describeEscrow(activeEscrowFor(escrows, v.deviceIdentifier)!)}
                        </div>
                      )}

                      <div className="row-sub" style={{ marginTop: 6, gap: 6 }}>
                        {canReadKeys && activeEscrowFor(escrows, v.deviceIdentifier) && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => setRevealing(activeEscrowFor(escrows, v.deviceIdentifier)!)}
                          >
                            Reveal
                          </button>
                        )}

                        {canManageKeys && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => setEscrowing(v)}
                          >
                            {activeEscrowFor(escrows, v.deviceIdentifier) ? 'Replace' : 'Escrow key'}
                          </button>
                        )}

                        {canManageKeys && activeEscrowFor(escrows, v.deviceIdentifier) && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={async () => {
                              await deleteRecoveryKeyEscrow(activeEscrowFor(escrows, v.deviceIdentifier)!.id)
                              await load()
                            }}
                          >
                            Delete
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}

        <p className="muted" style={{ marginTop: 12 }}>
          The agent never reads a recovery key from Windows. A key can only be here because an
          administrator escrowed it deliberately; it is encrypted at rest, never shown on this page,
          and revealing one requires your own password and is recorded against your account.
        </p>
      </div>

      {escrowing && (
        <EscrowKeyDialog
          deviceId={deviceId}
          volume={escrowing}
          onClose={() => setEscrowing(null)}
          onSaved={() => void load()}
        />
      )}

      {revealing && (
        <RevealKeyDialog escrow={revealing} onClose={() => setRevealing(null)} />
      )}
    </>
  )
}
