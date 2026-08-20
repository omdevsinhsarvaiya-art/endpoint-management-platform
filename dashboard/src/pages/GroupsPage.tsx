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
import { Icon } from '../components/Icon'

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

  const canManage = hasPermission('group.manage')

  return (
    <>
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Device groups are how policies, software and updates get targeted at a set of machines
          rather than one at a time.
        </div>
        {canManage && (
          <button
            type="button"
            className={creating ? undefined : 'btn-primary'}
            onClick={() => setCreating(!creating)}
          >
            {!creating && <Icon name="plus" size={14} />}
            {creating ? 'Cancel' : 'New group'}
          </button>
        )}
      </div>

      {creating && (
        <div className="card card-section">
          <h2>New group</h2>
          <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end' }}>
            <div className="field" style={{ marginBottom: 0, width: 260 }}>
              <label className="field-label" htmlFor="group-name">
                Group name
              </label>
              <input
                id="group-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Finance"
              />
            </div>
            <button
              type="button"
              className="btn-primary"
              disabled={!name.trim()}
              onClick={() => void onCreate()}
            >
              Create
            </button>
          </div>
        </div>
      )}

      <div className="split">
        <div className="card split-aside">
          <h2>Groups</h2>
          {groups.length === 0 && (
            <div className="empty-state">
              <Icon name="groups" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">No groups yet</div>
            </div>
          )}
          {groups.map((g) => (
            <button
              key={g.id}
              type="button"
              className="list-item"
              aria-selected={selected?.id === g.id}
              onClick={() => setSelected(g)}
            >
              <div className="list-item-title">{g.name}</div>
              <div className="list-item-sub">
                {g.memberCount} device{g.memberCount === 1 ? '' : 's'} · {g.type}
              </div>
            </button>
          ))}
        </div>

        <div className="card split-main">
          {!selected && (
            <div className="empty-state">
              <Icon name="chevron-right" size={36} strokeWidth={1.25} className="icon" />
              <div className="title">Select a group</div>
              <div>Choose a group to view and manage its members.</div>
            </div>
          )}
          {selected && (
            <>
              <div className="card-header">
                <h2>{selected.name} — members</h2>
                {canManage && (
                  <button type="button" className="btn-sm" onClick={() => void onAddMember()}>
                    <Icon name="plus" size={14} />
                    Add member
                  </button>
                )}
              </div>
              {members.length === 0 && (
                <div className="empty-state">
                  <div className="title">No members yet</div>
                  <div>Add a device to target this group with policies and software.</div>
                </div>
              )}
              {members.length > 0 && (
                <div className="table-wrap">
                  <table className="table">
                    <thead>
                      <tr>
                        <th>Device</th>
                        <th>Status</th>
                      </tr>
                    </thead>
                    <tbody>
                      {members.map((m) => (
                        <tr key={m.id}>
                          <td>{m.hostname}</td>
                          <td>
                            <span className={`badge ${m.status === 'Active' ? 'ok' : 'neutral'}`}>
                              {m.status}
                            </span>
                          </td>
                        </tr>
                      ))}
                    </tbody>
                  </table>
                </div>
              )}
            </>
          )}
        </div>
      </div>
    </>
  )
}
