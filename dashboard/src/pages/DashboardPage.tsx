import { useEffect, useState } from 'react'
import {
  getDeviceCounts,
  getFleetReport,
  getReadiness,
  getServiceInfo,
  type DeviceCounts,
  type FleetReport,
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

function ReportItem({ label, value, tone }: { label: string; value: string; tone?: 'ok' | 'warn' | 'crit' }) {
  const color =
    tone === 'ok' ? 'var(--color-ok, #16a34a)' : tone === 'warn' ? 'var(--color-warn, #b8860b)' : tone === 'crit' ? 'var(--color-crit, #c0392b)' : 'var(--color-text)'
  return (
    <div style={{ padding: '10px 12px', border: '1px solid var(--color-border)', borderRadius: 8 }}>
      <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{label}</div>
      <div style={{ fontSize: 22, fontWeight: 700, color }}>{value}</div>
    </div>
  )
}

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
  const [report, setReport] = useState<FleetReport | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  useEffect(() => {
    let cancelled = false

    async function load() {
      try {
        const [info, readiness, deviceCounts, fleetReport] = await Promise.all([
          getServiceInfo(),
          getReadiness(),
          getDeviceCounts(),
          getFleetReport().catch(() => null),
        ])
        if (!cancelled) {
          setServiceInfo(info)
          setHealth(readiness)
          setCounts(deviceCounts)
          setReport(fleetReport)
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

  const reportStats = report
    ? [
        { label: 'Reboot Pending', value: String(report.updates.rebootPending) },
        { label: 'Needs Attention', value: String(report.security.needsAttention + report.security.critical) },
      ]
    : PLACEHOLDER_STATS

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div className="stat-grid">
        {[...deviceStats, ...reportStats].map((stat) => (
          <div key={stat.label} className="card stat-card">
            <div className="stat-label">{stat.label}</div>
            <div className="stat-value">{stat.value}</div>
          </div>
        ))}
      </div>

      {report && (
        <div className="card card-section">
          <h2>Fleet report</h2>
          <div style={{ display: 'grid', gridTemplateColumns: 'repeat(auto-fit, minmax(160px, 1fr))', gap: 12 }}>
            <ReportItem label="Avg security score" value={report.security.averageScore == null ? '—' : `${report.security.averageScore}%`} />
            <ReportItem label="Devices w/ failed updates" value={String(report.updates.withFailedUpdates)} tone={report.updates.withFailedUpdates > 0 ? 'crit' : undefined} />
            <ReportItem label="Enabled policies" value={String(report.policies.enabledPolicies)} />
            <ReportItem label="Non-compliant results" value={String(report.policies.nonCompliantResults)} tone={report.policies.nonCompliantResults > 0 ? 'warn' : undefined} />
            <ReportItem label="Tasks queued" value={String(report.tasks.queued)} />
            <ReportItem label="Tasks succeeded" value={String(report.tasks.succeeded)} tone="ok" />
            <ReportItem label="Tasks failed" value={String(report.tasks.failed)} tone={report.tasks.failed > 0 ? 'crit' : undefined} />
            <ReportItem label="Active packages" value={String(report.activePackages)} />
          </div>
        </div>
      )}

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
