import { useEffect, useState } from 'react'
import {
  getReadiness,
  getServiceInfo,
  type HealthReport,
  type ServiceInfo,
} from '../api/client'

interface StatCard {
  label: string
  value: string
}

/**
 * Phase 0 dashboard: platform connectivity and honest placeholders.
 *
 * The stat tiles show em-dashes rather than zeroes: "0 devices" would be a
 * claim about the estate, and no device inventory exists yet. Real values
 * arrive with enrollment (Phase 1) and inventory (Phase 2).
 */
const PLACEHOLDER_STATS: StatCard[] = [
  { label: 'Total Devices', value: '—' },
  { label: 'Online', value: '—' },
  { label: 'Offline', value: '—' },
  { label: 'Healthy', value: '—' },
  { label: 'Needs Attention', value: '—' },
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
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const [info, readiness] = await Promise.all([getServiceInfo(), getReadiness()])
        if (!cancelled) {
          setServiceInfo(info)
          setHealth(readiness)
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
    return () => {
      cancelled = true
    }
  }, [])

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div className="stat-grid">
        {PLACEHOLDER_STATS.map((stat) => (
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
