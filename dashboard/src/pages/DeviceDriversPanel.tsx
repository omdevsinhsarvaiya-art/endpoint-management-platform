import { useCallback, useEffect, useState } from 'react'
import {
  getDeviceDriverHealth,
  getDeviceDrivers,
  type DriverHealthSummary,
  type DriverRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import {
  compareDrivers,
  driverHealthLabel,
  driverHealthTone,
  faultKindLabel,
  formatDriverDate,
  signedLabel,
} from './driverView'

/**
 * Device drivers and their health.
 *
 * Read-only. There is no control here that installs or changes a driver:
 * driver package deployment exists on the server but is deliberately not
 * surfaced in this console yet.
 *
 * The panel separates two questions that look similar and are not. **Health**
 * is the endpoint's verdict on each device — and it distinguishes a fault from
 * a device this platform deliberately disabled, because USB storage restriction
 * disables devices and a restricted stick must not read as damage. **Inventory**
 * is the full list, which is mostly uninteresting and is therefore collapsed to
 * the faults by default.
 */
export function DeviceDriversPanel({ deviceId }: { deviceId: string }) {
  const { hasPermission } = useAuth()
  const canView = hasPermission('driver.view')

  const [health, setHealth] = useState<DriverHealthSummary | null>(null)
  const [drivers, setDrivers] = useState<DriverRow[]>([])
  const [loading, setLoading] = useState(true)
  const [error, setError] = useState<string | null>(null)
  const [showAll, setShowAll] = useState(false)

  const load = useCallback(async () => {
    setLoading(true)
    setError(null)
    try {
      const [summary, rows] = await Promise.all([
        getDeviceDriverHealth(deviceId),
        getDeviceDrivers(deviceId),
      ])
      setHealth(summary)
      setDrivers(rows)
    } catch {
      setError('Driver information could not be loaded.')
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
        <p className="muted">You do not have permission to view driver information.</p>
      </div>
    )
  }

  if (loading) return <p className="muted">Loading drivers…</p>
  if (error) return <div className="warn-banner">{error}</div>
  if (!health) return null

  const faults = drivers.filter((d) => d.health === 'Problem')
  const visible = (showAll ? [...drivers] : faults).sort(compareDrivers)

  return (
    <>
      <div className="card">
        <h2>Driver health</h2>

        {health.lastReportedAt === null ? (
          // Not "healthy". An endpoint that has reported nothing is unknown, and
          // saying otherwise would claim the estate had been checked when it
          // has not.
          <p className="muted">
            This endpoint has not reported drivers yet. Driver inventory needs agent 1.3.0 or
            newer; until then its driver health is unknown rather than healthy.
          </p>
        ) : (
          <>
            <div className="row-sub" style={{ marginBottom: 12 }}>
              <span className={`badge ${driverHealthTone(health.state)}`}>
                {driverHealthLabel(health.state)}
              </span>
              <span className="muted">
                {health.totalCount} device{health.totalCount === 1 ? '' : 's'} · last reported{' '}
                {new Date(health.lastReportedAt).toLocaleString()}
              </span>
            </div>

            <dl className="kv">
              <dt>Driver faults</dt>
              <dd>{health.driverFaultCount}</dd>

              <dt>Hardware faults</dt>
              <dd>{health.deviceFaultCount}</dd>

              <dt>Unattributed faults</dt>
              <dd>{health.indeterminateFaultCount}</dd>

              {/* Reported separately and never counted as a fault. This platform
                  disables devices itself; USB restriction is exactly that. */}
              <dt>Disabled devices</dt>
              <dd>
                {health.disabledCount}
                {health.disabledCount > 0 && (
                  <span className="muted"> — intended state, not a fault</span>
                )}
              </dd>

              <dt>Unreadable</dt>
              <dd>{health.unknownCount}</dd>
            </dl>

            <p className="muted" style={{ marginTop: 10 }}>
              {health.limitation}
            </p>
          </>
        )}
      </div>

      {health.lastReportedAt !== null && (
        <div className="card">
          <div className="row-sub" style={{ justifyContent: 'space-between', marginBottom: 10 }}>
            <h2 style={{ margin: 0 }}>
              {showAll ? 'All devices' : 'Devices with problems'}
              <span className="badge neutral plain" style={{ marginLeft: 8 }}>
                {visible.length}
              </span>
            </h2>
            <button type="button" className="btn-ghost btn-sm" onClick={() => setShowAll((v) => !v)}>
              {showAll ? 'Show problems only' : `Show all ${drivers.length}`}
            </button>
          </div>

          {visible.length === 0 ? (
            <p className="muted">
              {showAll
                ? 'No devices reported.'
                : 'No device on this endpoint is reporting a driver problem.'}
            </p>
          ) : (
            <div style={{ overflowX: 'auto' }}>
              <table className="table">
                <thead>
                  <tr>
                    <th>Device</th>
                    <th>Status</th>
                    <th>Provider</th>
                    <th>Version</th>
                    <th>Date</th>
                    <th>INF</th>
                    <th>Signed</th>
                  </tr>
                </thead>
                <tbody>
                  {visible.map((d) => {
                    const signed = signedLabel(d.isSigned)
                    const fault = faultKindLabel(d.faultKind)

                    return (
                      <tr key={d.instanceId}>
                        <td>
                          {d.deviceName}
                          <div className="muted" style={{ fontSize: '0.85em' }}>
                            {d.deviceClass ?? 'Unclassified'}
                            {d.manufacturer ? ` · ${d.manufacturer}` : ''}
                          </div>
                        </td>
                        <td>
                          <span className={`badge ${driverHealthTone(d.health)}`}>
                            {driverHealthLabel(d.health)}
                          </span>
                          {d.health !== 'Healthy' && (
                            <div className="muted" style={{ fontSize: '0.85em', marginTop: 4 }}>
                              {/* Both the platform's reading and the raw code an
                                  engineer will search for. */}
                              {fault ? `${fault} · ` : ''}
                              {d.problemCode !== null ? `code ${d.problemCode} · ` : ''}
                              {d.problemDescription}
                            </div>
                          )}
                        </td>
                        <td>{d.driverProvider ?? '—'}</td>
                        <td>{d.driverVersion ?? '—'}</td>
                        <td>{formatDriverDate(d.driverDate)}</td>
                        <td>{d.infName ?? '—'}</td>
                        <td>
                          <span className={`badge ${signed.tone}`}>{signed.text}</span>
                        </td>
                      </tr>
                    )
                  })}
                </tbody>
              </table>
            </div>
          )}

          <div className="row-sub" style={{ marginTop: 12 }}>
            <button type="button" className="btn-ghost btn-sm" onClick={() => void load()}>
              <Icon name="refresh" size={14} />
              Reload
            </button>
          </div>
        </div>
      )}
    </>
  )
}
