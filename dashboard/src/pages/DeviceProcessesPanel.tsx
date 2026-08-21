import { useState } from 'react'
import { terminateProcess, type DeviceProcessRow } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { TaskProgress } from '../components/TaskProgress'
import { useTaskTracker } from '../components/useTaskTracker'

/**
 * Device → Processes: the point-in-time snapshot plus termination.
 *
 * Termination sends the PID *and* the image name from this snapshot. PIDs are
 * recycled, so by the time the agent acts the number may belong to a different
 * program — the agent compares the image name first and refuses on mismatch,
 * which turns "killed the wrong process" from an accident into an impossibility.
 */
export function DeviceProcessesPanel({
  deviceId,
  processes,
  offline,
}: {
  deviceId: string
  processes: DeviceProcessRow[]
  offline: boolean
}) {
  const { hasPermission } = useAuth()
  const canTerminate = hasPermission('task.execute')

  const { tracked, track, dismiss } = useTaskTracker(deviceId, offline)
  const [error, setError] = useState<string | null>(null)
  const [confirm, setConfirm] = useState<DeviceProcessRow | null>(null)

  async function terminate(process: DeviceProcessRow) {
    setConfirm(null)
    setError(null)
    try {
      const { taskId } = await terminateProcess(deviceId, process.processId, process.name)
      track(taskId, `Terminate ${process.name} (PID ${process.processId})`)
    } catch {
      setError(`Could not queue termination of ${process.name}.`)
    }
  }

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
        <h2>Running processes</h2>
        <span className="muted" style={{ fontSize: 12 }}>
          Snapshot (top by memory) as of{' '}
          {processes[0] ? new Date(processes[0].collectedAt).toLocaleString() : '—'}
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
              {canTerminate && <th style={{ textAlign: 'right' }}>Actions</th>}
            </tr>
          </thead>
          <tbody>
            {processes.map((pr) => (
              <tr key={pr.processId}>
                <td>{pr.name}</td>
                <td>{pr.processId}</td>
                <td>{formatBytes(pr.workingSetBytes)}</td>
                <td className="muted" style={{ maxWidth: 340 }}>
                  <span className="truncate" title={pr.executablePath ?? undefined}>
                    {pr.executablePath ?? '—'}
                  </span>
                </td>
                {canTerminate && (
                  <td style={{ textAlign: 'right' }}>
                    <button type="button" className="btn-danger btn-sm" onClick={() => setConfirm(pr)}>
                      Terminate
                    </button>
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      </div>

      <p className="muted" style={{ fontSize: 12, marginTop: 10, marginBottom: 0 }}>
        The snapshot is not live. The agent verifies the process image before terminating, so a PID
        that has since been reused by another program is refused rather than killed.
      </p>

      {confirm && (
        <ConfirmDialog
          title={`Terminate ${confirm.name}?`}
          confirmLabel="Yes, terminate"
          onCancel={() => setConfirm(null)}
          onConfirm={() => void terminate(confirm)}
        >
          <>
            This forcibly ends <strong className="secondary">{confirm.name}</strong> (PID{' '}
            {confirm.processId}) on the device. Unsaved work in that program is lost. The agent
            refuses if the PID no longer belongs to{' '}
            <strong className="secondary">{confirm.name}</strong>. This action is audited.
          </>
        </ConfirmDialog>
      )}
    </div>
  )
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
