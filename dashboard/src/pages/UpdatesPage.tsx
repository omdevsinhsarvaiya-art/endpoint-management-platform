import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getUpdateOverview, type UpdateOverview } from '../api/client'
import { Icon } from '../components/Icon'

export function UpdatesPage() {
  const [data, setData] = useState<UpdateOverview | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getUpdateOverview()
      .then(setData)
      .catch(() => setError('Could not load update status.'))
  }, [])

  if (error) {
    return (
      <div className="error-banner" role="alert">
        <Icon name="alert" size={15} />
        <span>{error}</span>
      </div>
    )
  }
  if (!data) return <div className="loading">Loading update status…</div>

  const { summary, devices } = data

  return (
    <>
      <div className="page-header">
        <div className="lede">
          Windows Update visibility across the fleet. The platform reads each device's local update
          history and reboot-pending state during inventory — it reports, and never silently
          installs or reboots.
        </div>
      </div>

      <div className="stat-grid">
        <div className="card stat-card">
          <div className="stat-label">Devices reporting</div>
          <div className="stat-value">{summary.devicesReporting}</div>
        </div>
        <div className={`card stat-card${summary.rebootPending > 0 ? ' tone-warn' : ''}`}>
          <div className="stat-label">Reboot pending</div>
          <div className="stat-value">{summary.rebootPending}</div>
        </div>
        <div className={`card stat-card${summary.withFailedUpdates > 0 ? ' tone-crit' : ''}`}>
          <div className="stat-label">With failed updates</div>
          <div className="stat-value">{summary.withFailedUpdates}</div>
        </div>
      </div>

      <div className="card">
        <h2>Per-device status</h2>
        {devices.length === 0 && (
          <div className="empty-state">
            <Icon name="updates" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No update data yet</div>
            <div>Devices report Windows Update history on their next inventory cycle.</div>
          </div>
        )}
        {devices.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Device</th>
                  <th>Reboot pending</th>
                  <th>Failed updates</th>
                  <th>Reported</th>
                </tr>
              </thead>
              <tbody>
                {devices.map((d) => (
                  <tr key={d.deviceId}>
                    <td>
                      <Link to={`/devices/${d.deviceId}`}>{d.hostname}</Link>
                    </td>
                    <td>
                      {d.rebootRequired ? (
                        <span className="badge warn">Yes</span>
                      ) : (
                        <span className="badge neutral">No</span>
                      )}
                    </td>
                    <td>
                      <span
                        className={`badge plain ${d.failedUpdateCount > 0 ? 'crit' : 'neutral'}`}
                      >
                        {d.failedUpdateCount}
                      </span>
                    </td>
                    <td className="muted">{new Date(d.collectedAt).toLocaleString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
