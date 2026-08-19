import { useCallback, useEffect, useState } from 'react'
import {
  addLocalGroupMember,
  changeAccountType,
  createLocalUser,
  deleteLocalUser,
  forceLocalUserPasswordChange,
  getDeviceTasks,
  getLocalGroups,
  getLocalUsers,
  removeLocalGroupMember,
  resetLocalUserPassword,
  setLocalUserEnabled,
  type LocalGroupRow,
  type CreateLocalUserBody,
  type LocalUserRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { CreateLocalUserDialog } from './CreateLocalUserDialog'

type Pending = { taskId: string; label: string; stage: string }

/**
 * What the operator is told while a task is in flight. Deliberately never claims the
 * account exists until the device has reported it back: an optimistic "created" that
 * later turns out false is worse than a slower truthful one.
 */
function stageFor(status: string): string {
  switch (status) {
    case 'Queued':
      return 'Waiting for the device to check in\u2026'
    case 'Delivered':
      return 'Applying changes on Windows\u2026'
    case 'Succeeded':
      return 'Verifying Windows state and reconciling inventory\u2026'
    default:
      return 'Working\u2026'
  }
}

/**
 * Device -> Users. Every control queues a typed task; nothing here mutates Windows
 * directly, and nothing is shown as done until the endpoint reports back and the
 * refreshed inventory confirms it.
 */
export function DeviceUsersPanel({ deviceId, deviceName }: { deviceId: string; deviceName: string }) {
  const { hasPermission } = useAuth()
  const [users, setUsers] = useState<LocalUserRow[]>([])
  const [groups, setGroups] = useState<LocalGroupRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [search, setSearch] = useState('')
  const [selected, setSelected] = useState<LocalUserRow | null>(null)
  const [pending, setPending] = useState<Pending | null>(null)
  const [confirm, setConfirm] = useState<null | { title: string; body: string; run: () => Promise<void> }>(null)
  const [creating, setCreating] = useState(false)

  const load = useCallback(async () => {
    try {
      const [u, g] = await Promise.all([getLocalUsers(deviceId), getLocalGroups(deviceId)])
      setUsers(u)
      setGroups(g)
      setError(null)
    } catch {
      setError('Could not load local accounts for this device.')
    }
  }, [deviceId])

  useEffect(() => {
    void load()
  }, [load])

  // Follow the queued task to a terminal state, then reconcile against the endpoint's
  // actual reported inventory rather than assuming the requested change took effect.
  useEffect(() => {
    if (!pending) return
    let cancelled = false

    const timer = setInterval(async () => {
      try {
        const tasks = await getDeviceTasks(deviceId)
        const task = tasks.find((t) => t.id === pending.taskId)
        if (!task || cancelled) return

        // Keep the operator informed of where the work actually is.
        setPending((current) =>
          current && current.stage !== stageFor(task.status)
            ? { ...current, stage: stageFor(task.status) }
            : current)

        if (task.status === 'Succeeded' || task.status === 'Failed'
          || task.status === 'Expired' || task.status === 'Cancelled') {
          clearInterval(timer)
          setPending(null)
          setNotice(
            task.status === 'Succeeded'
              ? `${pending.label}: ${task.resultMessage ?? 'succeeded'}.`
              : `${pending.label} ${task.status.toLowerCase()}: ${task.resultMessage ?? 'no detail reported'}.`,
          )
          await load()
        }
      } catch {
        // Transient read failure: keep polling; the interval clears on unmount.
      }
    }, 3000)

    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [pending, deviceId, load])

  async function run(label: string, action: () => Promise<{ taskId: string }>) {
    setConfirm(null)
    setError(null)
    setNotice(null)
    try {
      const { taskId } = await action()
      setPending({ taskId, label, stage: stageFor('Queued') })
      setNotice(null)
    } catch (e) {
      // Safety-rule refusals (last administrator, protected account) arrive here.
      setError(e instanceof Error ? e.message : `${label} failed.`)
    }
  }

  const visible = users.filter((u) =>
    !search || u.name.toLowerCase().includes(search.toLowerCase())
    || (u.fullName ?? '').toLowerCase().includes(search.toLowerCase()))

  const canChangeType = hasPermission('user.change_type')
  const canDisable = hasPermission('user.disable')
  const canCreate = hasPermission('user.create')
  const canDelete = hasPermission('user.delete')
  const canReset = hasPermission('user.reset_password')
  const canForce = hasPermission('user.force_password_change')
  const canManageGroups = hasPermission('group.manage')

  return (
    <div className="card">
      {error && <div className="error-banner">{error}</div>}
      {notice && (
        <div className="error-banner" style={{ background: '#ecfdf5', borderColor: '#a7f3d0', color: '#065f46' }}>
          {notice}
        </div>
      )}
      {pending && (
        <div className="loading">
          <strong>{pending.label}</strong> — {pending.stage}
        </div>
      )}

      <div style={{ display: 'flex', gap: 12, marginBottom: 14, alignItems: 'center', flexWrap: 'wrap' }}>
        <input
          type="search"
          placeholder="Search users…"
          value={search}
          onChange={(e) => setSearch(e.target.value)}
          style={{ flex: '0 1 260px', padding: '7px 12px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit' }}
        />
        <div style={{ flex: 1 }} />
        {canCreate && (
          <button type="button" onClick={() => setCreating(!creating)}>
            {creating ? 'Cancel' : 'Create user'}
          </button>
        )}
      </div>

      {creating && canCreate && (
        <CreateLocalUserDialog
          deviceId={deviceId}
          deviceName={deviceName}
          onCancel={() => setCreating(false)}
          onSubmit={async (body: CreateLocalUserBody) => {
            setCreating(false)
            await run(`Create '${body.username}'`, () => createLocalUser(deviceId, body))
          }}
        />
      )}

      {visible.length === 0 && (
        <div className="empty-state">
          <div className="title">No local users reported</div>
          <div>Refresh inventory and wait for the agent's next heartbeat.</div>
        </div>
      )}

      {visible.length > 0 && (
        <table className="table">
          <thead>
            <tr>
              <th>Account</th><th>Status</th><th>Account type</th><th>SID</th><th>Last logon</th><th></th>
            </tr>
          </thead>
          <tbody>
            {visible.map((u) => (
              <tr key={u.sid}>
                <td>
                  <div style={{ fontWeight: 600 }}>{u.name}</div>
                  <div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{u.fullName ?? u.description ?? '—'}</div>
                </td>
                <td>{u.enabled ? <span className="badge ok">Enabled</span> : <span className="badge neutral">Disabled</span>}</td>
                <td>
                  {u.isLocalAdministrator
                    ? <span className="badge warn">Administrator</span>
                    : <span className="badge neutral">Standard User</span>}
                </td>
                <td style={{ fontFamily: 'monospace', fontSize: 11, color: 'var(--color-text-muted)' }}>{u.sid}</td>
                <td style={{ fontSize: 12, color: 'var(--color-text-muted)' }}>
                  {u.lastLogon ? new Date(u.lastLogon).toLocaleDateString() : '—'}
                </td>
                <td style={{ whiteSpace: 'nowrap' }}>
                  <button type="button" onClick={() => setSelected(u)}>Details</button>
                </td>
              </tr>
            ))}
          </tbody>
        </table>
      )}

      {selected && (
        <UserDetail
          user={selected}
          groups={groups}
          onClose={() => setSelected(null)}
          permissions={{ canChangeType, canDisable, canDelete, canReset, canForce, canManageGroups }}
          onAction={(label, action) => {
            setSelected(null)
            void run(label, action)
          }}
          onConfirm={setConfirm}
          deviceId={deviceId}
        />
      )}

      {confirm && (
        <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', zIndex: 50, display: 'grid', placeItems: 'center' }}>
          <div className="card" style={{ maxWidth: 460 }}>
            <h2 style={{ marginTop: 0 }}>{confirm.title}</h2>
            <p style={{ color: 'var(--color-text-muted)', fontSize: 13.5 }}>{confirm.body}</p>
            <div style={{ display: 'flex', gap: 10, justifyContent: 'flex-end' }}>
              <button type="button" onClick={() => setConfirm(null)}>Cancel</button>
              <button type="button" onClick={() => void confirm.run()}
                style={{ background: 'var(--color-crit)', color: '#fff', border: 'none', borderRadius: 6, padding: '8px 16px', fontWeight: 600, cursor: 'pointer' }}>
                Confirm
              </button>
            </div>
          </div>
        </div>
      )}
    </div>
  )
}

function UserDetail({
  user, groups, onClose, permissions, onAction, onConfirm, deviceId,
}: {
  user: LocalUserRow
  groups: LocalGroupRow[]
  onClose: () => void
  permissions: Record<string, boolean>
  onAction: (label: string, action: () => Promise<{ taskId: string }>) => void
  onConfirm: (c: { title: string; body: string; run: () => Promise<void> }) => void
  deviceId: string
}) {
  const memberOf = groups.filter((g) => (g.members ?? []).some((m) => m.sid === user.sid))
  const target = user.isLocalAdministrator ? 'StandardUser' : 'Administrator'
  const targetLabel = user.isLocalAdministrator ? 'Standard User' : 'Administrator'
  const currentLabel = user.isLocalAdministrator ? 'Administrator' : 'Standard User'

  return (
    <div style={{ position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.4)', zIndex: 40, display: 'grid', placeItems: 'center' }}>
      <div className="card" style={{ maxWidth: 620, width: '90%', maxHeight: '85vh', overflowY: 'auto' }}>
        <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'start' }}>
          <div>
            <h2 style={{ margin: 0 }}>{user.name}</h2>
            <div style={{ color: 'var(--color-text-muted)', fontSize: 13 }}>{user.fullName ?? '—'}</div>
          </div>
          <button type="button" onClick={onClose}>Close</button>
        </div>

        <table className="table" style={{ marginTop: 14 }}>
          <tbody>
            <tr><td style={{ color: 'var(--color-text-muted)', width: 170 }}>SID</td>
              <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{user.sid}</td></tr>
            <tr><td style={{ color: 'var(--color-text-muted)' }}>Status</td>
              <td>{user.enabled ? <span className="badge ok">Enabled</span> : <span className="badge neutral">Disabled</span>}</td></tr>
            <tr><td style={{ color: 'var(--color-text-muted)' }}>Account type</td>
              <td>{user.isLocalAdministrator ? <span className="badge warn">Administrator</span> : <span className="badge neutral">Standard User</span>}</td></tr>
            <tr><td style={{ color: 'var(--color-text-muted)' }}>Administrators member</td>
              <td>{user.isLocalAdministrator ? 'Yes — real BUILTIN\\Administrators membership' : 'No'}</td></tr>
            <tr><td style={{ color: 'var(--color-text-muted)' }}>Groups</td>
              <td>{memberOf.length ? memberOf.map((g) => g.name).join(', ') : '—'}</td></tr>
            <tr><td style={{ color: 'var(--color-text-muted)' }}>Last logon</td>
              <td>{user.lastLogon ? new Date(user.lastLogon).toLocaleString() : '—'}</td></tr>
            <tr><td style={{ color: 'var(--color-text-muted)' }}>Description</td>
              <td>{user.description ?? '—'}</td></tr>
          </tbody>
        </table>

        <div style={{ display: 'flex', gap: 8, flexWrap: 'wrap', marginTop: 16 }}>
          {permissions.canChangeType && (
            <button type="button"
              onClick={() => onConfirm({
                title: `Change "${user.name}" from ${currentLabel} to ${targetLabel}?`,
                body: user.isLocalAdministrator
                  ? `This will remove the account from the local Windows Administrators group on this device.`
                  : `This will add the account to the local Windows Administrators group on this device.`,
                run: async () => onAction(
                  `Change ${user.name} to ${targetLabel}`,
                  () => changeAccountType(deviceId, user.sid, target as 'Administrator' | 'StandardUser')),
              })}
              style={{ fontWeight: 600 }}>
              Change to {targetLabel}
            </button>
          )}

          {permissions.canDisable && (
            <button type="button"
              onClick={() => onConfirm({
                title: `${user.enabled ? 'Disable' : 'Enable'} "${user.name}"?`,
                body: user.enabled
                  ? 'The account will not be able to sign in until it is re-enabled.'
                  : 'The account will be able to sign in again.',
                run: async () => onAction(
                  `${user.enabled ? 'Disable' : 'Enable'} ${user.name}`,
                  () => setLocalUserEnabled(deviceId, user.sid, !user.enabled)),
              })}>
              {user.enabled ? 'Disable' : 'Enable'}
            </button>
          )}

          {permissions.canReset && (
            <button type="button" onClick={() => {
              const password = window.prompt(`New password for "${user.name}" (min 8 characters):`)
              if (!password) return
              onAction(`Reset password for ${user.name}`, () => resetLocalUserPassword(deviceId, user.sid, password))
            }}>
              Reset password
            </button>
          )}

          {permissions.canForce && (
            <button type="button"
              onClick={() => onAction(
                `Force password change for ${user.name}`,
                () => forceLocalUserPasswordChange(deviceId, user.sid))}>
              Force password change
            </button>
          )}

          {permissions.canManageGroups && (
            <button type="button" onClick={() => {
              const groupName = window.prompt(`Add "${user.name}" to which local group?\n\n${groups.map((g) => g.name).join(', ')}`)
              if (!groupName) return
              const group = groups.find((g) => g.name.toLowerCase() === groupName.toLowerCase())
              if (!group) return
              onAction(`Add ${user.name} to ${group.name}`, () => addLocalGroupMember(deviceId, group.sid, user.sid))
            }}>
              Add to group
            </button>
          )}

          {permissions.canManageGroups && memberOf.length > 0 && (
            <button type="button" onClick={() => {
              const groupName = window.prompt(`Remove "${user.name}" from which group?\n\n${memberOf.map((g) => g.name).join(', ')}`)
              if (!groupName) return
              const group = memberOf.find((g) => g.name.toLowerCase() === groupName.toLowerCase())
              if (!group) return
              onAction(`Remove ${user.name} from ${group.name}`, () => removeLocalGroupMember(deviceId, group.sid, user.sid))
            }}>
              Remove from group
            </button>
          )}

          {permissions.canDelete && (
            <button type="button"
              onClick={() => onConfirm({
                title: `Delete "${user.name}"?`,
                body: `This permanently removes the local Windows account${user.isLocalAdministrator ? ' — it is currently an Administrator' : ''}. The profile on disk is not removed.`,
                run: async () => onAction(`Delete ${user.name}`, () => deleteLocalUser(deviceId, user.sid)),
              })}
              style={{ border: '1px solid #fca5a5', color: 'var(--color-crit)' }}>
              Delete
            </button>
          )}
        </div>
      </div>
    </div>
  )
}

