import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { getDevices, type DevicePage } from '../api/client'

const PAGE_SIZE = 25

function formatLastSeen(lastSeenAt: string | null): string {
  if (!lastSeenAt) {
    return 'never'
  }

  const seconds = Math.floor((Date.now() - new Date(lastSeenAt).getTime()) / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds}s ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)}m ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)}h ago`
  return new Date(lastSeenAt).toLocaleString()
}

export function DevicesPage() {
  const [data, setData] = useState<DevicePage | null>(null)
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await getDevices(page, PAGE_SIZE, search))
      setError(null)
    } catch {
      setError('Could not load devices from the Admin API.')
    } finally {
      setLoading(false)
    }
  }, [page, search])

  useEffect(() => {
    void load()
    // Device status changes as heartbeats arrive; refresh every 30s.
    const timer = setInterval(() => void load(), 30_000)
    return () => clearInterval(timer)
  }, [load])

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div className="card card-section">
        <div style={{ display: 'flex', gap: 12, marginBottom: 14 }}>
          <input
            type="search"
            placeholder="Search by hostname…"
            value={search}
            onChange={(event) => {
              setPage(1)
              setSearch(event.target.value)
            }}
            style={{
              flex: '0 1 320px',
              padding: '7px 12px',
              border: '1px solid var(--color-border)',
              borderRadius: 6,
              font: 'inherit',
            }}
          />
        </div>

        {loading && !data && <div className="loading">Loading devices…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <div className="title">No devices found</div>
            <div>
              {search
                ? 'No hostname matches this search.'
                : 'Enroll an agent to see it here. Issue an enrollment token under Settings, then install the agent with it.'}
            </div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <table className="table">
              <thead>
                <tr>
                  <th>Hostname</th>
                  <th>Status</th>
                  <th>Operating system</th>
                  <th>Agent</th>
                  <th>Last seen</th>
                  <th>Enrolled</th>
                </tr>
              </thead>
              <tbody>
                {data.items.map((device) => (
                  <tr key={device.id}>
                    <td style={{ fontWeight: 600 }}>
                      <Link to={`/devices/${device.id}`}>{device.hostname}</Link>
                    </td>
                    <td>
                      {device.status === 'Retired' ? (
                        <span className="badge neutral">Retired</span>
                      ) : device.isOnline ? (
                        <span className="badge ok">Online</span>
                      ) : (
                        <span className="badge crit">Offline</span>
                      )}
                    </td>
                    <td>{device.operatingSystem ?? '—'}</td>
                    <td>
                      <code>{device.agentVersion}</code>
                    </td>
                    <td>{formatLastSeen(device.lastSeenAt)}</td>
                    <td>{new Date(device.enrolledAt).toLocaleDateString()}</td>
                  </tr>
                ))}
              </tbody>
            </table>

            <div
              style={{
                display: 'flex',
                justifyContent: 'space-between',
                alignItems: 'center',
                marginTop: 14,
                color: 'var(--color-text-muted)',
                fontSize: 13,
              }}
            >
              <span>
                {data.totalCount} device{data.totalCount === 1 ? '' : 's'}
              </span>
              <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>
                  Previous
                </button>
                <span>
                  Page {page} of {totalPages}
                </span>
                <button type="button" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>
                  Next
                </button>
              </span>
            </div>
          </>
        )}
      </div>
    </>
  )
}
