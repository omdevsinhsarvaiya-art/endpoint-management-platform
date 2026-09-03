import { useEffect, useState } from 'react'
import { getDevices } from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { selectableTargets, type TargetCandidate } from './deploymentView'

const PAGE_SIZE = 100

/**
 * Pick devices by searching, not by typing a hostname from memory.
 *
 * Replaces a `window.prompt` that demanded an exact hostname match — unusable
 * against a few hundred machines, and wrong in principle: a hostname is display
 * text that repeats and changes, while the id is what the server authorizes
 * against. Selection returns DeviceIds.
 *
 * Search and paging are server-side. Pulling the fleet into the browser to
 * filter it here would get steadily worse as the estate grows, which is the
 * opposite of what this is for.
 */
export function DevicePickerDialog({
  title,
  confirmLabel,
  excludeDeviceIds = [],
  onCancel,
  onConfirm,
}: {
  title: string
  confirmLabel: string
  excludeDeviceIds?: string[]
  onCancel: () => void
  onConfirm: (deviceIds: string[]) => Promise<void>
}) {
  useDialogDismiss(onCancel)

  const [devices, setDevices] = useState<TargetCandidate[] | null>(null)
  const [total, setTotal] = useState(0)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<string[]>([])
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false

    // Retired devices are excluded at the query, not hidden afterwards: they
    // cannot receive a task, so offering one offers an action that does nothing.
    getDevices(1, PAGE_SIZE, search, 'Active')
      .then((page) => {
        if (cancelled) return
        const exclude = new Set(excludeDeviceIds)
        setDevices(selectableTargets(page.items.map((d) => ({
          id: d.id,
          hostname: d.hostname,
          displayName: d.displayName,
          status: d.status,
          agentVersion: d.agentVersion,
          lastSeenAt: d.lastSeenAt,
        }))).filter((d) => !exclude.has(d.id)))
        setTotal(page.totalCount)
      })
      .catch(() => {
        if (!cancelled) setError('Could not load devices.')
      })

    return () => {
      cancelled = true
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [search, excludeDeviceIds.join(',')])

  async function confirm() {
    setBusy(true)
    try {
      await onConfirm(selected)
    } catch {
      setError('Could not add the selected devices.')
      setBusy(false)
    }
  }

  return (
    <div className="overlay">
      <div className="dialog" role="dialog" aria-modal="true" aria-label={title}>
        <div className="dialog-header">
          <h2>{title}</h2>
          <div className="sub">Retired devices are not listed.</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          <div className="input-search">
            <Icon name="search" size={15} className="search-icon" />
            <input
              type="search"
              placeholder="Search devices…"
              aria-label="Search devices"
              value={search}
              onChange={(e) => setSearch(e.target.value)}
            />
          </div>

          {devices === null && <div className="loading">Loading devices…</div>}

          {devices !== null && devices.length === 0 && (
            <div className="empty-state">
              <div className="title">No devices match</div>
              <div>Try a different search.</div>
            </div>
          )}

          {devices !== null && devices.length > 0 && (
            <div className="table-wrap" style={{ maxHeight: 320, overflowY: 'auto' }}>
              <table className="table">
                <thead>
                  <tr>
                    <th style={{ width: 32 }}></th>
                    <th>Device</th>
                    <th>Agent</th>
                    <th>Last seen</th>
                  </tr>
                </thead>
                <tbody>
                  {devices.map((d) => (
                    <tr key={d.id}>
                      <td>
                        <input
                          type="checkbox"
                          aria-label={`Select ${d.hostname}`}
                          checked={selected.includes(d.id)}
                          onChange={() =>
                            setSelected(
                              selected.includes(d.id)
                                ? selected.filter((x) => x !== d.id)
                                : [...selected, d.id],
                            )
                          }
                        />
                      </td>
                      <td>{d.displayName ?? d.hostname}</td>
                      <td>{d.agentVersion}</td>
                      <td>{d.lastSeenAt ? new Date(d.lastSeenAt).toLocaleDateString() : '—'}</td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}

          {devices !== null && total > devices.length && (
            <p className="group-note">
              Showing {devices.length} of {total} active devices. Narrow the search to reach the rest.
            </p>
          )}
        </div>

        <div className="dialog-footer">
          <button type="button" className="btn-sm" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <button
            type="button"
            className="btn-primary"
            disabled={busy || selected.length === 0}
            onClick={() => void confirm()}
          >
            {busy ? 'Adding…' : `${confirmLabel} (${selected.length})`}
          </button>
        </div>
      </div>
    </div>
  )
}
