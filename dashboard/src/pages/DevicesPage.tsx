import { useCallback, useEffect, useState } from 'react'
import { Link } from 'react-router-dom'
import { deviceName, getDevices, type DevicePage } from '../api/client'
import { Icon } from '../components/Icon'

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
      {error && (
        <div className="error-banner">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="card card-section">
        <div className="toolbar">
          <div className="input-search">
            <Icon name="search" size={15} className="search-icon" />
            <input
              type="search"
              placeholder="Search name or hostname…"
              aria-label="Search devices by name or hostname"
              value={search}
              onChange={(event) => {
                setPage(1)
                setSearch(event.target.value)
              }}
            />
          </div>
          <div className="spacer" />
          <button type="button" onClick={() => void load()} disabled={loading}>
            <Icon name="refresh" size={14} />
            {loading ? 'Refreshing…' : 'Refresh'}
          </button>
        </div>

        {loading && !data && <div className="loading">Loading devices…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <Icon name="devices" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No devices found</div>
            <div>
              {search ? (
                'No device name or hostname matches this search.'
              ) : (
                <>
                  Install the Windows agent on a PC, then approve it under{' '}
                  <Link to="/enrollments">Pending Enrollments</Link>.
                </>
              )}
            </div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Name</th>
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
                      {/* The administrator's label leads. The Windows hostname
                          keeps its own column so the row still says which
                          physical machine this is. */}
                      <td>
                        <Link to={`/devices/${device.id}`}>{deviceName(device)}</Link>
                      </td>
                      <td className="muted">{device.hostname}</td>
                      <td>
                        {/* Retired is neutral rather than critical: it is an
                            intended end state, not a fault to be investigated. */}
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
                        {device.agentUpdateAvailable && (
                          <div className="row-sub">
                            <span className="badge warn">
                              Update available: {device.latestAgentVersion}
                            </span>
                          </div>
                        )}
                      </td>
                      <td>{formatLastSeen(device.lastSeenAt)}</td>
                      <td>{new Date(device.enrolledAt).toLocaleDateString()}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>

            <div className="pagination">
              <span>
                {data.totalCount} device{data.totalCount === 1 ? '' : 's'}
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
    </>
  )
}
