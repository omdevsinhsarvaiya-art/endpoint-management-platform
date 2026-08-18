import { useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getSecurityOverview, type SecurityOverview } from '../api/client'

function scoreBadge(score: number | null) {
  if (score == null) return <span className="badge neutral">Unknown</span>
  const cls = score >= 80 ? 'badge ok' : score >= 50 ? 'badge warn' : 'badge crit'
  return <span className={cls}>{score}%</span>
}

function checkCell(value: boolean | null) {
  if (value == null) return <span style={{ color: 'var(--color-text-muted)' }}>—</span>
  return value
    ? <span className="badge ok">Pass</span>
    : <span className="badge crit">Fail</span>
}

export function SecurityPage() {
  const [data, setData] = useState<SecurityOverview | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    getSecurityOverview()
      .then(setData)
      .catch(() => setError('Could not load security overview.'))
  }, [])

  if (error) return <div className="error-banner">{error}</div>
  if (!data) return <div className="loading">Loading security overview…</div>

  const s = data.summary

  return (
    <>
      <div className="stat-grid">
        <div className="card stat-card"><div className="stat-label">Devices reporting</div><div className="stat-value">{s.devicesReporting}</div></div>
        <div className="card stat-card"><div className="stat-label">Average score</div><div className="stat-value">{s.averageScore ?? '—'}{s.averageScore != null ? '%' : ''}</div></div>
        <div className="card stat-card"><div className="stat-label">Healthy (≥80%)</div><div className="stat-value" style={{ color: 'var(--color-ok)' }}>{s.healthy}</div></div>
        <div className="card stat-card"><div className="stat-label">Needs attention</div><div className="stat-value" style={{ color: 'var(--color-warn)' }}>{s.needsAttention}</div></div>
        <div className="card stat-card"><div className="stat-label">Critical (&lt;50%)</div><div className="stat-value" style={{ color: 'var(--color-crit)' }}>{s.critical}</div></div>
      </div>

      <div className="card">
        <h2>Device compliance</h2>
        {data.devices.length === 0 && (
          <div className="empty-state">
            <div className="title">No security data yet</div>
            <div>Security posture appears once devices report an inventory that includes it.</div>
          </div>
        )}
        {data.devices.length > 0 && (
          <table className="table">
            <thead>
              <tr>
                <th>Device</th><th>Score</th><th>Defender</th><th>Firewall</th>
                <th>Secure Boot</th><th>TPM</th><th>BitLocker</th><th>Local admins</th>
              </tr>
            </thead>
            <tbody>
              {data.devices.map((d) => (
                <tr key={d.deviceId}>
                  <td style={{ fontWeight: 600 }}><Link to={`/devices/${d.deviceId}`}>{d.hostname}</Link></td>
                  <td>{scoreBadge(d.complianceScore)}</td>
                  <td>{checkCell(d.defenderEnabled)}</td>
                  <td>{checkCell(d.firewallEnabled)}</td>
                  <td>{checkCell(d.secureBootEnabled)}</td>
                  <td>{checkCell(d.tpmEnabled)}</td>
                  <td>
                    {d.bitLockerSystemDriveStatus == null
                      ? <span style={{ color: 'var(--color-text-muted)' }}>—</span>
                      : d.bitLockerSystemDriveStatus === 'On'
                        ? <span className="badge ok">On</span>
                        : <span className="badge crit">{d.bitLockerSystemDriveStatus}</span>}
                  </td>
                  <td>
                    {d.localAdministratorCount == null ? '—'
                      : <span className={d.localAdministratorCount > 3 ? 'badge warn' : 'badge neutral'}>{d.localAdministratorCount}</span>}
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  )
}
