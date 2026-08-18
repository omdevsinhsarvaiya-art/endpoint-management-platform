import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import {
  getDevice,
  requestInventoryRefresh,
  type DeviceDetail,
} from '../api/client'

type Tab = 'overview' | 'hardware' | 'network' | 'users' | 'groups'

function formatBytes(bytes: number | null): string {
  if (bytes == null) return '—'
  const units = ['B', 'KB', 'MB', 'GB', 'TB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`
}

function formatTimestamp(value: string | null): string {
  return value ? new Date(value).toLocaleString() : '—'
}

export function DeviceDetailPage() {
  const { deviceId } = useParams<{ deviceId: string }>()
  const [device, setDevice] = useState<DeviceDetail | null>(null)
  const [tab, setTab] = useState<Tab>('overview')
  const [error, setError] = useState<string | null>(null)
  const [refreshRequested, setRefreshRequested] = useState(false)

  const load = useCallback(async () => {
    if (!deviceId) return
    try {
      setDevice(await getDevice(deviceId))
      setError(null)
    } catch {
      setError('Could not load this device.')
    }
  }, [deviceId])

  useEffect(() => {
    void load()
    const timer = setInterval(() => void load(), 30_000)
    return () => clearInterval(timer)
  }, [load])

  async function onRefreshInventory() {
    if (!deviceId) return
    try {
      await requestInventoryRefresh(deviceId)
      setRefreshRequested(true)
      await load()
    } catch {
      setError('Inventory refresh request failed.')
    }
  }

  if (error && !device) {
    return <div className="error-banner">{error}</div>
  }

  if (!device) {
    return <div className="loading">Loading device…</div>
  }

  const tabs: { key: Tab; label: string }[] = [
    { key: 'overview', label: 'Overview' },
    { key: 'hardware', label: 'Hardware' },
    { key: 'network', label: 'Network' },
    { key: 'users', label: `Users${device.localUsers.length ? ` (${device.localUsers.length})` : ''}` },
    { key: 'groups', label: `Groups${device.localGroups.length ? ` (${device.localGroups.length})` : ''}` },
  ]

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div style={{ marginBottom: 16, display: 'flex', justifyContent: 'space-between', alignItems: 'center' }}>
        <div>
          <Link to="/devices">← Devices</Link>
          <span style={{ margin: '0 8px', color: 'var(--color-text-muted)' }}>/</span>
          <strong>{device.hostname}</strong>{' '}
          {device.status === 'Retired' ? (
            <span className="badge neutral">Retired</span>
          ) : (
            <span className="badge ok">Active</span>
          )}
        </div>
        <button type="button" onClick={() => void onRefreshInventory()} disabled={device.inventoryRefreshPending}>
          {device.inventoryRefreshPending
            ? 'Inventory refresh pending…'
            : refreshRequested
              ? 'Refresh again'
              : 'Refresh inventory'}
        </button>
      </div>

      <div style={{ display: 'flex', gap: 4, marginBottom: 16, borderBottom: '1px solid var(--color-border)' }}>
        {tabs.map((t) => (
          <button
            key={t.key}
            type="button"
            onClick={() => setTab(t.key)}
            style={{
              border: 'none',
              background: 'none',
              font: 'inherit',
              padding: '8px 14px',
              cursor: 'pointer',
              borderBottom: tab === t.key ? '2px solid var(--color-primary)' : '2px solid transparent',
              fontWeight: tab === t.key ? 600 : 400,
              color: tab === t.key ? 'var(--color-text)' : 'var(--color-text-muted)',
            }}
          >
            {t.label}
          </button>
        ))}
      </div>

      {tab === 'overview' && (
        <div className="card">
          <table className="table">
            <tbody>
              <tr><td style={{ width: 220, color: 'var(--color-text-muted)' }}>Hostname</td><td>{device.hostname}</td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Operating system</td><td>{device.operatingSystem ?? '—'}</td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Logged-on user</td><td>{device.loggedOnUser ?? '—'}</td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Agent version</td><td><code>{device.agentVersion}</code></td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Last seen</td><td>{formatTimestamp(device.lastSeenAt)}</td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Enrolled</td><td>{formatTimestamp(device.enrolledAt)}</td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Inventory collected</td><td>{formatTimestamp(device.inventoryCollectedAt)}</td></tr>
              <tr><td style={{ color: 'var(--color-text-muted)' }}>Machine identifier</td><td><code>{device.machineIdentifier}</code></td></tr>
            </tbody>
          </table>
        </div>
      )}

      {tab === 'hardware' && (
        <>
          {!device.hardware && (
            <div className="card">
              <div className="empty-state">
                <div className="title">No hardware inventory yet</div>
                <div>Use "Refresh inventory" and wait for the agent's next heartbeat.</div>
              </div>
            </div>
          )}
          {device.hardware && (
            <>
              <div className="card card-section">
                <h2>System</h2>
                <table className="table">
                  <tbody>
                    <tr><td style={{ width: 220, color: 'var(--color-text-muted)' }}>Manufacturer</td><td>{device.hardware.manufacturer ?? '—'}</td></tr>
                    <tr><td style={{ color: 'var(--color-text-muted)' }}>Model</td><td>{device.hardware.model ?? '—'}</td></tr>
                    <tr><td style={{ color: 'var(--color-text-muted)' }}>Serial number</td><td>{device.hardware.serialNumber ?? '—'}</td></tr>
                    <tr><td style={{ color: 'var(--color-text-muted)' }}>CPU</td><td>{device.hardware.cpuName ?? '—'}</td></tr>
                    <tr>
                      <td style={{ color: 'var(--color-text-muted)' }}>Cores</td>
                      <td>
                        {device.hardware.cpuPhysicalCores ?? '—'} physical /{' '}
                        {device.hardware.cpuLogicalProcessors ?? '—'} logical
                      </td>
                    </tr>
                    <tr><td style={{ color: 'var(--color-text-muted)' }}>Memory</td><td>{formatBytes(device.hardware.totalMemoryBytes)}</td></tr>
                  </tbody>
                </table>
              </div>

              <div className="card card-section">
                <h2>Disks</h2>
                {!device.hardware.disks?.length && <div className="loading">No fixed volumes reported.</div>}
                {!!device.hardware.disks?.length && (
                  <table className="table">
                    <thead>
                      <tr><th>Volume</th><th>File system</th><th>Size</th><th>Free</th><th>Used</th></tr>
                    </thead>
                    <tbody>
                      {device.hardware.disks.map((disk) => {
                        const usedPct = disk.sizeBytes > 0
                          ? Math.round(((disk.sizeBytes - disk.freeBytes) / disk.sizeBytes) * 100)
                          : 0
                        return (
                          <tr key={disk.name}>
                            <td style={{ fontWeight: 600 }}>{disk.name}</td>
                            <td>{disk.fileSystem ?? '—'}</td>
                            <td>{formatBytes(disk.sizeBytes)}</td>
                            <td>{formatBytes(disk.freeBytes)}</td>
                            <td>
                              <span className={usedPct >= 90 ? 'badge crit' : usedPct >= 75 ? 'badge warn' : 'badge ok'}>
                                {usedPct}%
                              </span>
                            </td>
                          </tr>
                        )
                      })}
                    </tbody>
                  </table>
                )}
              </div>
            </>
          )}
        </>
      )}

      {tab === 'users' && (
        <div className="card">
          {!device.localUsers.length && (
            <div className="empty-state">
              <div className="title">No local user inventory yet</div>
              <div>Use "Refresh inventory" and wait for the agent's next heartbeat.</div>
            </div>
          )}
          {!!device.localUsers.length && (
            <table className="table">
              <thead>
                <tr>
                  <th>Account</th><th>Status</th><th>Type</th>
                  <th>Password</th><th>Last logon</th><th>Description</th>
                </tr>
              </thead>
              <tbody>
                {device.localUsers.map((u) => (
                  <tr key={u.sid}>
                    <td>
                      <div style={{ fontWeight: 600 }}>{u.name}</div>
                      {u.fullName && (
                        <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{u.fullName}</div>
                      )}
                    </td>
                    <td>
                      {u.enabled
                        ? <span className="badge ok">Enabled</span>
                        : <span className="badge neutral">Disabled</span>}
                    </td>
                    <td>
                      {u.isLocalAdministrator
                        ? <span className="badge warn">Administrator</span>
                        : <span className="badge neutral">Standard</span>}
                    </td>
                    <td style={{ fontSize: 12.5 }}>
                      {u.passwordRequired ? 'required' : 'not required'}
                      {' · '}
                      {u.passwordExpires ? 'expires' : 'never expires'}
                    </td>
                    <td>{u.lastLogon ? new Date(u.lastLogon).toLocaleString() : '—'}</td>
                    <td style={{ color: 'var(--color-text-muted)', fontSize: 12.5 }}>{u.description ?? ''}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {tab === 'groups' && (
        <div className="card">
          {!device.localGroups.length && (
            <div className="empty-state">
              <div className="title">No local group inventory yet</div>
              <div>Use "Refresh inventory" and wait for the agent's next heartbeat.</div>
            </div>
          )}
          {!!device.localGroups.length && (
            <table className="table">
              <thead>
                <tr><th>Group</th><th>Members</th><th>Membership</th></tr>
              </thead>
              <tbody>
                {device.localGroups.map((g) => (
                  <tr key={g.sid}>
                    <td>
                      <div style={{ fontWeight: 600 }}>
                        {g.name}{' '}
                        {g.isAdministrators && <span className="badge warn">high impact</span>}
                      </div>
                      {g.description && (
                        <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{g.description}</div>
                      )}
                    </td>
                    <td>{g.memberCount}</td>
                    <td style={{ fontSize: 12.5 }}>
                      {g.members?.length
                        ? g.members.map((m) => m.name).join(', ')
                        : '—'}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {tab === 'network' && (
        <div className="card">
          {!device.networkInterfaces.length && (
            <div className="empty-state">
              <div className="title">No network inventory yet</div>
              <div>Use "Refresh inventory" and wait for the agent's next heartbeat.</div>
            </div>
          )}
          {!!device.networkInterfaces.length && (
            <table className="table">
              <thead>
                <tr><th>Interface</th><th>Status</th><th>MAC address</th><th>IP addresses</th></tr>
              </thead>
              <tbody>
                {device.networkInterfaces.map((nic) => (
                  <tr key={nic.name}>
                    <td style={{ fontWeight: 600 }}>{nic.name}</td>
                    <td>
                      {nic.isUp
                        ? <span className="badge ok">Up</span>
                        : <span className="badge neutral">Down</span>}
                    </td>
                    <td><code>{nic.macAddress ?? '—'}</code></td>
                    <td>{nic.ipAddresses?.length ? nic.ipAddresses.join(', ') : '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}
    </>
  )
}
