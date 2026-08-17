import { useEffect, useState } from 'react'
import {
  getDeviceCounts,
  getReadiness,
  getServiceInfo,
  type DeviceCounts,
  type HealthReport,
  type ServiceInfo,
} from '../api/client'

/**
 * Device tiles show live counts (Phase 1). The remaining tiles keep em-dashes
 * rather than zeroes: "0 pending updates" would be a claim about the estate,
 * and those collectors do not exist yet (Phases 8/12).
 */
const PLACEHOLDER_STATS = [
  { label: 'Pending Updates', value: '—' },
  { label: 'Security Alerts', value: '—' },
]

function statusBadgeClass(status: string): string {
  switch (status) {
    case 'Healthy':
      return 'badge ok'
    case 'Degraded':
      return 'badge warn'
    default:
      return 'badge crit'
  }
}

export function DashboardPage() {
  const [serviceInfo, setServiceInfo] = useState<ServiceInfo | null>(null)
  const [health, setHealth] = useState<HealthReport | null>(null)
  const [counts, setCounts] = useState<DeviceCounts | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const [info, readiness, deviceCounts] = await Promise.all([
          getServiceInfo(),
          getReadiness(),
          getDeviceCounts(),
        ])
        if (!cancelled) {
          setServiceInfo(info)
          setHealth(readiness)
          setCounts(deviceCounts)
          setError(null)
        }
      } catch {
        if (!cancelled) {
          setError(
            'The Admin API is unreachable. Check that the backend is running and that ' +
              'the dev proxy target matches its port (see docs/development.md).',
          )
        }
      } finally {
        if (!cancelled) {
          setLoading(false)
        }
      }
    }

    void load()
    const timer = setInterval(() => void load(), 30_000)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [])

  const deviceStats = [
    { label: 'Total Devices', value: counts ? String(counts.total) : '—' },
    { label: 'Online', value: counts ? String(counts.online) : '—' },
    { label: 'Offline', value: counts ? String(counts.offline) : '—' },
    { label: 'Retired', value: counts ? String(counts.retired) : '—' },
  ]

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div className="stat-grid">
        {[...deviceStats, ...PLACEHOLDER_STATS].map((stat) => (
          <div key={stat.label} className="card stat-card">
            <div className="stat-label">{stat.label}</div>
            <div className="stat-value">{stat.value}</div>
          </div>
        ))}
      </div>

      <div className="card card-section">
        <h2>Platform status</h2>
        {loading && <div className="loading">Checking platform status…</div>}
        {!loading && serviceInfo && health && (
          <table className="table">
            <thead>
              <tr>
                <th>Component</th>
                <th>Status</th>
                <th>Detail</th>
              </tr>
            </thead>
            <tbody>
              <tr>
                <td>Admin API</td>
                <td>
                  <span className="badge ok">Reachable</span>
                </td>
                <td>
                  <code>{serviceInfo.service}</code> v{serviceInfo.version} ·{' '}
                  {serviceInfo.environment}
                </td>
              </tr>
              {health.checks.map((check) => (
                <tr key={check.name}>
                  <td style={{ textTransform: 'capitalize' }}>{check.name}</td>
                  <td>
                    <span className={statusBadgeClass(check.status)}>{check.status}</span>
                  </td>
                  <td>{check.durationMs.toFixed(1)} ms</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
        {!loading && !serviceInfo && !error && (
          <div className="loading">No status available.</div>
        )}
      </div>

      <div className="card card-section">
        <h2>Recent activity</h2>
        <div className="empty-state">
          <div className="title">No activity yet</div>
          <div>
            Audit events will appear here once devices enroll and administrators act.
          </div>
        </div>
      </div>
    </>
  )
}
