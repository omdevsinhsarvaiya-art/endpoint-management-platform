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
import { Icon } from '../components/Icon'

type Tone = 'ok' | 'warn' | 'crit' | 'muted' | undefined

/**
 * Device tiles show live counts (Phase 1). The remaining tiles keep em-dashes
 * rather than zeroes: "0 pending updates" would be a claim about the estate,
 * and those collectors do not exist yet (Phases 8/12).
 */
const PLACEHOLDER_STATS: StatTile[] = [
  { label: 'Pending Updates', value: '—', note: 'Collector not implemented' },
  { label: 'Security Alerts', value: '—', note: 'Collector not implemented' },
]

interface StatTile {
  label: string
  value: string
  tone?: Tone
  note?: string
}

function StatCard({ label, value, tone, note }: StatTile) {
  const unknown = value === '—'
  return (
    <div className={`card stat-card${tone && !unknown ? ` tone-${tone}` : ''}`}>
      <div className="stat-label">{label}</div>
      <div className={`stat-value${unknown ? ' unknown' : ''}`}>{value}</div>
      {note && <div className="stat-note">{note}</div>}
    </div>
  )
}

function Metric({ label, value, tone }: { label: string; value: string; tone?: Tone }) {
  return (
    <div className={`metric${tone ? ` tone-${tone}` : ''}`}>
      <div className="metric-label">{label}</div>
      <div className="metric-value">{value}</div>
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

  // Tones describe the estate, not the tile. "Total" and "Retired" are facts
  // rather than states, so they stay neutral; offline machines only turn amber
  // when there actually are some.
  const deviceStats: StatTile[] = [
    { label: 'Total Devices', value: counts ? String(counts.total) : '—' },
    { label: 'Online', value: counts ? String(counts.online) : '—', tone: 'ok' },
    {
      label: 'Offline',
      value: counts ? String(counts.offline) : '—',
      tone: counts && counts.offline > 0 ? 'warn' : undefined,
    },
    { label: 'Retired', value: counts ? String(counts.retired) : '—', tone: 'muted' },
  ]

  const reportStats: StatTile[] = report
    ? [
        {
          label: 'Reboot Pending',
          value: String(report.updates.rebootPending),
          tone: report.updates.rebootPending > 0 ? 'warn' : undefined,
        },
        {
          label: 'Needs Attention',
          value: String(report.security.needsAttention + report.security.critical),
          tone: report.security.needsAttention + report.security.critical > 0 ? 'crit' : undefined,
        },
      ]
    : PLACEHOLDER_STATS

  return (
    <>
      {error && (
        <div className="error-banner">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="stat-grid">
        {[...deviceStats, ...reportStats].map((stat) => (
          <StatCard key={stat.label} {...stat} />
        ))}
      </div>

      {report && (
        <div className="card card-section">
          <h2>Fleet report</h2>
          <div className="metric-grid">
            <Metric
              label="Avg security score"
              value={report.security.averageScore == null ? '—' : `${report.security.averageScore}%`}
            />
            <Metric
              label="Devices w/ failed updates"
              value={String(report.updates.withFailedUpdates)}
              tone={report.updates.withFailedUpdates > 0 ? 'crit' : undefined}
            />
            <Metric label="Enabled policies" value={String(report.policies.enabledPolicies)} />
            <Metric
              label="Non-compliant results"
              value={String(report.policies.nonCompliantResults)}
              tone={report.policies.nonCompliantResults > 0 ? 'warn' : undefined}
            />
            <Metric label="Tasks queued" value={String(report.tasks.queued)} />
            <Metric label="Tasks succeeded" value={String(report.tasks.succeeded)} tone="ok" />
            <Metric
              label="Tasks failed"
              value={String(report.tasks.failed)}
              tone={report.tasks.failed > 0 ? 'crit' : undefined}
            />
            <Metric label="Active packages" value={String(report.activePackages)} />
          </div>
        </div>
      )}

      <div className="card card-section">
        <h2>Platform status</h2>
        {loading && <div className="loading">Checking platform status…</div>}
        {!loading && serviceInfo && health && (
          <div className="table-wrap">
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
          </div>
        )}
        {!loading && !serviceInfo && !error && <div className="loading">No status available.</div>}
      </div>

      <div className="card card-section">
        <h2>Recent activity</h2>
        <div className="empty-state">
          <Icon name="audit" size={40} strokeWidth={1.25} className="icon" />
          <div className="title">No activity yet</div>
          <div>Audit events will appear here once devices enroll and administrators act.</div>
        </div>
      </div>
    </>
  )
}
