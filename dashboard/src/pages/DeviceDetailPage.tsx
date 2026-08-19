import { useCallback, useEffect, useState } from 'react'
import { Link, useParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { DeviceUsersPanel } from './DeviceUsersPanel'
import { DeviceGroupsPanel } from './DeviceGroupsPanel'
import {
  getDevice,
  getDeviceTasks,
  offboardDevice,
  queueDeviceAction,
  reactivateDevice,
  requestInventoryRefresh,
  type DeviceDetail,
  type DeviceTaskItem,
} from '../api/client'

type Tab = 'overview' | 'hardware' | 'network' | 'users' | 'groups' | 'software' | 'security' | 'updates' | 'services' | 'processes' | 'actions' | 'tasks'

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
  const { hasPermission } = useAuth()
  const [device, setDevice] = useState<DeviceDetail | null>(null)
  const [tasks, setTasks] = useState<DeviceTaskItem[]>([])
  const [confirm, setConfirm] = useState<null | 'restart' | 'shutdown' | 'lock' | 'signout'>(null)
  const [actionMsg, setActionMsg] = useState<string | null>(null)
  const [tab, setTab] = useState<Tab>('overview')
  const [error, setError] = useState<string | null>(null)
  const [refreshRequested, setRefreshRequested] = useState(false)

  const load = useCallback(async () => {
    if (!deviceId) return
    try {
      const [d, t] = await Promise.all([
        getDevice(deviceId),
        getDeviceTasks(deviceId).catch(() => [] as DeviceTaskItem[]),
      ])
      setDevice(d)
      setTasks(t)
      setError(null)
    } catch {
      setError('Could not load this device.')
    }
  }, [deviceId])

  async function runAction(action: 'restart' | 'shutdown' | 'lock' | 'signout') {
    if (!deviceId) return
    setConfirm(null)
    try {
      await queueDeviceAction(deviceId, action)
      setActionMsg(`Queued "${action}". The device runs it on its next check-in; watch the Tasks tab.`)
      await load()
    } catch {
      setActionMsg(`Could not queue "${action}".`)
    }
  }

  async function onOffboard() {
    if (!deviceId) return
    if (!window.confirm(
      'Offboard this device? Its credentials are revoked and it is retired: it can no longer check in, '
      + 'receive tasks, or re-enroll until reactivated. This does not wipe the machine.',
    )) return
    try {
      await offboardDevice(deviceId)
      setActionMsg('Device offboarded: credentials revoked and device retired.')
      await load()
    } catch {
      setActionMsg('Could not offboard the device.')
    }
  }

  async function onReactivate() {
    if (!deviceId) return
    try {
      await reactivateDevice(deviceId)
      setActionMsg('Device reactivated. The machine must re-enroll to obtain a fresh credential.')
      await load()
    } catch {
      setActionMsg('Could not reactivate the device.')
    }
  }

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
    { key: 'software', label: `Software${device.software.length ? ` (${device.software.length})` : ''}` },
    { key: 'security', label: 'Security' },
    { key: 'updates', label: `Updates${device.windowsUpdate?.history.length ? ` (${device.windowsUpdate.history.length})` : ''}` },
    { key: 'services', label: `Services${device.services.length ? ` (${device.services.length})` : ''}` },
    { key: 'processes', label: `Processes${device.processes.length ? ` (${device.processes.length})` : ''}` },
    { key: 'actions', label: 'Actions' },
    { key: 'tasks', label: `Tasks${tasks.length ? ` (${tasks.length})` : ''}` },
  ]

  const actionButtons: { key: 'restart' | 'shutdown' | 'lock' | 'signout'; label: string; perm: string; danger?: boolean }[] = [
    { key: 'lock', label: 'Lock screen', perm: 'device.lock' },
    { key: 'signout', label: 'Sign out user', perm: 'device.sign_out_user', danger: true },
    { key: 'restart', label: 'Restart', perm: 'device.restart', danger: true },
    { key: 'shutdown', label: 'Shut down', perm: 'device.shutdown', danger: true },
  ]

  return (
    <>
      {error && <div className="error-banner">{error}</div>}
      {actionMsg && <div className="error-banner" style={{ background: 'var(--color-ok-bg)', color: 'var(--color-ok)', borderColor: '#bbf7d0' }}>{actionMsg}</div>}

      {confirm && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', display: 'flex', alignItems: 'center', justifyContent: 'center', zIndex: 50 }}>
          <div className="card" style={{ width: 420, padding: 24 }}>
            <h2 style={{ marginTop: 0 }}>Confirm {confirm}</h2>
            <p style={{ color: 'var(--color-text-muted)', fontSize: 14 }}>
              This queues a <strong>{confirm}</strong> task for <strong>{device.hostname}</strong>. The device
              performs it on its next check-in. This action is audited.
            </p>
            <div style={{ display: 'flex', gap: 8, justifyContent: 'flex-end', marginTop: 20 }}>
              <button type="button" onClick={() => setConfirm(null)}>Cancel</button>
              <button type="button" onClick={() => void runAction(confirm)}
                style={{ background: 'var(--color-crit)', color: '#fff', border: 'none', borderRadius: 6, padding: '7px 14px', fontWeight: 600, cursor: 'pointer' }}>
                Yes, {confirm}
              </button>
            </div>
          </div>
        </div>
      )}

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

      {tab === 'users' && <DeviceUsersPanel deviceId={deviceId!} deviceName={device.hostname} />}

      {tab === 'groups' && <DeviceGroupsPanel deviceId={deviceId!} />}

      {tab === 'software' && (
        <div className="card">
          {!device.software.length && (
            <div className="empty-state">
              <div className="title">No software inventory yet</div>
              <div>Use "Refresh inventory" and wait for the agent's next heartbeat.</div>
            </div>
          )}
          {!!device.software.length && (
            <table className="table">
              <thead>
                <tr><th>Application</th><th>Version</th><th>Publisher</th><th>Arch</th></tr>
              </thead>
              <tbody>
                {device.software.map((sw) => (
                  <tr key={`${sw.name}|${sw.version}`}>
                    <td style={{ fontWeight: 600 }}>{sw.name}</td>
                    <td>{sw.version ?? '—'}</td>
                    <td>{sw.publisher ?? '—'}</td>
                    <td>{sw.architecture ?? '—'}</td>
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </div>
      )}

      {tab === 'security' && (
        <div className="card">
          {!device.securityPosture && (
            <div className="empty-state">
              <div className="title">No security posture yet</div>
              <div>Use "Refresh inventory" and wait for the agent's next heartbeat. Some checks (BitLocker, TPM) require the agent to run elevated.</div>
            </div>
          )}
          {device.securityPosture && (() => {
            const p = device.securityPosture
            const row = (label: string, v: boolean | null, extra?: string) => (
              <tr>
                <td style={{ color: 'var(--color-text-muted)', width: 260 }}>{label}</td>
                <td>
                  {v == null
                    ? <span style={{ color: 'var(--color-text-muted)' }}>Unknown (agent may need elevation)</span>
                    : v ? <span className="badge ok">Pass</span> : <span className="badge crit">Fail</span>}
                  {extra ? <span style={{ marginLeft: 8, color: 'var(--color-text-muted)', fontSize: 12.5 }}>{extra}</span> : null}
                </td>
              </tr>
            )
            return (
              <>
                <div style={{ display: 'flex', alignItems: 'center', gap: 12, marginBottom: 14 }}>
                  <span style={{ fontSize: 15, fontWeight: 600 }}>Compliance score</span>
                  {p.complianceScore == null
                    ? <span className="badge neutral">Unknown</span>
                    : <span className={p.complianceScore >= 80 ? 'badge ok' : p.complianceScore >= 50 ? 'badge warn' : 'badge crit'} style={{ fontSize: 15 }}>{p.complianceScore}%</span>}
                </div>
                <table className="table">
                  <tbody>
                    {row('Microsoft Defender antivirus', p.defenderAntivirusEnabled)}
                    {row('Defender real-time protection', p.defenderRealtimeProtectionEnabled)}
                    {row('Defender signatures fresh (≤7d)', p.defenderSignatureAgeDays == null ? null : p.defenderSignatureAgeDays <= 7, p.defenderSignatureAgeDays == null ? undefined : `${p.defenderSignatureAgeDays}d old`)}
                    {row('Firewall (Domain)', p.firewallDomainEnabled)}
                    {row('Firewall (Private)', p.firewallPrivateEnabled)}
                    {row('Firewall (Public)', p.firewallPublicEnabled)}
                    {row('Secure Boot', p.secureBootEnabled)}
                    {row('TPM enabled', p.tpmEnabled, p.tpmSpecVersion ? `v${p.tpmSpecVersion}` : undefined)}
                    {row('BitLocker (system drive)', p.bitLockerSystemDriveStatus == null ? null : p.bitLockerSystemDriveStatus === 'On', p.bitLockerSystemDriveStatus ?? undefined)}
                    <tr>
                      <td style={{ color: 'var(--color-text-muted)' }}>Local administrators</td>
                      <td>{p.localAdministratorCount ?? '—'}</td>
                    </tr>
                  </tbody>
                </table>
              </>
            )
          })()}
        </div>
      )}

      {tab === 'updates' && (
        <div className="card">
          {!device.windowsUpdate && (
            <div className="empty-state">
              <div className="title">No update data yet</div>
              <div>Refresh inventory and wait for the agent's next heartbeat. History is read from the local Windows Update store.</div>
            </div>
          )}
          {device.windowsUpdate && (() => {
            const u = device.windowsUpdate
            return (
              <>
                <div style={{ display: 'flex', gap: 24, alignItems: 'center', marginBottom: 14, flexWrap: 'wrap' }}>
                  <div>
                    <span style={{ color: 'var(--color-text-muted)', marginRight: 8 }}>Reboot pending</span>
                    {u.rebootRequired ? <span className="badge warn">Yes</span> : <span className="badge ok">No</span>}
                  </div>
                  <div>
                    <span style={{ color: 'var(--color-text-muted)', marginRight: 8 }}>Failed updates</span>
                    {u.failedUpdateCount > 0 ? <span className="badge crit">{u.failedUpdateCount}</span> : <span className="badge neutral">0</span>}
                  </div>
                  <div style={{ color: 'var(--color-text-muted)', fontSize: 12.5 }}>
                    Reported {new Date(u.collectedAt).toLocaleString()}
                  </div>
                </div>
                {u.history.length === 0
                  ? <div className="loading">No update history recorded.</div>
                  : (
                    <div style={{ maxHeight: 520, overflowY: 'auto' }}>
                      <table className="table">
                        <thead><tr><th>Update</th><th>Operation</th><th>Result</th><th>Date</th></tr></thead>
                        <tbody>
                          {u.history.map((h, i) => (
                            <tr key={i}>
                              <td style={{ fontWeight: 600 }}>{h.title}</td>
                              <td>{h.operation}</td>
                              <td>
                                {h.result === 'Succeeded' ? <span className="badge ok">Succeeded</span>
                                  : h.result === 'Failed' || h.result === 'Aborted' ? <span className="badge crit">{h.result}</span>
                                  : <span className="badge neutral">{h.result}</span>}
                              </td>
                              <td style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{h.date ? new Date(h.date).toLocaleString() : '—'}</td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
              </>
            )
          })()}
        </div>
      )}

      {tab === 'services' && (
        <div className="card">
          {!device.services.length && (
            <div className="empty-state"><div className="title">No service inventory yet</div><div>Refresh inventory and wait for the next heartbeat.</div></div>
          )}
          {!!device.services.length && (
            <div style={{ maxHeight: 520, overflowY: 'auto' }}>
              <table className="table">
                <thead><tr><th>Service</th><th>Status</th><th>Start mode</th></tr></thead>
                <tbody>
                  {device.services.map((sv) => (
                    <tr key={sv.name}>
                      <td><div style={{ fontWeight: 600 }}>{sv.displayName}</div><div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{sv.name}</div></td>
                      <td>{sv.status === 'Running' ? <span className="badge ok">Running</span> : <span className="badge neutral">{sv.status}</span>}</td>
                      <td>{sv.startMode}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'processes' && (
        <div className="card">
          {!device.processes.length && (
            <div className="empty-state"><div className="title">No process snapshot yet</div><div>Refresh inventory and wait for the next heartbeat.</div></div>
          )}
          {!!device.processes.length && (
            <>
              <div style={{ color: 'var(--color-text-muted)', fontSize: 12.5, marginBottom: 8 }}>
                Point-in-time snapshot (top by memory) as of {device.processes[0] ? new Date(device.processes[0].collectedAt).toLocaleString() : ''}.
              </div>
              <table className="table">
                <thead><tr><th>Process</th><th>PID</th><th>Memory</th><th>Path</th></tr></thead>
                <tbody>
                  {device.processes.map((pr) => (
                    <tr key={pr.processId}>
                      <td style={{ fontWeight: 600 }}>{pr.name}</td>
                      <td>{pr.processId}</td>
                      <td>{formatBytes(pr.workingSetBytes)}</td>
                      <td style={{ color: 'var(--color-text-muted)', fontSize: 12, maxWidth: 380, overflow: 'hidden', textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{pr.executablePath ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </>
          )}
        </div>
      )}

      {tab === 'actions' && (
        <div className="card">
          <h2>Device actions</h2>
          <p style={{ color: 'var(--color-text-muted)', fontSize: 13.5, marginTop: 0 }}>
            Each action is delivered as a typed task the device pulls on its next check-in. Buttons you
            lack permission for are hidden; the server enforces this regardless.
          </p>
          <div style={{ display: 'flex', flexWrap: 'wrap', gap: 10, marginTop: 12 }}>
            {actionButtons.filter((a) => hasPermission(a.perm)).map((a) => (
              <button key={a.key} type="button"
                onClick={() => (a.key === 'lock' ? void runAction('lock') : setConfirm(a.key))}
                style={{
                  padding: '9px 16px', borderRadius: 6, font: 'inherit', fontWeight: 600, cursor: 'pointer',
                  border: a.danger ? '1px solid #fca5a5' : '1px solid var(--color-border)',
                  color: a.danger ? 'var(--color-crit)' : 'var(--color-text)',
                  background: 'var(--color-surface)',
                }}>
                {a.label}
              </button>
            ))}
            {actionButtons.filter((a) => hasPermission(a.perm)).length === 0 && (
              <div className="loading">Your role grants no device actions.</div>
            )}
          </div>

          {hasPermission('device.retire') && (
            <div style={{ marginTop: 24, paddingTop: 16, borderTop: '1px solid var(--color-border)' }}>
              <h3 style={{ margin: '0 0 4px' }}>Lifecycle</h3>
              <p style={{ color: 'var(--color-text-muted)', fontSize: 13, marginTop: 0 }}>
                Offboarding revokes the device's credentials and retires it — reversible via reactivation, which
                requires the machine to re-enroll. It does not wipe the machine.
              </p>
              {device.status === 'Retired' ? (
                <button type="button" onClick={() => void onReactivate()}
                  style={{ padding: '9px 16px', borderRadius: 6, font: 'inherit', fontWeight: 600, cursor: 'pointer', border: '1px solid var(--color-border)', background: 'var(--color-surface)' }}>
                  Reactivate device
                </button>
              ) : (
                <button type="button" onClick={() => void onOffboard()}
                  style={{ padding: '9px 16px', borderRadius: 6, font: 'inherit', fontWeight: 600, cursor: 'pointer', border: '1px solid #fca5a5', color: 'var(--color-crit)', background: 'var(--color-surface)' }}>
                  Offboard device
                </button>
              )}
            </div>
          )}
        </div>
      )}

      {tab === 'tasks' && (
        <div className="card">
          {!tasks.length && (
            <div className="empty-state">
              <div className="title">No tasks yet</div>
              <div>Queue an action from the Actions tab to see it here.</div>
            </div>
          )}
          {!!tasks.length && (
            <table className="table">
              <thead>
                <tr><th>Task</th><th>Status</th><th>Queued by</th><th>Queued</th><th>Result</th></tr>
              </thead>
              <tbody>
                {tasks.map((t) => (
                  <tr key={t.id}>
                    <td style={{ fontWeight: 600 }}>{t.type}</td>
                    <td>
                      <span className={
                        t.status === 'Succeeded' ? 'badge ok'
                          : t.status === 'Failed' || t.status === 'Expired' ? 'badge crit'
                          : t.status === 'Cancelled' ? 'badge neutral'
                          : 'badge warn'}>{t.status}</span>
                    </td>
                    <td>{t.createdByDisplay}</td>
                    <td>{new Date(t.createdAt).toLocaleString()}</td>
                    <td style={{ color: 'var(--color-text-muted)', fontSize: 12.5 }}>{t.resultMessage ?? '—'}</td>
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
