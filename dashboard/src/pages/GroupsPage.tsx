import { useCallback, useEffect, useState } from 'react'
import {
  addGroupMember,
  createGroup,
  getDevices,
  getGroupMembers,
  getGroups,
  type GroupMember,
  type GroupRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'

export function GroupsPage() {
  const { hasPermission } = useAuth()
  const [groups, setGroups] = useState<GroupRow[]>([])
  const [selected, setSelected] = useState<GroupRow | null>(null)
  const [members, setMembers] = useState<GroupMember[]>([])
  const [error, setError] = useState<string | null>(null)
  const [name, setName] = useState('')
  const [creating, setCreating] = useState(false)

  const load = useCallback(async () => {
    try {
      setGroups(await getGroups())
      setError(null)
    } catch {
      setError('Could not load groups.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    if (selected) {
      getGroupMembers(selected.id).then(setMembers).catch(() => setMembers([]))
    }
  }, [selected])

  async function onCreate() {
    try {
      await createGroup(name.trim(), `${name.trim()} devices`)
      setName('')
      setCreating(false)
      await load()
    } catch {
      setError('Could not create the group (name may already exist).')
    }
  }

  async function onAddMember() {
    if (!selected) return
    const hostname = window.prompt('Device hostname to add (exact match):')
    if (!hostname) return
    try {
      const page = await getDevices(1, 50, hostname)
      const device = page.items.find((d) => d.hostname.toLowerCase() === hostname.toLowerCase())
      if (!device) {
        setError(`No device named "${hostname}".`)
        return
      }
      await addGroupMember(selected.id, device.id)
      setMembers(await getGroupMembers(selected.id))
      await load()
    } catch {
      setError('Could not add the member.')
    }
  }

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div style={{ display: 'flex', justifyContent: 'flex-end', marginBottom: 16 }}>
        {hasPermission('group.manage') && (
          <button type="button" onClick={() => setCreating(!creating)}>
            {creating ? 'Cancel' : 'New group'}
          </button>
        )}
      </div>

      {creating && (
        <div className="card card-section">
          <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end' }}>
            <label style={{ fontSize: 13, fontWeight: 600 }}>
              Group name
              <input
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Finance"
                style={{ display: 'block', marginTop: 4, padding: '7px 10px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit', width: 240 }}
              />
            </label>
            <button type="button" disabled={!name.trim()} onClick={() => void onCreate()}
              style={{ background: 'var(--color-primary)', color: '#fff', border: 'none', borderRadius: 6, padding: '8px 16px', fontWeight: 600, cursor: 'pointer' }}>
              Create
            </button>
          </div>
        </div>
      )}

      <div style={{ display: 'flex', gap: 16 }}>
        <div className="card" style={{ flex: '0 0 320px' }}>
          <h2>Groups</h2>
          {groups.length === 0 && <div className="empty-state"><div className="title">No groups yet</div></div>}
          {groups.map((g) => (
            <div key={g.id} onClick={() => setSelected(g)}
              style={{ padding: '10px 12px', borderRadius: 6, cursor: 'pointer', marginBottom: 4,
                background: selected?.id === g.id ? 'var(--color-neutral-bg)' : 'transparent' }}>
              <div style={{ fontWeight: 600 }}>{g.name}</div>
              <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>
                {g.memberCount} device{g.memberCount === 1 ? '' : 's'} · {g.type}
              </div>
            </div>
          ))}
        </div>

        <div className="card" style={{ flex: 1 }}>
          {!selected && (
            <div className="empty-state">
              <div className="title">Select a group</div>
              <div>Choose a group to view and manage its members.</div>
            </div>
          )}
          {selected && (
            <>
              <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
                <h2 style={{ margin: 0 }}>{selected.name} — members</h2>
                {hasPermission('group.manage') && <button type="button" onClick={() => void onAddMember()}>Add member</button>}
              </div>
              {members.length === 0 && <div className="loading">No members yet.</div>}
              {members.length > 0 && (
                <table className="table">
                  <thead><tr><th>Device</th><th>Status</th></tr></thead>
                  <tbody>
                    {members.map((m) => (
                      <tr key={m.id}><td style={{ fontWeight: 600 }}>{m.hostname}</td><td>{m.status}</td></tr>
                    ))}
                  </tbody>
                </table>
              )}
            </>
          )}
        </div>
      </div>
    </>
  )
}
