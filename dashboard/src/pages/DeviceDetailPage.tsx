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

import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'

type Tab = 'overview' | 'hardware' | 'network' | 'users' | 'groups' | 'software' | 'security' | 'updates' | 'services' | 'processes' | 'actions' | 'tasks'

type ActionKey = 'restart' | 'shutdown' | 'lock' | 'signout'

interface ActionSpec {
  key: ActionKey
  label: string
  perm: string
  /** Whether the action interrupts someone, and so warrants a confirmation. */
  confirm: boolean
}

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

/**
 * The "we have not heard from the machine yet" state, which every inventory tab
 * needs. Says what to do about it rather than only reporting absence.
 */
function InventoryEmpty({ title, detail }: { title: string; detail?: string }) {
  return (
    <div className="card">
      <div className="empty-state">
        <Icon name="inbox" size={40} strokeWidth={1.25} className="icon" />
        <div className="title">{title}</div>
        <div>{detail ?? 'Use "Refresh inventory" and wait for the agent’s next heartbeat.'}</div>
      </div>
    </div>
  )
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
    return (
      <div className="error-banner" role="alert">
        <Icon name="alert" size={15} />
        <span>{error}</span>
      </div>
    )
  }

  if (!device) {
    return <div className="loading">Loading device…</div>
  }

  const tabs: { key: Tab; label: string; count?: number }[] = [
    { key: 'overview', label: 'Overview' },
    { key: 'hardware', label: 'Hardware' },
    { key: 'network', label: 'Network', count: device.networkInterfaces.length },
    { key: 'users', label: 'Users', count: device.localUsers.length },
    { key: 'groups', label: 'Groups', count: device.localGroups.length },
    { key: 'software', label: 'Software', count: device.software.length },
    { key: 'security', label: 'Security' },
    { key: 'updates', label: 'Updates', count: device.windowsUpdate?.history.length },
    { key: 'services', label: 'Services', count: device.services.length },
    { key: 'processes', label: 'Processes', count: device.processes.length },
    { key: 'actions', label: 'Actions' },
    { key: 'tasks', label: 'Tasks', count: tasks.length },
  ]

  // Grouped by consequence. "Lock screen" is recoverable in a second; "Shut
  // down" needs someone physically present to undo. Presenting them as one flat
  // row of identical buttons is what makes the wrong one easy to click.
  const sessionActions: ActionSpec[] = [
    { key: 'lock', label: 'Lock screen', perm: 'device.lock', confirm: false },
    { key: 'signout', label: 'Sign out user', perm: 'device.sign_out_user', confirm: true },
  ]
  const powerActions: ActionSpec[] = [
    { key: 'restart', label: 'Restart', perm: 'device.restart', confirm: true },
    { key: 'shutdown', label: 'Shut down', perm: 'device.shutdown', confirm: true },
  ]

  const visibleSession = sessionActions.filter((a) => hasPermission(a.perm))
  const visiblePower = powerActions.filter((a) => hasPermission(a.perm))

  function actionButton(a: ActionSpec) {
    return (
      <button
        key={a.key}
        type="button"
        className={a.confirm ? 'btn-warning' : undefined}
        onClick={() => (a.confirm ? setConfirm(a.key) : void runAction(a.key))}
      >
        {a.label}
      </button>
    )
  }

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      {actionMsg && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span>{actionMsg}</span>
        </div>
      )}

      {confirm && (
        <ConfirmDialog
          title={`Confirm ${confirm}`}
          confirmLabel={`Yes, ${confirm}`}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void runAction(confirm)}
        >
          <>
            This queues a <strong className="secondary">{confirm}</strong> task for{' '}
            <strong className="secondary">{device.hostname}</strong>. The device performs it on its
            next check-in. This action is audited.
          </>
        </ConfirmDialog>
      )}

      <div className="detail-header">
        <div className="breadcrumb">
          <Link to="/devices">Devices</Link>
          <Icon name="chevron-right" size={12} />
          <span>{device.hostname}</span>
        </div>
        <div className="detail-title">
          <h1>{device.hostname}</h1>
          {device.status === 'Retired' ? (
            <span className="badge neutral">Retired</span>
          ) : (
            <span className="badge ok">Active</span>
          )}
          <span className="spacer" />
          <button
            type="button"
            onClick={() => void onRefreshInventory()}
            disabled={device.inventoryRefreshPending}
          >
            <Icon name="refresh" size={14} />
            {device.inventoryRefreshPending
              ? 'Inventory refresh pending…'
              : refreshRequested
                ? 'Refresh again'
                : 'Refresh inventory'}
          </button>
        </div>
        <div className="detail-facts">
          <span className="fact">
            <span className="fact-label">OS</span>
            <span className="fact-value">{device.operatingSystem ?? '—'}</span>
          </span>
          <span className="fact">
            <span className="fact-label">Agent</span>
            <span className="fact-value">{device.agentVersion}</span>
          </span>
          <span className="fact">
            <span className="fact-label">Signed in</span>
            <span className="fact-value">{device.loggedOnUser ?? '—'}</span>
          </span>
          <span className="fact">
            <span className="fact-label">Last seen</span>
            <span className="fact-value">{formatTimestamp(device.lastSeenAt)}</span>
          </span>
        </div>
      </div>

      <div className="tabs" role="tablist" aria-label="Device sections">
        {tabs.map((t) => (
          <button
            key={t.key}
            type="button"
            role="tab"
            className="tab"
            aria-selected={tab === t.key}
            onClick={() => setTab(t.key)}
          >
            {t.label}
            {t.count ? <span className="tab-count">{t.count}</span> : null}
          </button>
        ))}
      </div>

      {tab === 'overview' && (
        <div className="card">
          <h2>Identity</h2>
          <dl className="kv">
            <dt>Hostname</dt>
            <dd>{device.hostname}</dd>
            <dt>Operating system</dt>
            <dd>{device.operatingSystem ?? '—'}</dd>
            <dt>Logged-on user</dt>
            <dd>{device.loggedOnUser ?? '—'}</dd>
            <dt>Agent version</dt>
            <dd>
              <code>{device.agentVersion}</code>
            </dd>
            <dt>Last seen</dt>
            <dd>{formatTimestamp(device.lastSeenAt)}</dd>
            <dt>Enrolled</dt>
            <dd>{formatTimestamp(device.enrolledAt)}</dd>
            <dt>Inventory collected</dt>
            <dd>{formatTimestamp(device.inventoryCollectedAt)}</dd>
            {/* The SMBIOS UUID: how this record survives a rename or a rebuild. */}
            <dt>Machine identifier</dt>
            <dd>
              <code>{device.machineIdentifier}</code>
            </dd>
          </dl>
        </div>
      )}

      {tab === 'hardware' && (
        <>
          {!device.hardware && <InventoryEmpty title="No hardware inventory yet" />}
          {device.hardware && (
            <>
              <div className="card card-section">
                <h2>System</h2>
                <dl className="kv">
                  <dt>Manufacturer</dt>
                  <dd>{device.hardware.manufacturer ?? '—'}</dd>
                  <dt>Model</dt>
                  <dd>{device.hardware.model ?? '—'}</dd>
                  <dt>Serial number</dt>
                  <dd>{device.hardware.serialNumber ?? '—'}</dd>
                  <dt>CPU</dt>
                  <dd>{device.hardware.cpuName ?? '—'}</dd>
                  <dt>Cores</dt>
                  <dd>
                    {device.hardware.cpuPhysicalCores ?? '—'} physical /{' '}
                    {device.hardware.cpuLogicalProcessors ?? '—'} logical
                  </dd>
                  <dt>Memory</dt>
                  <dd>{formatBytes(device.hardware.totalMemoryBytes)}</dd>
                </dl>
              </div>

              <div className="card card-section">
                <h2>Disks</h2>
                {!device.hardware.disks?.length && (
                  <div className="empty-state">
                    <div className="title">No fixed volumes reported</div>
                  </div>
                )}
                {!!device.hardware.disks?.length && (
                  <div className="table-wrap">
                    <table className="table">
                      <thead>
                        <tr>
                          <th>Volume</th>
                          <th>File system</th>
                          <th>Size</th>
                          <th>Free</th>
                          <th>Used</th>
                        </tr>
                      </thead>
                      <tbody>
                        {device.hardware.disks.map((disk) => {
                          const usedPct =
                            disk.sizeBytes > 0
                              ? Math.round(((disk.sizeBytes - disk.freeBytes) / disk.sizeBytes) * 100)
                              : 0
                          return (
                            <tr key={disk.name}>
                              <td>{disk.name}</td>
                              <td>{disk.fileSystem ?? '—'}</td>
                              <td>{formatBytes(disk.sizeBytes)}</td>
                              <td>{formatBytes(disk.freeBytes)}</td>
                              <td>
                                {/* A full disk is an operational problem, so the
                                    thresholds escalate rather than just reporting. */}
                                <span
                                  className={`badge plain ${
                                    usedPct >= 90 ? 'crit' : usedPct >= 75 ? 'warn' : 'ok'
                                  }`}
                                >
                                  {usedPct}%
                                </span>
                              </td>
                            </tr>
                          )
                        })}
                      </tbody>
                    </table>
                  </div>
                )}
              </div>
            </>
          )}
        </>
      )}

      {tab === 'users' && <DeviceUsersPanel deviceId={deviceId!} deviceName={device.hostname} />}

      {tab === 'groups' && <DeviceGroupsPanel deviceId={deviceId!} />}

      {tab === 'software' && (
        <>
          {!device.software.length && <InventoryEmpty title="No software inventory yet" />}
          {!!device.software.length && (
            <div className="card">
              <h2>Installed applications</h2>
              <div className="scroll-y table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Application</th>
                      <th>Version</th>
                      <th>Publisher</th>
                      <th>Arch</th>
                    </tr>
                  </thead>
                  <tbody>
                    {device.software.map((sw) => (
                      <tr key={`${sw.name}|${sw.version}`}>
                        <td>{sw.name}</td>
                        <td>{sw.version ?? '—'}</td>
                        <td>{sw.publisher ?? '—'}</td>
                        <td>{sw.architecture ?? '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}

      {tab === 'security' && (
        <>
          {!device.securityPosture && (
            <InventoryEmpty
              title="No security posture yet"
              detail="Use “Refresh inventory” and wait for the agent’s next heartbeat. Some checks (BitLocker, TPM) require the agent to run elevated."
            />
          )}
          {device.securityPosture &&
            (() => {
              const p = device.securityPosture
              // "Unknown" is kept visually distinct from "Fail". An unelevated
              // agent that cannot read TPM state has not found a problem, and
              // showing it as a failure would send someone chasing nothing.
              const check = (label: string, v: boolean | null, extra?: string) => (
                <>
                  <dt>{label}</dt>
                  <dd>
                    {v == null ? (
                      <span className="badge neutral">Unknown — agent may need elevation</span>
                    ) : v ? (
                      <span className="badge ok">Pass</span>
                    ) : (
                      <span className="badge crit">Fail</span>
                    )}
                    {extra ? <span className="muted" style={{ marginLeft: 8, fontSize: 12.5 }}>{extra}</span> : null}
                  </dd>
                </>
              )
              return (
                <div className="card">
                  <div className="card-header">
                    <h2>Compliance</h2>
                    {p.complianceScore == null ? (
                      <span className="badge neutral">Score unknown</span>
                    ) : (
                      <span
                        className={`badge ${
                          p.complianceScore >= 80 ? 'ok' : p.complianceScore >= 50 ? 'warn' : 'crit'
                        }`}
                        style={{ fontSize: 13 }}
                      >
                        {p.complianceScore}% compliant
                      </span>
                    )}
                  </div>
                  <dl className="kv">
                    {check('Microsoft Defender antivirus', p.defenderAntivirusEnabled)}
                    {check('Defender real-time protection', p.defenderRealtimeProtectionEnabled)}
                    {check(
                      'Defender signatures fresh (≤7d)',
                      p.defenderSignatureAgeDays == null ? null : p.defenderSignatureAgeDays <= 7,
                      p.defenderSignatureAgeDays == null ? undefined : `${p.defenderSignatureAgeDays}d old`,
                    )}
                    {check('Firewall (Domain)', p.firewallDomainEnabled)}
                    {check('Firewall (Private)', p.firewallPrivateEnabled)}
                    {check('Firewall (Public)', p.firewallPublicEnabled)}
                    {check('Secure Boot', p.secureBootEnabled)}
                    {check('TPM enabled', p.tpmEnabled, p.tpmSpecVersion ? `v${p.tpmSpecVersion}` : undefined)}
                    {check(
                      'BitLocker (system drive)',
                      p.bitLockerSystemDriveStatus == null
                        ? null
                        : p.bitLockerSystemDriveStatus === 'On',
                      p.bitLockerSystemDriveStatus ?? undefined,
                    )}
                    <dt>Local administrators</dt>
                    <dd>{p.localAdministratorCount ?? '—'}</dd>
                  </dl>
                </div>
              )
            })()}
        </>
      )}

      {tab === 'updates' && (
        <>
          {!device.windowsUpdate && (
            <InventoryEmpty
              title="No update data yet"
              detail="Refresh inventory and wait for the agent’s next heartbeat. History is read from the local Windows Update store."
            />
          )}
          {device.windowsUpdate &&
            (() => {
              const u = device.windowsUpdate
              return (
                <div className="card">
                  <div className="detail-facts" style={{ marginTop: 0, paddingTop: 0, borderTop: 'none' }}>
                    <span className="fact">
                      <span className="fact-label">Reboot pending</span>
                      {u.rebootRequired ? (
                        <span className="badge warn">Yes</span>
                      ) : (
                        <span className="badge ok">No</span>
                      )}
                    </span>
                    <span className="fact">
                      <span className="fact-label">Failed updates</span>
                      <span className={`badge plain ${u.failedUpdateCount > 0 ? 'crit' : 'neutral'}`}>
                        {u.failedUpdateCount}
                      </span>
                    </span>
                    <span className="fact">
                      <span className="fact-label">Reported</span>
                      <span className="fact-value">{new Date(u.collectedAt).toLocaleString()}</span>
                    </span>
                  </div>
                  <hr className="divider" />
                  {u.history.length === 0 ? (
                    <div className="empty-state">
                      <div className="title">No update history recorded</div>
                    </div>
                  ) : (
                    <div className="scroll-y table-wrap">
                      <table className="table">
                        <thead>
                          <tr>
                            <th>Update</th>
                            <th>Operation</th>
                            <th>Result</th>
                            <th>Date</th>
                          </tr>
                        </thead>
                        <tbody>
                          {u.history.map((h, i) => (
                            <tr key={i}>
                              <td>{h.title}</td>
                              <td>{h.operation}</td>
                              <td>
                                {h.result === 'Succeeded' ? (
                                  <span className="badge ok">Succeeded</span>
                                ) : h.result === 'Failed' || h.result === 'Aborted' ? (
                                  <span className="badge crit">{h.result}</span>
                                ) : (
                                  <span className="badge neutral">{h.result}</span>
                                )}
                              </td>
                              <td className="muted">
                                {h.date ? new Date(h.date).toLocaleString() : '—'}
                              </td>
                            </tr>
                          ))}
                        </tbody>
                      </table>
                    </div>
                  )}
                </div>
              )
            })()}
        </>
      )}

      {tab === 'services' && (
        <>
          {!device.services.length && (
            <InventoryEmpty
              title="No service inventory yet"
              detail="Refresh inventory and wait for the next heartbeat."
            />
          )}
          {!!device.services.length && (
            <div className="card">
              <h2>Windows services</h2>
              <div className="scroll-y table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Service</th>
                      <th>Status</th>
                      <th>Start mode</th>
                    </tr>
                  </thead>
                  <tbody>
                    {device.services.map((sv) => (
                      <tr key={sv.name}>
                        <td>
                          <div>{sv.displayName}</div>
                          <div className="row-sub mono-sub">{sv.name}</div>
                        </td>
                        <td>
                          {sv.status === 'Running' ? (
                            <span className="badge ok">Running</span>
                          ) : (
                            <span className="badge neutral">{sv.status}</span>
                          )}
                        </td>
                        <td>{sv.startMode}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}

      {tab === 'processes' && (
        <>
          {!device.processes.length && (
            <InventoryEmpty
              title="No process snapshot yet"
              detail="Refresh inventory and wait for the next heartbeat."
            />
          )}
          {!!device.processes.length && (
            <div className="card">
              <div className="card-header">
                <h2>Running processes</h2>
                <span className="muted" style={{ fontSize: 12 }}>
                  Snapshot (top by memory) as of{' '}
                  {device.processes[0]
                    ? new Date(device.processes[0].collectedAt).toLocaleString()
                    : ''}
                </span>
              </div>
              <div className="scroll-y table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Process</th>
                      <th>PID</th>
                      <th>Memory</th>
                      <th>Path</th>
                    </tr>
                  </thead>
                  <tbody>
                    {device.processes.map((pr) => (
                      <tr key={pr.processId}>
                        <td>{pr.name}</td>
                        <td>{pr.processId}</td>
                        <td>{formatBytes(pr.workingSetBytes)}</td>
                        <td className="muted" style={{ maxWidth: 380 }}>
                          <span className="truncate" title={pr.executablePath ?? undefined}>
                            {pr.executablePath ?? '—'}
                          </span>
                        </td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}

      {tab === 'actions' && (
        <div className="card">
          <h2>Device actions</h2>
          <p className="muted" style={{ fontSize: 13, marginTop: 0, maxWidth: 620 }}>
            Each action is delivered as a typed task the device pulls on its next check-in. Actions
            you lack permission for are hidden; the server enforces this regardless.
          </p>

          {visibleSession.length === 0 && visiblePower.length === 0 && !hasPermission('device.retire') && (
            <div className="empty-state">
              <Icon name="key" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">No actions available</div>
              <div>Your role grants no device actions on this machine.</div>
            </div>
          )}

          {visibleSession.length > 0 && (
            <div className="action-group">
              <h3>Session</h3>
              <p className="group-note">
                Affects whoever is signed in right now. Locking is immediate and harmless; signing a
                user out closes their applications and can lose unsaved work.
              </p>
              <div className="btn-row">{visibleSession.map(actionButton)}</div>
            </div>
          )}

          {visiblePower.length > 0 && (
            <div className="action-group">
              <h3>Power</h3>
              <p className="group-note">
                Interrupts the machine. A shut-down device cannot be restarted remotely — someone
                has to be physically present to power it back on.
              </p>
              <div className="btn-row">{visiblePower.map(actionButton)}</div>
            </div>
          )}

          {hasPermission('device.retire') && (
            <div className="action-group destructive">
              <h3>Lifecycle</h3>
              <p className="group-note">
                Offboarding revokes the device's credentials and retires it — reversible via
                reactivation, which requires the machine to re-enroll. It does not wipe the machine.
              </p>
              {device.status === 'Retired' ? (
                <button type="button" onClick={() => void onReactivate()}>
                  Reactivate device
                </button>
              ) : (
                <button type="button" className="btn-danger" onClick={() => void onOffboard()}>
                  <Icon name="trash" size={14} />
                  Offboard device
                </button>
              )}
            </div>
          )}
        </div>
      )}

      {tab === 'tasks' && (
        <div className="card">
          <h2>Queued and completed tasks</h2>
          {!tasks.length && (
            <div className="empty-state">
              <Icon name="tasks" size={40} strokeWidth={1.25} className="icon" />
              <div className="title">No tasks yet</div>
              <div>Queue an action from the Actions tab to see it here.</div>
            </div>
          )}
          {!!tasks.length && (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Task</th>
                    <th>Status</th>
                    <th>Queued by</th>
                    <th>Queued</th>
                    <th>Result</th>
                  </tr>
                </thead>
                <tbody>
                  {tasks.map((t) => (
                    <tr key={t.id}>
                      <td>{t.type}</td>
                      <td>
                        {/* Anything still in flight is amber, not green: a queued
                            task has not happened on the machine yet. */}
                        <span
                          className={`badge ${
                            t.status === 'Succeeded'
                              ? 'ok'
                              : t.status === 'Failed' || t.status === 'Expired'
                                ? 'crit'
                                : t.status === 'Cancelled'
                                  ? 'neutral'
                                  : 'warn'
                          }`}
                        >
                          {t.status}
                        </span>
                      </td>
                      <td>{t.createdByDisplay}</td>
                      <td>{new Date(t.createdAt).toLocaleString()}</td>
                      <td className="muted">{t.resultMessage ?? '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </div>
      )}

      {tab === 'network' && (
        <>
          {!device.networkInterfaces.length && <InventoryEmpty title="No network inventory yet" />}
          {!!device.networkInterfaces.length && (
            <div className="card">
              <h2>Network interfaces</h2>
              <div className="scroll-y table-wrap">
                <table className="table">
                  <thead>
                    <tr>
                      <th>Interface</th>
                      <th>Status</th>
                      <th>MAC address</th>
                      <th>IP addresses</th>
                    </tr>
                  </thead>
                  <tbody>
                    {device.networkInterfaces.map((nic) => (
                      <tr key={nic.name}>
                        <td>{nic.name}</td>
                        <td>
                          {nic.isUp ? (
                            <span className="badge ok">Up</span>
                          ) : (
                            <span className="badge neutral">Down</span>
                          )}
                        </td>
                        <td>
                          <code>{nic.macAddress ?? '—'}</code>
                        </td>
                        <td>{nic.ipAddresses?.length ? nic.ipAddresses.join(', ') : '—'}</td>
                      </tr>
                    ))}
                  </tbody>
                </table>
              </div>
            </div>
          )}
        </>
      )}
    </>
  )
}
