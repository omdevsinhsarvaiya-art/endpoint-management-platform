import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import {
  cancelDeployment,
  getDeployment,
  getDeployments,
  retryDeployment,
  type DeploymentDetail,
  type DeploymentSummaryPage,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import {
  hasCancellableWork,
  hasRetryableWork,
  isSettled,
  reasonLabel,
  statusTone,
  tallySummary,
} from './deploymentView'

const PAGE_SIZE = 25

/**
 * Software deployments and their per-device outcomes.
 *
 * Every number here is derived from persisted task state on the server. Nothing
 * is inferred in the browser and no progress is animated: a deployment reads as
 * pending until a task actually says otherwise, because a console that shows
 * optimistic progress is a console that lies about the fleet.
 */
export function DeploymentsPanel({ focusDeploymentId }: { focusDeploymentId?: string | null }) {
  const [data, setData] = useState<DeploymentSummaryPage | null>(null)
  const [page, setPage] = useState(1)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const { hasPermission } = useAuth()
  const canDeploy = hasPermission('software.deploy')

  const [selectedId, setSelectedId] = useState<string | null>(null)
  const [detail, setDetail] = useState<DeploymentDetail | null>(null)
  const [detailError, setDetailError] = useState<string | null>(null)
  const [acting, setActing] = useState(false)
  const [notice, setNotice] = useState<string | null>(null)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await getDeployments(page, PAGE_SIZE))
      setError(null)
    } catch {
      setError('Could not load deployments.')
    } finally {
      setLoading(false)
    }
  }, [page])

  useEffect(() => {
    void load()
  }, [load])

  // A deployment just created elsewhere on the page opens straight away, so the
  // operator sees what it decided instead of hunting for it in the list.
  useEffect(() => {
    if (focusDeploymentId) {
      setSelectedId(focusDeploymentId)
      void load()
    }
  }, [focusDeploymentId, load])

  useEffect(() => {
    if (selectedId === null) {
      setDetail(null)
      setDetailError(null)
      return
    }

    let cancelled = false
    getDeployment(selectedId)
      .then((d) => {
        if (!cancelled) setDetail(d)
      })
      .catch(() => {
        if (!cancelled) setDetailError('Could not load this deployment.')
      })

    return () => {
      cancelled = true
    }
  }, [selectedId])

  /** Re-reads the deployment after an action so the numbers come from the server. */
  const refreshDetail = useCallback(async (id: string) => {
    setDetail(await getDeployment(id))
    await load()
  }, [load])

  async function onRetry(id: string) {
    setActing(true)
    setNotice(null)
    try {
      const result = await retryDeployment(id)
      setNotice(
        result.queued === 0
          ? 'Nothing needed retrying — every failed device is now compliant, retired, or out of scope.'
          : `Retrying ${result.queued} device${result.queued === 1 ? '' : 's'}.`,
      )
      await refreshDetail(id)
    } catch {
      setDetailError('The retry could not be started.')
    } finally {
      setActing(false)
    }
  }

  async function onCancel(id: string) {
    setActing(true)
    setNotice(null)
    try {
      const result = await cancelDeployment(id)
      // Considered minus cancelled is work an agent claimed mid-request. Saying
      // so is more useful than reporting a number that looks like a failure.
      setNotice(
        result.cancelled === result.considered
          ? `Cancelled ${result.cancelled} pending install${result.cancelled === 1 ? '' : 's'}.`
          : `Cancelled ${result.cancelled} of ${result.considered}; the rest had already started and were left running.`,
      )
      await refreshDetail(id)
    } catch {
      setDetailError('The deployment could not be cancelled.')
    } finally {
      setActing(false)
    }
  }

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1

  return (
    <>
      <div className="card">
        <h2>Deployments</h2>

        {error && (
          <div className="error-banner" role="alert">
            <Icon name="alert" size={15} />
            <span>{error}</span>
          </div>
        )}

        {loading && !data && <div className="loading">Loading deployments…</div>}

        {data && data.items.length === 0 && !error && (
          <div className="empty-state">
            <Icon name="software" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No deployments yet</div>
            <div>Deploy a managed package to devices or groups to see its results here.</div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Package</th>
                    <th>Target</th>
                    <th>Devices</th>
                    <th>Progress</th>
                    <th>Created by</th>
                    <th>When</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((d) => (
                    <tr key={d.id} className={d.id === selectedId ? 'row-selected' : undefined}>
                      <td>
                        <button
                          type="button"
                          className="link-button"
                          aria-expanded={d.id === selectedId}
                          onClick={() => setSelectedId(d.id === selectedId ? null : d.id)}
                        >
                          {d.packageName} {d.packageVersion}
                        </button>
                      </td>
                      <td>{d.targetType}</td>
                      <td>{d.tally.total}</td>
                      <td>
                        <span className={`badge ${isSettled(d.tally) ? 'neutral' : 'info'} plain`}>
                          {tallySummary(d.tally)}
                        </span>
                      </td>
                      <td>{d.createdByDisplay}</td>
                      <td>{new Date(d.createdAt).toLocaleString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="pagination">
              <span>
                {data.totalCount} deployment{data.totalCount === 1 ? '' : 's'}
              </span>
              <span className="pager">
                <button
                  type="button"
                  className="btn-sm"
                  disabled={page <= 1}
                  onClick={() => setPage(page - 1)}
                >
                  Previous
                </button>
                <span>
                  Page {page} of {totalPages}
                </span>
                <button
                  type="button"
                  className="btn-sm"
                  disabled={page >= totalPages}
                  onClick={() => setPage(page + 1)}
                >
                  Next
                </button>
              </span>
            </div>
          </>
        )}
      </div>

      {selectedId && (
        <div className="card">
          <div className="card-header">
            <h2>{detail ? `${detail.packageName} ${detail.packageVersion}` : 'Deployment'}</h2>
            <div className="btn-row">
              {/* Offered only when there is work of that kind. A Retry button on
                  a deployment with nothing failed would invite a no-op, and a
                  Cancel button on finished work would imply it could be undone. */}
              {canDeploy && detail && hasRetryableWork(detail.tally) && (
                <button
                  type="button"
                  className="btn-sm"
                  disabled={acting}
                  onClick={() => void onRetry(detail.id)}
                >
                  Retry failed
                </button>
              )}
              {canDeploy && detail && hasCancellableWork(detail.tally) && (
                <button
                  type="button"
                  className="btn-sm"
                  disabled={acting}
                  onClick={() => void onCancel(detail.id)}
                >
                  Cancel pending
                </button>
              )}
              <button type="button" className="btn-sm" onClick={() => setSelectedId(null)}>
                Close
              </button>
            </div>
          </div>

          {notice && (
            <div className="notice" role="status">
              <Icon name="check" size={15} />
              <span>{notice}</span>
            </div>
          )}

          {detailError && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{detailError}</span>
            </div>
          )}

          {!detail && !detailError && <div className="loading">Loading results…</div>}

          {detail && (
            <>
              <dl className="detail-grid">
                <div>
                  <dt>Created by</dt>
                  <dd>{detail.createdByDisplay}</dd>
                </div>
                <div>
                  <dt>Created</dt>
                  <dd>{new Date(detail.createdAt).toLocaleString()}</dd>
                </div>
                <div>
                  <dt>Targeted</dt>
                  <dd>{detail.tally.total}</dd>
                </div>
                <div>
                  <dt>Progress</dt>
                  <dd>{tallySummary(detail.tally)}</dd>
                </div>
              </dl>

              <div className="table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Device</th>
                      <th>Status</th>
                      <th>Attempt</th>
                      <th>Version when targeted</th>
                      <th>Detail</th>
                    </tr>
                  </thead>
                  <tbody>
                    {/* Keyed by device AND attempt: a retried device appears once
                        per attempt, and that history is the point. */}
                    {detail.targets.map((t) => (
                      <tr key={`${t.deviceId}|${t.attempt}`}>
                        <td>
                          <Link to={`/devices/${t.deviceId}`}>{t.displayName ?? t.hostname}</Link>
                        </td>
                        <td>
                          <span className={`badge ${statusTone(t.status)}`}>{t.status}</span>
                        </td>
                        <td>{t.attempt}</td>
                        <td>{t.observedVersion ?? 'Not installed'}</td>
                        {/* For a skipped device the reason is the useful fact; for
                            one that ran, the agent's own result message is. */}
                        <td className="muted">
                          {t.status === 'Skipped' ? reasonLabel(t.reason) : t.resultMessage ?? '—'}
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </>
          )}
        </div>
      )}
    </>
  )
}
