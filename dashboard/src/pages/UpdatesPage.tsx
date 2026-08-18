import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getUpdateOverview, type UpdateOverview } from '../api/client'

export function UpdatesPage() {
  const [data, setData] = useState<UpdateOverview | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getUpdateOverview()
      .then(setData)
      .catch(() => setError('Could not load update status.'))
  }, [])

  if (error) return <div className="error-banner">{error}</div>
  if (!data) return <div className="loading">Loading update status…</div>

  const { summary, devices } = data

  return (
    <>
      <div style={{ color: 'var(--color-text-muted)', fontSize: 13.5, marginBottom: 16 }}>
        Windows Update visibility across the fleet. The platform reads each device's local update history and
        reboot-pending state during inventory — it reports, and never silently installs or reboots.
      </div>

      <div style={{ display: 'flex', gap: 16, marginBottom: 16, flexWrap: 'wrap' }}>
        <div className="card" style={{ flex: '1 1 180px' }}>
          <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>Devices reporting</div>
          <div style={{ fontSize: 28, fontWeight: 700 }}>{summary.devicesReporting}</div>
        </div>
        <div className="card" style={{ flex: '1 1 180px' }}>
          <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>Reboot pending</div>
          <div style={{ fontSize: 28, fontWeight: 700, color: summary.rebootPending > 0 ? 'var(--color-warn, #b8860b)' : 'inherit' }}>
            {summary.rebootPending}
          </div>
        </div>
        <div className="card" style={{ flex: '1 1 180px' }}>
          <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>With failed updates</div>
          <div style={{ fontSize: 28, fontWeight: 700, color: summary.withFailedUpdates > 0 ? 'var(--color-danger, #c0392b)' : 'inherit' }}>
            {summary.withFailedUpdates}
          </div>
        </div>
      </div>

      <div className="card">
        {devices.length === 0 && (
          <div className="empty-state">
            <div className="title">No update data yet</div>
            <div>Devices report Windows Update history on their next inventory cycle.</div>
          </div>
        )}
        {devices.length > 0 && (
          <table className="table">
            <thead>
              <tr><th>Device</th><th>Reboot pending</th><th>Failed updates</th><th>Reported</th></tr>
            </thead>
            <tbody>
              {devices.map((d) => (
                <tr key={d.deviceId}>
                  <td style={{ fontWeight: 600 }}>
                    <Link to={`/devices/${d.deviceId}`}>{d.hostname}</Link>
                  </td>
                  <td>{d.rebootRequired ? <span className="badge warn">Yes</span> : <span className="badge neutral">No</span>}</td>
                  <td>{d.failedUpdateCount > 0 ? <span className="badge crit">{d.failedUpdateCount}</span> : <span className="badge neutral">0</span>}</td>
                  <td style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{new Date(d.collectedAt).toLocaleString()}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  )
}
