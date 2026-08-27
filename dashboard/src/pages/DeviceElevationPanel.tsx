import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  approveElevation,
  getElevations,
  requestElevation,
  revokeElevation,
  type ElevationRow,
  type LocalUserRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { useDialogDismiss } from '../components/useDialogDismiss'
import {
  describeEnforcement,
  ineligibilityReason,
  isEligibleTarget,
  isLive,
  remaining,
  type ElevationEnforcement,
} from './elevationView'

/** Durations offered, inside the 15-minute to 8-hour window the domain enforces. */
const DURATIONS = [
  { minutes: 15, label: '15 minutes' },
  { minutes: 30, label: '30 minutes' },
  { minutes: 60, label: '1 hour' },
  { minutes: 120, label: '2 hours' },
  { minutes: 240, label: '4 hours' },
  { minutes: 480, label: '8 hours' },
]

/**
 * How each enforcement state is presented.
 *
 * Only `Applied` is reassuring. `Drifted` is the loud one: the authorization has
 * ended and the account is still an administrator, which is the case an operator
 * most needs to act on and the one a naive console would hide behind the word
 * "Expired".
 */
const ENFORCEMENT: Record<ElevationEnforcement, { badge: string; label: string; hint: string }> = {
  Applied: {
    badge: 'ok',
    label: 'Applied',
    hint: 'The endpoint reports this account as an administrator.',
  },
  Pending: {
    badge: 'warn',
    label: 'Not applied yet',
    hint: 'Authorized, but the endpoint has not reported the change. It may be offline, or the task may still be on its way.',
  },
  Drifted: {
    badge: 'crit',
    label: 'Still administrator',
    hint: 'The authorization has ended but the endpoint still reports this account as an administrator. The rights were NOT removed.',
  },
  NotApplicable: {
    badge: 'neutral',
    label: '—',
    hint: 'No live authorization and no elevated rights from it.',
  },
}

const STATE_BADGE: Record<string, string> = {
  Requested: 'warn',
  Approved: 'ok',
  Active: 'ok',
  Rejected: 'neutral',
  Expired: 'neutral',
  Revoked: 'neutral',
  Failed: 'crit',
}

function when(iso: string | null): string {
  return iso ? new Date(iso).toLocaleString() : '—'
}

/**
 * Temporary local administrator elevation for one endpoint.
 *
 * Two things are kept visibly separate throughout, and that is the whole point
 * of the layout. **State** is what the platform authorized. **Enforcement** is
 * what the endpoint reports it is actually doing. Collapsing them would hide the
 * case that matters: an expired elevation whose de-elevation failed, where the
 * account is still an administrator.
 */
export function DeviceElevationPanel({
  deviceId,
  accounts,
  onChanged,
}: {
  deviceId: string
  /** The device's reported local accounts, used for eligibility and drift. */
  accounts: LocalUserRow[]
  /** Called after a mutation so the parent can refresh reported account state. */
  onChanged: () => void
}) {
  const { hasPermission } = useAuth()
  const canElevate = hasPermission('localuser.elevate')

  const [elevations, setElevations] = useState<ElevationRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [requesting, setRequesting] = useState(false)
  const [revoking, setRevoking] = useState<ElevationRow | null>(null)

  // Re-rendered on a timer so "12m left" does not sit frozen while somebody
  // watches an elevation run down.
  const [now, setNow] = useState(() => new Date())

  const load = useCallback(async () => {
    try {
      setElevations(await getElevations(deviceId))
      setError(null)
    } catch {
      setError('Could not load elevations for this device.')
    }
  }, [deviceId])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    const timer = setInterval(() => setNow(new Date()), 30_000)
    return () => clearInterval(timer)
  }, [])

  const byAccount = new Map(accounts.map((a) => [a.sid.toLowerCase(), a]))
  const eligible = accounts.filter(isEligibleTarget)

  async function run(action: () => Promise<unknown>, success: string) {
    setBusy(true)
    setError(null)
    try {
      await action()
      setNotice(success)
      await load()
      // The account's reported administrator state is what proves the change
      // took effect, and it comes from the endpoint's next inventory rather than
      // from this response.
      onChanged()
    } catch {
      setError('The server refused that request. Nothing was changed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="card">
      <div className="page-header">
        <div>
          <h2>Temporary administrator access</h2>
          <p className="lede">
            Grants a standard account administrator rights for a bounded period. Rights are
            withdrawn automatically when the period ends, even if this endpoint is offline.
          </p>
        </div>
        {canElevate && eligible.length > 0 && (
          <button type="button" className="btn-primary btn-sm" onClick={() => setRequesting(true)} disabled={busy}>
            <Icon name="shield-check" size={14} />
            Request elevation
          </button>
        )}
      </div>

      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      {notice && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span>{notice}</span>
        </div>
      )}

      {!canElevate && (
        <div className="row-sub" style={{ marginBottom: 10 }}>
          View only. Granting or ending administrator access requires the elevation permission.
        </div>
      )}

      {elevations.length === 0 ? (
        <p className="muted">No elevation has been requested for this endpoint.</p>
      ) : (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Account</th>
                <th>Authorization</th>
                <th>Endpoint</th>
                <th>Expires</th>
                <th>Requested by</th>
                <th style={{ textAlign: 'right' }}>Action</th>
              </tr>
            </thead>
            <tbody>
              {elevations.map((e) => {
                const account = byAccount.get(e.targetSid.toLowerCase())
                const enforcement = ENFORCEMENT[describeEnforcement(e, account, now)]
                const live = isLive(e, now)

                return (
                  <tr key={e.id}>
                    <td>
                      <div>
                        <span>{e.targetUsername}</span>
                        <code className="row-sub mono-sub" title={e.targetSid}>
                          {e.targetSid}
                        </code>
                      </div>
                    </td>
                    <td>
                      <div>
                        <span className={`badge ${STATE_BADGE[e.state] ?? 'neutral'}`}>{e.state}</span>
                        {e.failureReason && <div className="row-sub">{e.failureReason}</div>}
                        {e.decisionNote && <div className="row-sub">{e.decisionNote}</div>}
                      </div>
                    </td>
                    <td>
                      {/* Deliberately a separate column from Authorization. The
                          console must be able to say "the authorization ended
                          but the account is still an administrator". */}
                      <span className={`badge ${enforcement.badge}`} title={enforcement.hint}>
                        {enforcement.label}
                      </span>
                    </td>
                    <td>
                      <div>
                        <span title={e.expiresAt ?? undefined}>{when(e.expiresAt)}</span>
                        {live && <div className="row-sub">{remaining(e.expiresAt, now)}</div>}
                      </div>
                    </td>
                    <td>
                      <div>
                        <span>{e.requestedBy}</span>
                        <div className="row-sub" title={e.requestedAt}>
                          {e.justification}
                        </div>
                      </div>
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      {canElevate && e.state === 'Requested' && (
                        <button
                          type="button"
                          className="btn-sm"
                          disabled={busy}
                          onClick={() =>
                            void run(() => approveElevation(e.id, 60), `Approved for ${e.targetUsername}.`)
                          }
                        >
                          Approve 1h
                        </button>
                      )}
                      {canElevate && live && (
                        <button
                          type="button"
                          className="btn-danger btn-sm"
                          disabled={busy}
                          onClick={() => setRevoking(e)}
                        >
                          Revoke
                        </button>
                      )}
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        </div>
      )}

      {requesting && (
        <RequestElevationDialog
          candidates={eligible}
          excluded={accounts.filter((a) => !isEligibleTarget(a))}
          onCancel={() => setRequesting(false)}
          onConfirm={(sid, justification, minutes) => {
            setRequesting(false)
            void run(
              () => requestElevation(deviceId, sid, justification, minutes),
              'Elevation requested.',
            )
          }}
        />
      )}

      {revoking && (
        <ConfirmDialog
          title="Revoke administrator access"
          confirmLabel="Revoke"
          onCancel={() => setRevoking(null)}
          onConfirm={() => {
            const target = revoking
            setRevoking(null)
            void run(() => revokeElevation(target.id, null), `Revoked for ${target.targetUsername}.`)
          }}
        >
          <p>
            <strong>{revoking.targetUsername}</strong> will be returned to a standard user on this
            endpoint.
          </p>
          <p className="row-sub">
            The authorization ends immediately. The endpoint applies the change on its next
            check-in; if it is offline, the rights are withdrawn when it reconnects.
          </p>
        </ConfirmDialog>
      )}
    </div>
  )
}

/**
 * The request dialog.
 *
 * Only eligible accounts are offered, and the ineligible ones are shown with the
 * reason rather than omitted — an operator looking for a specific user needs to
 * know why it is not in the list, or they will assume the console is broken.
 */
function RequestElevationDialog({
  candidates,
  excluded,
  onCancel,
  onConfirm,
}: {
  candidates: LocalUserRow[]
  excluded: LocalUserRow[]
  onCancel: () => void
  onConfirm: (sid: string, justification: string, minutes: number) => void
}) {
  const [sid, setSid] = useState(candidates[0]?.sid ?? '')
  const [justification, setJustification] = useState('')
  const [minutes, setMinutes] = useState(60)

  useDialogDismiss(onCancel)

  const trimmed = justification.trim()
  const valid = sid.length > 0 && trimmed.length >= 3

  function submit(event: FormEvent) {
    event.preventDefault()
    if (valid) onConfirm(sid, trimmed, minutes)
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="elevate-title">
      <form className="dialog" style={{ maxWidth: 520 }} onSubmit={submit}>
        <div className="dialog-header">
          <h2 id="elevate-title">Request administrator access</h2>
          <div className="sub">Temporary, time-boxed, and limited to one account.</div>
        </div>

        <div className="dialog-body">
          <div className="field">
            <label className="field-label" htmlFor="elev-account">
              Account
            </label>
            <select id="elev-account" value={sid} onChange={(e) => setSid(e.target.value)}>
              {candidates.map((a) => (
                <option key={a.sid} value={a.sid}>
                  {a.name}
                </option>
              ))}
            </select>
            <div className="field-hint">
              Only standard, enabled accounts can be elevated. The built-in Administrator is never
              eligible.
            </div>
          </div>

          <div className="field">
            <label className="field-label" htmlFor="elev-duration">
              Access expires after
            </label>
            <select
              id="elev-duration"
              value={minutes}
              onChange={(e) => setMinutes(Number(e.target.value))}
            >
              {DURATIONS.map((d) => (
                <option key={d.minutes} value={d.minutes}>
                  {d.label}
                </option>
              ))}
            </select>
            <div className="field-hint">
              Rights end automatically when the period elapses, even if the endpoint is offline.
              The window cannot be extended — issue a new elevation instead.
            </div>
          </div>

          <div className="field">
            <label className="field-label" htmlFor="elev-why">
              Justification
            </label>
            <textarea
              id="elev-why"
              rows={3}
              maxLength={1000}
              value={justification}
              onChange={(e) => setJustification(e.target.value)}
              placeholder="Why does this user need administrator rights?"
            />
            <div className="field-hint">
              Required. Recorded in the audit log with your name and this endpoint.
            </div>
          </div>

          {excluded.length > 0 && (
            <div className="row-sub" style={{ marginTop: 8 }}>
              Not eligible:{' '}
              {excluded.map((a, i) => (
                <span key={a.sid}>
                  {i > 0 && '; '}
                  <strong>{a.name}</strong> — {ineligibilityReason(a)}
                </span>
              ))}
            </div>
          )}
        </div>

        <div className="dialog-footer">
          <button type="button" onClick={onCancel}>
            Cancel
          </button>
          <button type="submit" className="btn-primary" disabled={!valid}>
            Request and approve
          </button>
        </div>
      </form>
    </div>
  )
}
