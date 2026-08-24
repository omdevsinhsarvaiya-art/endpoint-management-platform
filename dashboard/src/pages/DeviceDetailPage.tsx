import { useCallback, useEffect, useState } from 'react'
import { Link, useParams, useSearchParams } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { DeviceUsersPanel } from './DeviceUsersPanel'
import { DeviceGroupsPanel } from './DeviceGroupsPanel'
import { DeviceServicesPanel } from './DeviceServicesPanel'
import { DeviceProcessesPanel } from './DeviceProcessesPanel'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'
import {
  ApiError,
  cancelDeviceTask,
  getLatestAgentRelease,
  updateDeviceAgent,
  type LatestAgentRelease,
  getDevice,
  getDeviceTasks,
  deviceName,
  offboardDevice,
  queueDeviceAction,
  reactivateDevice,
  requestInventoryRefresh,
  type DeviceDetail,
  type DeviceTaskItem,
} from '../api/client'

import { Icon, type IconName } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { EditDeviceNameDialog } from './EditDeviceNameDialog'

/** One navigation card in the device feature grid. */
interface FeatureSpec {
  key: Tab
  label: string
  icon: IconName
  desc: string
  /** Omitted where the device has not reported anything to count. */
  count?: number
}

type Tab = 'overview' | 'hardware' | 'network' | 'users' | 'groups' | 'software' | 'security' | 'updates' | 'services' | 'processes' | 'actions' | 'tasks'

const MODULES: Tab[] = ['overview', 'hardware', 'network', 'users', 'groups', 'software',
  'security', 'updates', 'services', 'processes', 'actions', 'tasks']

function isModule(value: string | null): value is Tab {
  return value !== null && (MODULES as string[]).includes(value)
}

type ActionKey = 'restart' | 'shutdown' | 'lock' | 'signout'

const ACTION_LABELS: Record<ActionKey, string> = {
  restart: 'Restart device',
  shutdown: 'Shut down device',
  lock: 'Lock screen',
  signout: 'Sign out user',
}

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

/** "20 seconds ago". Falls back to an absolute date once it stops being useful. */
function relativeSince(value: string | null): string {
  if (!value) return 'never'
  const seconds = Math.floor((Date.now() - new Date(value).getTime()) / 1000)
  if (seconds < 5) return 'just now'
  if (seconds < 60) return `${seconds} seconds ago`
  if (seconds < 3600) return `${Math.floor(seconds / 60)} minutes ago`
  if (seconds < 86400) return `${Math.floor(seconds / 3600)} hours ago`
  return new Date(value).toLocaleDateString()
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
  const [editingName, setEditingName] = useState(false)
  const [latestAgent, setLatestAgent] = useState<LatestAgentRelease | null>(null)

  // The published agent release, for the "update agent" affordance. Fetched
  // once per page visit: releases change rarely, and the compare is cheap.
  useEffect(() => {
    getLatestAgentRelease().then(setLatestAgent).catch(() => setLatestAgent(null))
  }, [])

  // The open module lives in the query string rather than component state, so
  // Back returns to the feature grid instead of leaving the device entirely,
  // and a link to a specific module can be shared. No module in the URL means
  // the grid — which is the landing view, not a tab that happens to be first.
  const [searchParams, setSearchParams] = useSearchParams()
  const moduleParam = searchParams.get('m')
  const tab: Tab | null = isModule(moduleParam) ? moduleParam : null

  const openModule = useCallback(
    (next: Tab) => setSearchParams({ m: next }),
    [setSearchParams],
  )
  const closeModule = useCallback(() => setSearchParams({}), [setSearchParams])

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

  // Tracks queued actions to their terminal state, so the page reports what the
  // machine actually did rather than that the server accepted a request.
  const { tracked, track, dismiss } = useTaskTracker(
    deviceId ?? '',
    device ? !device.isOnline : false,
    load,
  )

  async function runAction(action: 'restart' | 'shutdown' | 'lock' | 'signout') {
    if (!deviceId) return
    setConfirm(null)
    setActionMsg(null)
    try {
      const { taskId } = await queueDeviceAction(deviceId, action)
      track(taskId, ACTION_LABELS[action])
      await load()
    } catch {
      setActionMsg(`Could not queue "${action}".`)
    }
  }

  async function onCancelTask(taskId: string) {
    if (!deviceId) return
    setActionMsg(null)
    try {
      await cancelDeviceTask(deviceId, taskId)
      setActionMsg('Task cancelled.')
    } catch (e) {
      setActionMsg(
        e instanceof ApiError && e.status === 409
          ? 'Too late to cancel — the task was already delivered to the agent or has finished.'
          : e instanceof ApiError && e.status === 403
            ? 'You do not have permission to cancel this type of task.'
            : 'The task could not be cancelled.',
      )
    } finally {
      await load()
    }
  }

  const [confirmRemoval, setConfirmRemoval] = useState(false)

  async function onRemoveDevice() {
    setConfirmRemoval(false)
    if (!deviceId) return
    try {
      await offboardDevice(deviceId)
      setActionMsg(
        'Device removed from active management: credentials revoked, device retired. '
        + 'Its record and history remain under the Retired view.',
      )
      await load()
    } catch {
      setActionMsg('Could not remove the device.')
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

  // Counts come from the loaded inventory only. A module whose count the device
  // has not reported shows a description instead of a fabricated zero — "0
  // services" would be a claim about the machine, not an absence of data.
  const features: FeatureSpec[] = [
    { key: 'overview', label: 'Overview', icon: 'devices', desc: 'device status' },
    { key: 'hardware', label: 'Hardware', icon: 'software', desc: 'hardware info' },
    { key: 'network', label: 'Network', icon: 'updates', desc: 'interfaces',
      count: device.networkInterfaces.length },
    { key: 'users', label: 'Users', icon: 'users', desc: 'local accounts',
      count: device.localUsers.length },
    { key: 'groups', label: 'Groups', icon: 'groups', desc: 'local groups',
      count: device.localGroups.length },
    { key: 'software', label: 'Software', icon: 'software', desc: 'installed SW',
      count: device.software.length },
    { key: 'security', label: 'Security', icon: 'security', desc: 'posture' },
    { key: 'updates', label: 'Updates', icon: 'updates', desc: 'Windows Update',
      count: device.windowsUpdate?.history.length },
    { key: 'services', label: 'Services', icon: 'settings', desc: 'services',
      count: device.services.length },
    { key: 'processes', label: 'Processes', icon: 'tasks', desc: 'running procs',
      count: device.processes.length },
    { key: 'actions', label: 'Actions', icon: 'key', desc: 'device actions' },
    { key: 'tasks', label: 'Tasks', icon: 'audit', desc: 'task history', count: tasks.length },
  ]

  const openFeature = tab ? features.find((f) => f.key === tab) : undefined

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

  const canRename = hasPermission('device.rename')
  const canDeploy = hasPermission('software.deploy')

  // Strictly newer, numerically — mirrors the server's rule, which re-checks
  // anyway; hiding the button just avoids offering a guaranteed 409.
  const agentOutdated = (() => {
    if (!latestAgent?.available || !latestAgent.version) return false
    const parse = (v: string) => v.split('.').map((n) => Number(n))
    const a = parse(latestAgent.version)
    const b = parse(device.agentVersion)
    if (a.length !== 3 || b.length !== 3 || a.some(Number.isNaN) || b.some(Number.isNaN)) return false
    for (let i = 0; i < 3; i++) {
      if (a[i] !== b[i]) return a[i] > b[i]
    }
    return false
  })()

  async function onUpdateAgent() {
    if (!deviceId || !latestAgent?.releaseId) return
    setActionMsg(null)
    try {
      const { taskId } = await updateDeviceAgent(deviceId, latestAgent.releaseId)
      track(taskId, `Update agent to ${latestAgent.version}`)
      await load()
    } catch (e) {
      setActionMsg(
        e instanceof ApiError && e.status === 409
          ? 'The update was refused — the release is not newer than the installed agent, or is no longer published.'
          : 'The agent update could not be queued.',
      )
    }
  }
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

      {editingName && (
        <EditDeviceNameDialog
          deviceId={device.id}
          hostname={device.hostname}
          currentDisplayName={device.displayName}
          onCancel={() => setEditingName(false)}
          onSaved={async () => {
            setEditingName(false)
            // Reload rather than patching local state: the server is what decides
            // whether a blank cleared the label, so its answer is the truth.
            await load()
          }}
        />
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
          <span>{deviceName(device)}</span>
        </div>
        <div className="detail-title">
          {/* The administrator's label is the headline. The real hostname sits
              in the facts strip below, so the machine is still identifiable. */}
          <h1>{deviceName(device)}</h1>
          {device.status === 'Retired' ? (
            <span className="badge neutral">Retired</span>
          ) : (
            <span className="badge ok">Active</span>
          )}
          <span className="spacer" />
          {canRename && (
            <button type="button" className="btn-sm" onClick={() => setEditingName(true)}>
              <Icon name="edit" size={14} />
              Edit Name
            </button>
          )}
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
          {/* Shown only when a label is set: without one the heading already
              is the hostname, and repeating it says nothing. */}
          {device.displayName && (
            <span className="fact">
              <span className="fact-label">Hostname</span>
              <span className="fact-value">{device.hostname}</span>
            </span>
          )}
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
            {/* Relative, because "is this machine reachable right now" is the
                question being asked. The exact timestamp is on the tooltip and
                in Overview for when it matters. */}
            <span className="fact-value" title={formatTimestamp(device.lastSeenAt)}>
              {relativeSince(device.lastSeenAt)}
            </span>
          </span>
        </div>
      </div>

      {/* The feature grid IS the device navigation. When a module is open the
          grid is replaced rather than kept above it, so the module gets the
          whole content area and there is exactly one way back. */}
      {!tab && (
        <>
          <h2 className="section-label">Device features</h2>
          <div className="feature-grid">
            {features.map((f) => (
              <button
                key={f.key}
                type="button"
                className="feature-card"
                onClick={() => openModule(f.key)}
              >
                <span className="feature-icon">
                  <Icon name={f.icon} size={17} />
                </span>
                <span className="feature-body">
                  <span className="feature-name">{f.label}</span>
                  {f.count !== undefined && <span className="feature-count">{f.count}</span>}
                  <span className="feature-desc">{f.desc}</span>
                </span>
              </button>
            ))}
          </div>
        </>
      )}

      {openFeature && (
        <div className="module-header">
          <button type="button" className="btn-ghost btn-sm" onClick={closeModule}>
            <Icon name="chevron-left" size={14} />
            Device Menu
          </button>
          <span className="back-divider" />
          <h2>{openFeature.label}</h2>
          {openFeature.count !== undefined && (
            <span className="badge neutral plain">{openFeature.count}</span>
          )}
        </div>
      )}

      {tab === 'overview' && (
        <div className="card">
          <h2>Identity</h2>
          <dl className="kv">
            {/* Both, always, and labelled — this is the one place where an
                administrator should be able to see exactly which name is the
                console's and which one is the machine's. */}
            <dt>Display name</dt>
            <dd>
              {device.displayName ?? <span className="muted">Not set — showing hostname</span>}
            </dd>
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
            <DeviceServicesPanel
              deviceId={device.id}
              services={device.services}
              offline={!device.isOnline}
              onInventoryChanged={load}
            />
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
            <DeviceProcessesPanel
              deviceId={device.id}
              processes={device.processes}
              offline={!device.isOnline}
              onInventoryChanged={load}
            />
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

          {/* An offline machine accepts nothing right now. Saying so up front
              beats letting someone queue a restart and wonder why nothing
              happened — the task waits, and that is exactly what this says. */}
          {!device.isOnline && device.status !== 'Retired' && (
            <div className="warn-banner">
              <strong>Device offline.</strong> Actions queued now will wait and run when the agent
              reconnects — last seen {relativeSince(device.lastSeenAt)}.
            </div>
          )}

          <TaskProgress tasks={tracked} onDismiss={dismiss} />

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

          {canDeploy && agentOutdated && (
            <div className="action-group">
              <h3>Agent</h3>
              <p className="group-note">
                This device runs agent {device.agentVersion}; release {latestAgent?.version} is
                published. The agent downloads the MSI itself, verifies its hash and signature, and
                restarts into the new version — identity, enrollment and credential are preserved.
              </p>
              <button type="button" className="btn-primary" onClick={() => void onUpdateAgent()}>
                Update agent to {latestAgent?.version}
              </button>
            </div>
          )}

          {hasPermission('device.retire') && (
            <div className="action-group destructive">
              <h3>Remove Device</h3>
              <p className="group-note">
                Removes this machine from active management: its credentials are revoked and it can
                no longer check in, receive tasks, or re-enroll. The device record and its full
                audit history are kept under the Retired view — nothing is wiped on the machine
                itself. Reversible via reactivation, after which the machine must re-enroll.
              </p>
              {device.status === 'Retired' ? (
                <button type="button" onClick={() => void onReactivate()}>
                  Reactivate device
                </button>
              ) : (
                <button type="button" className="btn-danger" onClick={() => setConfirmRemoval(true)}>
                  <Icon name="trash" size={14} />
                  Remove Device
                </button>
              )}
            </div>
          )}

          {confirmRemoval && (
            <ConfirmDialog
              title={`Remove ${deviceName(device)} from management?`}
              confirmLabel="Yes, remove device"
              onCancel={() => setConfirmRemoval(false)}
              onConfirm={() => void onRemoveDevice()}
            >
              <>
                <strong className="secondary">{deviceName(device)}</strong> will be removed from
                active management. Its credentials are revoked immediately, it stops appearing in
                the Active devices view, and it cannot check in or re-enroll unless an
                administrator reactivates it. The device record and audit history are preserved;
                nothing on the machine is wiped. This action is audited.
              </>
            </ConfirmDialog>
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
                    <th style={{ textAlign: 'right' }}></th>
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
                      <td style={{ textAlign: 'right' }}>
                        {/* Only while Queued: once delivered, the agent may be
                            mid-operation and the server refuses anyway. */}
                        {t.status === 'Queued' && (
                          <button
                            type="button"
                            className="btn-ghost btn-sm"
                            onClick={() => void onCancelTask(t.id)}
                          >
                            Cancel
                          </button>
                        )}
                      </td>
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
