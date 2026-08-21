import { useState } from 'react'
import { controlService, type DeviceServiceRow } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'

type ServiceAction = 'Start' | 'Stop' | 'Restart'

/**
 * Device → Services: the inventory table plus real service control.
 *
 * Each button queues a typed ControlService task; the agent acts through the
 * Windows service-control APIs and reports what actually happened, which is what
 * the progress banner shows. The table itself is the last reported inventory —
 * it does not flip to "Stopped" the moment a stop is queued, because the
 * machine has not said so yet.
 */
export function DeviceServicesPanel({
  deviceId,
  services,
  offline,
  onInventoryChanged,
}: {
  deviceId: string
  services: DeviceServiceRow[]
  offline: boolean
  /** Reloads the device data these rows are rendered from. */
  onInventoryChanged?: () => void | Promise<void>
}) {
  const { hasPermission } = useAuth()
  const canControl = hasPermission('task.execute')

  const { tracked, track, dismiss } = useTaskTracker(deviceId, offline, onInventoryChanged)
  const [error, setError] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<{ service: DeviceServiceRow; action: ServiceAction } | null>(null)
  const [search, setSearch] = useState('')

  async function run(service: DeviceServiceRow, action: ServiceAction) {
    setConfirm(null)
    setError(null)
    try {
      const { taskId } = await controlService(deviceId, service.name, action)
      // Service state is inventory data: on success the tracker requests a fresh
      // inventory and reloads this table before showing the green banner.
      track(taskId, `${action} "${service.displayName}"`, { syncInventory: true })
    } catch {
      setError(`Could not queue ${action.toLowerCase()} for "${service.displayName}".`)
    }
  }

  function request(service: DeviceServiceRow, action: ServiceAction) {
    // Starting a service is additive; stopping or restarting one interrupts
    // whatever depends on it, so those two confirm first.
    if (action === 'Start') {
      void run(service, action)
    } else {
      setConfirm({ service, action })
    }
  }

  const visible = services.filter(
    (s) =>
      !search ||
      s.displayName.toLowerCase().includes(search.toLowerCase()) ||
      s.name.toLowerCase().includes(search.toLowerCase()),
  )

  return (
    <div className="card">
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}
      <TaskProgress tasks={tracked} onDismiss={dismiss} />

      <div className="card-header">
        <h2>Windows services</h2>
        <div className="input-search" style={{ flexBasis: 240 }}>
          <Icon name="search" size={15} className="search-icon" />
          <input
            type="search"
            placeholder="Filter services…"
            aria-label="Filter services by name"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
      </div>

      {visible.length === 0 && (
        <div className="empty-state">
          <div className="title">{search ? 'No service matches this filter' : 'No services reported'}</div>
        </div>
      )}

      {visible.length > 0 && (
        <div className="scroll-y table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Service</th>
                <th>Status</th>
                <th>Start mode</th>
                {canControl && <th style={{ textAlign: 'right' }}>Actions</th>}
              </tr>
            </thead>
            <tbody>
              {visible.map((sv) => (
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
                  {canControl && (
                    <td style={{ textAlign: 'right' }}>
                      <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                        {sv.status !== 'Running' && (
                          <button type="button" className="btn-sm" onClick={() => request(sv, 'Start')}>
                            Start
                          </button>
                        )}
                        {sv.status === 'Running' && (
                          <>
                            <button type="button" className="btn-sm" onClick={() => request(sv, 'Restart')}>
                              Restart
                            </button>
                            <button
                              type="button"
                              className="btn-danger btn-sm"
                              onClick={() => request(sv, 'Stop')}
                            >
                              Stop
                            </button>
                          </>
                        )}
                      </div>
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}

      <p className="muted" style={{ fontSize: 12, marginTop: 10, marginBottom: 0 }}>
        Status shown is what the device last reported. After a change succeeds, the table refreshes
        itself as soon as the device uploads fresh inventory.
      </p>

      {confirm && (
        <ConfirmDialog
          title={`${confirm.action} "${confirm.service.displayName}"?`}
          confirmLabel={`Yes, ${confirm.action.toLowerCase()}`}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void run(confirm.service, confirm.action)}
        >
          <>
            This queues a service-control task for the device. Anything depending on{' '}
            <strong className="secondary">{confirm.service.name}</strong> will be affected while it
            is {confirm.action === 'Stop' ? 'stopped' : 'restarting'}. This action is audited.
          </>
        </ConfirmDialog>
      )}
    </div>
  )
}
