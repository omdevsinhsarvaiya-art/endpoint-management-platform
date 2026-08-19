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
      {error && <div className="error-banner">{error}</div>}
      {notice && (
        <div className="error-banner" style={{ background: '#ecfdf5', borderColor: '#a7f3d0', color: '#065f46' }}>
          {notice}
        </div>
      )}
      {pending && <div className="loading">Waiting for the device to report the result…</div>}

      {groups.length === 0 && (
        <div className="empty-state">
          <div className="title">No local group inventory yet</div>
          <div>Refresh inventory and wait for the agent's next heartbeat.</div>
        </div>
      )}

      {groups.length > 0 && !selected && (
        <table className="table">
          <thead><tr><th>Group</th><th>Members</th><th>Membership</th><th></th></tr></thead>
          <tbody>
            {groups.map((g) => (
              <tr key={g.sid}>
                <td>
                  <div style={{ fontWeight: 600 }}>
                    {g.name}{' '}
                    {g.isAdministrators && <span className="badge warn">high impact</span>}
                  </div>
                  <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{g.description ?? '—'}</div>
                </td>
                <td>{g.memberCount}</td>
                <td style={{ fontSize: 12, color: 'var(--color-text-muted)' }}>
                  {(g.members ?? []).map((m) => m.name).join(', ') || '—'}
                </td>
                <td><button type="button" onClick={() => setSelected(g)}>Open</button></td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {selected && (
        <>
          <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
            <div>
              <h2 style={{ margin: 0 }}>
                {selected.name} {selected.isAdministrators && <span className="badge warn">high impact</span>}
              </h2>
              <div style={{ color: 'var(--color-text-muted)', fontSize: 12, fontFamily: 'monospace' }}>{selected.sid}</div>
            </div>
            <div style={{ display: 'flex', gap: 8 }}>
              {canManage && (
                <button type="button" onClick={() => {
                  const candidates = users.filter(
                    (u) => !(selected.members ?? []).some((m) => m.sid === u.sid))
                  if (candidates.length === 0) {
                    setError('Every known local user is already a member of this group.')
                    return
                  }
                  const name = window.prompt(
                    `Add which user to "${selected.name}"?\n\n${candidates.map((u) => u.name).join(', ')}`)
                  if (!name) return
                  const user = candidates.find((u) => u.name.toLowerCase() === name.toLowerCase())
                  if (!user) {
                    setError(`No local user named "${name}".`)
                    return
                  }
                  void run(`Add ${user.name} to ${selected.name}`,
                    () => addLocalGroupMember(deviceId, selected.sid, user.sid))
                }}>
                  Add user
                </button>
              )}
              <button type="button" onClick={() => setSelected(null)}>Back</button>
            </div>
          </div>

          {(selected.members ?? []).length === 0 && <div className="loading">This group has no members.</div>}

          {(selected.members ?? []).length > 0 && (
            <table className="table">
              <thead><tr><th>Member</th><th>Type</th><th>SID</th>{canManage && <th></th>}</tr></thead>
              <tbody>
                {(selected.members ?? []).map((m) => (
                  <tr key={m.sid ?? m.name}>
                    <td style={{ fontWeight: 600 }}>{m.name}</td>
                    <td>{m.memberType}</td>
                    <td style={{ fontFamily: 'monospace', fontSize: 11, color: 'var(--color-text-muted)' }}>{m.sid ?? '—'}</td>
                    {canManage && (
                      <td>
                        {m.sid && (
                          <button type="button"
                            onClick={() => {
                              if (!window.confirm(
                                `Remove "${m.name}" from "${selected.name}"?`
                                + (selected.isAdministrators
                                  ? '\n\nThis removes their local administrator rights on this device.'
                                  : ''))) return
                              void run(`Remove ${m.name} from ${selected.name}`,
                                () => removeLocalGroupMember(deviceId, selected.sid, m.sid!))
                            }}
                            style={{ border: '1px solid #fca5a5', color: 'var(--color-crit)' }}>
                            Remove
                          </button>
                        )}
                      </td>
                    )}
                  </tr>
                ))}
              </tbody>
            </table>
          )}
        </>
      )}
    </div>
  )
}
