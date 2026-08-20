import { useCallback, useEffect, useState } from 'react'
import {
  addLocalGroupMember,
  getDeviceTasks,
  getLocalGroups,
  getLocalUsers,
  removeLocalGroupMember,
  type LocalGroupRow,
  type LocalUserRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'

/**
 * Device -> Groups. Shows real Windows local groups and their actual membership,
 * and lets an authorized operator add or remove members through the typed-task
 * pipeline. Membership shown is what the endpoint last reported, refreshed after a
 * change completes rather than assumed.
 */
export function DeviceGroupsPanel({ deviceId }: { deviceId: string }) {
  const { hasPermission } = useAuth()
  const canManage = hasPermission('group.manage')
  const [groups, setGroups] = useState<LocalGroupRow[]>([])
  const [users, setUsers] = useState<LocalUserRow[]>([])
  const [selected, setSelected] = useState<LocalGroupRow | null>(null)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [pending, setPending] = useState<{ taskId: string; label: string } | null>(null)

  const load = useCallback(async () => {
    try {
      const [g, u] = await Promise.all([getLocalGroups(deviceId), getLocalUsers(deviceId).catch(() => [])])
      setGroups(g)
      setUsers(u)
      setError(null)
      // Keep the open group in sync with freshly reported membership.
      setSelected((current) => (current ? g.find((x) => x.sid === current.sid) ?? null : null))
    } catch {
      setError('Could not load local groups for this device.')
    }
  }, [deviceId])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (!pending) return
    const timer = setInterval(async () => {
      try {
        const tasks = await getDeviceTasks(deviceId)
        const task = tasks.find((t) => t.id === pending.taskId)
        if (!task) return
        if (['Succeeded', 'Failed', 'Expired', 'Cancelled'].includes(task.status)) {
          clearInterval(timer)
          setPending(null)
          setNotice(`${pending.label}: ${task.status.toLowerCase()}${task.resultMessage ? ` — ${task.resultMessage}` : ''}.`)
          await load()
        }
      } catch {
        // Transient failure; keep polling until unmount.
      }
    }, 3000)
    return () => clearInterval(timer)
  }, [pending, deviceId, load])

  async function run(label: string, action: () => Promise<{ taskId: string }>) {
    setError(null)
    setNotice(null)
    try {
      const { taskId } = await action()
      setPending({ taskId, label })
      setNotice(`${label}: queued.`)
    } catch (e) {
      setError(e instanceof Error ? e.message : `${label} failed.`)
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
      {notice && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span>{notice}</span>
        </div>
      )}
      {pending && <div className="loading">Waiting for the device to report the result…</div>}

      {groups.length === 0 && (
        <div className="empty-state">
          <Icon name="groups" size={40} strokeWidth={1.25} className="icon" />
          <div className="title">No local group inventory yet</div>
          <div>Refresh inventory and wait for the agent’s next heartbeat.</div>
        </div>
      )}

      {groups.length > 0 && !selected && (
        <>
          <h2>Local Windows groups</h2>
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Group</th>
                  <th>Members</th>
                  <th>Membership</th>
                  <th style={{ textAlign: 'right' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {groups.map((g) => (
                  <tr key={g.sid}>
                    <td>
                      <div>
                        {g.name}{' '}
                        {/* Administrators is called out on sight: adding a member
                            here grants full control of the machine. */}
                        {g.isAdministrators && <span className="badge warn">high impact</span>}
                      </div>
                      <div className="row-sub">{g.description ?? '—'}</div>
                    </td>
                    <td>{g.memberCount}</td>
                    <td className="muted" style={{ maxWidth: 320 }}>
                      <span className="truncate">
                        {(g.members ?? []).map((m) => m.name).join(', ') || '—'}
                      </span>
                    </td>
                    <td style={{ textAlign: 'right' }}>
                      <button type="button" className="btn-sm" onClick={() => setSelected(g)}>
                        Open
                      </button>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        </>
      )}

      {selected && (
        <>
          <div className="card-header">
            <div>
              <h2>
                {selected.name}{' '}
                {selected.isAdministrators && <span className="badge warn">high impact</span>}
              </h2>
              <div className="row-sub mono-sub">{selected.sid}</div>
            </div>
            <div className="btn-row">
              {canManage && (
                <button
                  type="button"
                  className="btn-sm"
                  onClick={() => {
                    const candidates = users.filter(
                      (u) => !(selected.members ?? []).some((m) => m.sid === u.sid),
                    )
                    if (candidates.length === 0) {
                      setError('Every known local user is already a member of this group.')
                      return
                    }
                    const name = window.prompt(
                      `Add which user to "${selected.name}"?\n\n${candidates.map((u) => u.name).join(', ')}`,
                    )
                    if (!name) return
                    const user = candidates.find((u) => u.name.toLowerCase() === name.toLowerCase())
                    if (!user) {
                      setError(`No local user named "${name}".`)
                      return
                    }
                    void run(`Add ${user.name} to ${selected.name}`, () =>
                      addLocalGroupMember(deviceId, selected.sid, user.sid),
                    )
                  }}
                >
                  <Icon name="plus" size={14} />
                  Add user
                </button>
              )}
              <button type="button" className="btn-ghost btn-sm" onClick={() => setSelected(null)}>
                Back
              </button>
            </div>
          </div>

          {(selected.members ?? []).length === 0 && (
            <div className="empty-state">
              <div className="title">This group has no members</div>
            </div>
          )}

          {(selected.members ?? []).length > 0 && (
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Member</th>
                    <th>Type</th>
                    <th>SID</th>
                    {canManage && <th style={{ textAlign: 'right' }}>Actions</th>}
                  </tr>
                </thead>
                <tbody>
                  {(selected.members ?? []).map((m) => (
                    <tr key={m.sid ?? m.name}>
                      <td>{m.name}</td>
                      <td>{m.memberType}</td>
                      <td className="muted" style={{ maxWidth: 240 }}>
                        <span className="truncate mono-sub" title={m.sid ?? undefined}>
                          {m.sid ?? '—'}
                        </span>
                      </td>
                      {canManage && (
                        <td style={{ textAlign: 'right' }}>
                          {m.sid && (
                            <button
                              type="button"
                              className="btn-danger btn-sm"
                              onClick={() => {
                                if (
                                  !window.confirm(
                                    `Remove "${m.name}" from "${selected.name}"?` +
                                      (selected.isAdministrators
                                        ? '\n\nThis removes their local administrator rights on this device.'
                                        : ''),
                                  )
                                )
                                  return
                                void run(`Remove ${m.name} from ${selected.name}`, () =>
                                  removeLocalGroupMember(deviceId, selected.sid, m.sid!),
                                )
                              }}
                            >
                              Remove
                            </button>
                          )}
                        </td>
                      )}
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
          )}
        </>
      )}
    </div>
  )
}
