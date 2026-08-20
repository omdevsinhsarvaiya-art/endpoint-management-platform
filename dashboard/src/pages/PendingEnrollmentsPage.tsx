import { useCallback, useEffect, useState } from 'react'
import {
  approveEnrollment,
  getPendingEnrollments,
  rejectEnrollment,
  ApiError,
  type PendingEnrollment,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'

/** How often the list refreshes while the page is open. */
const POLL_INTERVAL_MS = 12_000

/**
 * Windows agents waiting for administrator approval.
 *
 * A PC that has installed the MSI carries no credential and no token — it asks
 * to be managed and waits here. Approving is what admits it, so this page is the
 * human half of the enrollment authorization boundary.
 *
 * Everything shown is identity and state. The proof secret, the sealed
 * enrollment token and the issued device credential never leave the server, so
 * there is nothing secret on this page to redact.
 */
export function PendingEnrollmentsPage() {
  const { hasPermission } = useAuth()
  const canDecide = hasPermission('device.enroll')

  const [requests, setRequests] = useState<PendingEnrollment[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  /** Request id currently being approved/rejected, so its buttons can disable. */
  const [busy, setBusy] = useState<string | null>(null)

  // Lets the render loop re-evaluate "expires in" without refetching.
  const [, setTick] = useState(0)

  const load = useCallback(async (showSpinner = false) => {
    if (showSpinner) setLoading(true)
    try {
      setRequests(await getPendingEnrollments())
      setError(null)
    } catch (e) {
      // Never clear the list on a failed refresh: a network blip must not make
      // machines look as though they withdrew their requests.
      setError(describe(e, 'Could not load pending enrollments.'))
    } finally {
      setLoading(false)
    }
  }, [])

  // Poll while mounted. The interval is cleared on unmount so a backgrounded
  // dashboard is not still calling the API.
  useEffect(() => {
    void load(true)
    const poll = setInterval(() => void load(), POLL_INTERVAL_MS)
    const clock = setInterval(() => setTick((t) => t + 1), 1_000)
    return () => {
      clearInterval(poll)
      clearInterval(clock)
    }
  }, [load])

  async function decide(request: PendingEnrollment, approve: boolean) {
    if (!approve) {
      const confirmed = window.confirm(
        `Reject the enrollment request from "${request.hostname}"?\n\n` +
          'No credential will be issued and the agent will stop asking. ' +
          'The machine can request enrollment again later.',
      )
      if (!confirmed) return
    }

    setBusy(request.requestId)
    setError(null)
    setNotice(null)

    try {
      const result = approve
        ? await approveEnrollment(request.requestId)
        : await rejectEnrollment(request.requestId)

      setNotice(
        approve
          ? `${result.hostname} approved. ${result.message}`
          : `${result.hostname} rejected. ${result.message}`,
      )
    } catch (e) {
      setError(describe(e, 'The decision could not be recorded.'))
    } finally {
      setBusy(null)
      // Refresh either way: on success to pick up the new state, and on failure
      // because the server's view is authoritative and ours may be stale.
      await load()
    }
  }

  return (
    <>
      {error && <div className="error-banner">{error}</div>}
      {notice && (
        <div className="card card-section" style={{ borderLeft: '3px solid var(--color-primary)' }}>
          {notice}
        </div>
      )}

      <div style={{ display: 'flex', alignItems: 'flex-start', justifyContent: 'space-between', marginBottom: 16, gap: 16 }}>
        <div style={{ color: 'var(--color-text-muted)', fontSize: 13.5, maxWidth: 640 }}>
          Windows agents waiting for administrator approval. A machine appears here after the
          agent is installed and reaches this server; it receives no credential and cannot be
          managed until it is approved.
        </div>
        <button type="button" onClick={() => void load(true)} disabled={loading}>
          {loading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      {!canDecide && (
        <div className="card card-section" style={{ color: 'var(--color-text-muted)' }}>
          You can see enrolment requests but not decide them — that needs the
          <code style={{ margin: '0 4px' }}>device.enroll</code> permission.
        </div>
      )}

      <div className="card">
        {loading && requests.length === 0 && <div className="loading">Loading…</div>}

        {!loading && requests.length === 0 && (
          <div className="empty-state">
            <div className="title">No pending enrollment requests.</div>
            <div style={{ color: 'var(--color-text-muted)', fontSize: 13, marginTop: 6 }}>
              Install the Windows agent on a PC and it will appear here within a minute.
            </div>
          </div>
        )}

        {requests.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Computer</th>
                <th>Operating system</th>
                <th>Agent</th>
                <th>Requested</th>
                <th>Expires</th>
                <th>Status</th>
                <th style={{ textAlign: 'right' }}>Decision</th>
              </tr>
            </thead>
            <tbody>
              {requests.map((r) => {
                const expired = new Date(r.expiresAt).getTime() <= Date.now()
                const decided = r.status !== 'Pending'
                const disabled = busy !== null || decided || expired || !canDecide

                return (
                  <tr key={r.requestId}>
                    <td>
                      <div style={{ fontWeight: 600 }}>{r.hostname}</div>
                      {/* Not a secret: the SMBIOS UUID is how a re-enrolling machine
                          resolves to its existing device record instead of a duplicate. */}
                      <div style={{ color: 'var(--color-text-muted)', fontSize: 11.5, fontFamily: 'monospace' }}>
                        {r.machineIdentifier}
                      </div>
                    </td>
                    <td>{r.operatingSystem ?? '—'}</td>
                    <td><code>{r.agentVersion}</code></td>
                    <td title={r.requestedAt}>{formatTime(r.requestedAt)}</td>
                    <td style={{ color: expired ? 'var(--color-crit)' : undefined }}>
                      {expired ? 'Expired' : relativeTo(r.expiresAt)}
                    </td>
                    <td>
                      <span className={`badge ${statusClass(r.status, expired)}`}>
                        {expired && r.status === 'Pending' ? 'Expired' : r.status}
                      </span>
                      {r.approvedBy && (
                        <div style={{ color: 'var(--color-text-muted)', fontSize: 11.5, marginTop: 2 }}>
                          by {r.approvedBy}
                        </div>
                      )}
                    </td>
                    <td style={{ textAlign: 'right', whiteSpace: 'nowrap' }}>
                      <button
                        type="button"
                        disabled={disabled}
                        onClick={() => void decide(r, true)}
                        style={{
                          background: disabled ? undefined : 'var(--color-primary)',
                          color: disabled ? undefined : '#fff',
                          border: disabled ? undefined : 'none',
                          borderRadius: 6,
                          padding: '6px 14px',
                          fontWeight: 600,
                          cursor: disabled ? 'not-allowed' : 'pointer',
                          marginRight: 8,
                        }}
                      >
                        {busy === r.requestId ? 'Working…' : 'Approve'}
                      </button>
                      <button type="button" disabled={disabled} onClick={() => void decide(r, false)}>
                        Reject
                      </button>
                    </td>
                  </tr>
                )
              })}
            </tbody>
          </table>
        )}
      </div>

      <div style={{ color: 'var(--color-text-muted)', fontSize: 12.5, marginTop: 12 }}>
        After approval the agent collects its credential on its next poll, then appears under
        Devices and becomes Active once it starts reporting. Requests expire 15 minutes after
        they are made; an agent that is still waiting simply asks again.
      </div>
    </>
  )
}

/**
 * Turns a failure into something an administrator can act on.
 *
 * 409 is the interesting one: it means the request was already decided, has
 * expired, or another administrator got there first. That is a stale view rather
 * than a fault, so it reads as information instead of an error.
 */
function describe(error: unknown, fallback: string): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 401:
        return 'Your session has expired. Sign in again.'
      case 403:
        return 'You do not have permission to decide enrolment requests (device.enroll).'
      case 409:
        return 'That request was already decided, or it expired. The list has been refreshed.'
      case 503:
        return 'The server is temporarily unavailable. It will be retried on the next refresh.'
      default:
        return `${fallback} (HTTP ${error.status}${error.correlationId ? `, ref ${error.correlationId}` : ''})`
    }
  }
  return fallback
}

function statusClass(status: string, expired: boolean): string {
  if (expired || status === 'Rejected') return 'crit'
  if (status === 'Approved') return 'ok'
  return 'warn'
}

function formatTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? '—' : d.toLocaleTimeString()
}

/** "in 12m 30s", or "Expired" once it has passed. */
function relativeTo(iso: string): string {
  const ms = new Date(iso).getTime() - Date.now()
  if (Number.isNaN(ms) || ms <= 0) return 'Expired'
  const total = Math.floor(ms / 1000)
  const minutes = Math.floor(total / 60)
  const seconds = total % 60
  return minutes > 0 ? `in ${minutes}m ${seconds}s` : `in ${seconds}s`
}
