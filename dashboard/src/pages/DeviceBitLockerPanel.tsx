import { useCallback, useEffect, useState } from 'react'
import {
  deleteRecoveryKeyEscrow,
  getBitLockerEscrowAttempts,
  getBitLockerEscrows,
  getDeviceBitLockerReadiness,
  getDeviceBitLockerVolumes,
  resetEscrowAttempts,
  type BitLockerReadinessSummary,
  type BitLockerVolumeRow,
  type EscrowAttemptRow,
  type EscrowRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { EscrowKeyDialog, RevealKeyDialog } from './RecoveryKeyDialog'
import {
  activeAutomaticEscrowFor,
  activeEscrowFor,
  attemptFor,
  autoEscrowLabel,
  autoEscrowState,
  autoEscrowTone,
  canReset,
  describeAttempt,
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
 * BitLocker readiness, encryption state, and recovery-key escrow.
 *
 * Read-only with respect to encryption: this panel has no control that encrypts,
 * decrypts, suspends or resumes anything.
 *
 * **No recovery key is ever displayed automatically.** The page shows that a
 * protector exists, the GUID naming it, and whether a key has been filed. Seeing
 * the key itself is a separate, deliberate act behind its own permission, a
 * step-up password and a rate limiter.
 *
 * The page is laid out as distinct sections rather than one continuous block,
 * because it covers three different questions -- is this machine encrypted, what
 * are its volumes, and are its keys recoverable -- and they were previously
 * running together. Recovery keys in particular used to be a column inside the
 * volumes table, which put two different escrow mechanisms, their status and
 * three controls inside a single cell.
 */
export function DeviceBitLockerPanel({ deviceId }: { deviceId: string }) {
  const { hasPermission } = useAuth()
  const canView = hasPermission('bitlocker.view')
  const canManageKeys = hasPermission('bitlocker.recovery_key.manage')
  const canReadKeys = hasPermission('bitlocker.recovery_key.read')

  const [summary, setSummary] = useState<BitLockerReadinessSummary | null>(null)
  const [volumes, setVolumes] = useState<BitLockerVolumeRow[]>([])
  const [escrows, setEscrows] = useState<EscrowRow[]>([])
  const [attempts, setAttempts] = useState<EscrowAttemptRow[]>([])
  const [eligible, setEligible] = useState(false)
  const [escrowing, setEscrowing] = useState<BitLockerVolumeRow | null>(null)
  const [revealing, setRevealing] = useState<EscrowRow | null>(null)
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      // METADATA only, on every one of these. Nothing here fetches a key:
      // retrieval is a separate route behind its own permission and a step-up
      // password, and it never happens on page load.
      const [readiness, rows, escrowRows, attemptRows] = await Promise.all([
        getDeviceBitLockerReadiness(deviceId),
        getDeviceBitLockerVolumes(deviceId),
        getBitLockerEscrows(deviceId),
        getBitLockerEscrowAttempts(deviceId),
      ])
      setSummary(readiness)
      setVolumes(rows)
      setEscrows(escrowRows)
      setAttempts(attemptRows.attempts)

      // From the device's credential, not from whether attempts exist. A pinned
      // device the agent has not reached yet has no attempts and is still
      // perfectly eligible; telling an operator to re-enroll it would send them
      // to fix a machine that needs nothing.
      setEligible(attemptRows.eligible)
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

  const protectors = sorted.flatMap((volume) =>
    (volume.recoveryProtectorIds ?? []).map((protectorId) => ({ volume, protectorId })),
  )

  return (
    <div className="panel-stack">
      <div className="card">
        <div className="section">
          <div className="section-head">
            <h3>Readiness</h3>
            <span className={`badge ${readinessTone(summary.readiness)}`}>
              {readinessLabel(summary.readiness)}
            </span>
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

          {summary.lastReportedAt && (
            <p className="section-note" style={{ marginTop: 10, marginBottom: 0 }}>
              Last reported {new Date(summary.lastReportedAt).toLocaleString()}. {summary.limitation}
            </p>
          )}
        </div>
      </div>

      <div className="card">
        <div className="section">
          <div className="section-head">
            <h3>
              Volumes
              <span className="badge neutral plain" style={{ marginLeft: 8 }}>
                {sorted.length}
              </span>
            </h3>
            <button type="button" className="btn-ghost btn-sm" onClick={() => void load()}>
              <Icon name="refresh" size={14} />
              Reload
            </button>
          </div>

          <p className="section-note">
            Encryption state as the endpoint last reported it. Recovery keys are handled
            separately, below.
          </p>

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
                      <td>{recoveryProtectorSummary(v)}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      </div>

      <div className="card">
        <div className="section">
          <div className="section-head">
            <h3>Recovery keys</h3>
          </div>

          <p className="section-note">
            A recovery key unlocks the disk it belongs to. Keys are encrypted before they are
            stored, are never shown on this page, and revealing one requires your own password
            and is recorded against your account.
            {!eligible && (
              <>
                {' '}
                <strong>This device is not collecting keys automatically</strong> — its
                credential carries no pinned sealing key, so it must re-enroll. Manual entry
                still works.
              </>
            )}
          </p>

          {protectors.length === 0 ? (
            <p className="muted">
              No recovery protectors have been reported, so there is nothing to escrow.
            </p>
          ) : (
            protectors.map(({ volume, protectorId }) => {
              // Three independent things, deliberately not conflated: how far
              // collection has got, the attempt row behind it, and the two
              // escrow records -- which are separate rows with separate origins.
              const collected = activeAutomaticEscrowFor(escrows, volume.deviceIdentifier, protectorId)
              const attempt = attemptFor(attempts, volume.deviceIdentifier, protectorId)

              // A collected key is escrowed whatever the attempt row says; the
              // row can lag, and the key is the fact that matters.
              const auto = collected
                ? 'Escrowed'
                : autoEscrowState(attempts, volume.deviceIdentifier, protectorId, eligible)

              // Manual is manual only. This is the fix: the manual card used to
              // be built from every escrow for the volume, so an automatically
              // collected key appeared under it as "Recovery key escrowed".
              const manual = activeEscrowFor(escrows, volume.deviceIdentifier)

              return (
                <div className="protector-card" key={`${volume.deviceIdentifier}:${protectorId}`}>
                  <div>
                    <strong>{volume.driveLetter ?? volume.deviceIdentifier}</strong>
                    {/* Identifiers only. A protector GUID names a protector; it
                        unlocks nothing. */}
                    <div className="protector-id">{protectorId}</div>
                  </div>

                  {/* Automatic and manual are different mechanisms carrying different
                      trust -- one collected by the endpoint, one vouched for by a
                      named administrator -- so they are shown as two paths rather
                      than one status a reader has to disambiguate. */}
                  <div className="escrow-paths">
                    <div className="escrow-path">
                      <h4>Automatic</h4>

                      <span className={`badge ${autoEscrowTone(auto)}`}>
                        {autoEscrowLabel(auto)}
                      </span>

                      <p className="path-detail">
                        {collected
                          ? `Collected by ${collected.escrowedBy} on ${new Date(
                              collected.escrowedAt,
                            ).toLocaleString()}`
                          : attempt
                            ? describeAttempt(attempt)
                            : eligible
                              ? 'The endpoint has not reported an attempt for this protector yet.'
                              : 'Unavailable until this device re-enrolls.'}
                      </p>

                      <div className="path-actions">
                        {/* Reveal is offered here too: an automatically collected
                            key is the one that unlocks the disk, and it goes
                            through exactly the same protected flow. Replace and
                            Delete are deliberately absent -- no administrator
                            owns this record, and it is replaced by the endpoint
                            collecting again, not by hand. */}
                        {canReadKeys && collected && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => setRevealing(collected)}
                          >
                            Reveal
                          </button>
                        )}

                        {canManageKeys && attempt && canReset(auto) && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={async () => {
                              await resetEscrowAttempts(attempt.id)
                              await load()
                            }}
                          >
                            Reset and retry
                          </button>
                        )}
                      </div>
                    </div>

                    <div className="escrow-path">
                      <h4>Manual</h4>

                      <span
                        className={`badge ${escrowStatusTone(escrowStatus(escrows, volume.deviceIdentifier))}`}
                      >
                        {escrowStatusLabel(escrowStatus(escrows, volume.deviceIdentifier))}
                      </span>

                      <p className="path-detail">
                        {manual
                          ? describeEscrow(manual)
                          : 'No key has been entered by an administrator.'}
                      </p>

                      <div className="path-actions">
                        {canReadKeys && manual && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => setRevealing(manual)}
                          >
                            Reveal
                          </button>
                        )}

                        {canManageKeys && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => setEscrowing(volume)}
                          >
                            {manual ? 'Replace' : 'Enter key'}
                          </button>
                        )}

                        {canManageKeys && manual && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={async () => {
                              await deleteRecoveryKeyEscrow(manual.id)
                              await load()
                            }}
                          >
                            Delete
                          </button>
                        )}
                      </div>
                    </div>
                  </div>
                </div>
              )
            })
          )}
        </div>
      </div>

      {escrowing && (
        <EscrowKeyDialog
          deviceId={deviceId}
          volume={escrowing}
          onClose={() => setEscrowing(null)}
          onSaved={() => void load()}
        />
      )}

      {revealing && <RevealKeyDialog escrow={revealing} onClose={() => setRevealing(null)} />}
    </div>
  )
}
