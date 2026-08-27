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
  getLocalAdminPosture,
  type LocalAdminPosture,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { CreateLocalUserDialog } from './CreateLocalUserDialog'
import { Icon } from '../components/Icon'
import { waitForFreshInventory } from '../components/inventorySync'
import { ConfirmDialog } from '../components/ConfirmDialog'
import { DeviceElevationPanel } from './DeviceElevationPanel'
import { useDialogDismiss } from '../components/useDialogDismiss'

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
  const [posture, setPosture] = useState<LocalAdminPosture | null>(null)
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
      const [u, g, p] = await Promise.all([
        getLocalUsers(deviceId),
        getLocalGroups(deviceId),
        // Loaded alongside the accounts it is derived from, so the verdict and
        // the rows behind it are always from the same moment.
        getLocalAdminPosture(deviceId),
      ])
      setUsers(u)
      setGroups(g)
      setPosture(p)
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
          // Account rows come from inventory, which the server does not refresh
          // on task completion — so a just-created user is not in the list this
          // load fetched. Chase a fresh inventory and reload once it lands, so
          // the change appears without anyone clicking Refresh.
          if (task.status === 'Succeeded') {
            void waitForFreshInventory(deviceId).then(() => load())
          }
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
    <>
    <div className="card">
      {posture && <AdminPostureSummary posture={posture} />}
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
      {pending && (
        // In-flight work is reported as progress, never as completion: the
        // account does not exist until Windows says it does.
        <div className="info-banner" role="status">
          <div className="loading">
            <span>
              <strong>{pending.label}</strong> — {pending.stage}
            </span>
          </div>
        </div>
      )}

      <div className="toolbar">
        <div className="input-search" style={{ flexBasis: 260 }}>
          <Icon name="search" size={15} className="search-icon" />
          <input
            type="search"
            placeholder="Search users…"
            aria-label="Search local users"
            value={search}
            onChange={(e) => setSearch(e.target.value)}
          />
        </div>
        <div className="spacer" />
        {canCreate && (
          <button
            type="button"
            className={creating ? undefined : 'btn-primary'}
            onClick={() => setCreating(!creating)}
          >
            {!creating && <Icon name="plus" size={14} />}
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
          <Icon name="users" size={40} strokeWidth={1.25} className="icon" />
          <div className="title">
            {search ? 'No accounts match this search' : 'No local users reported'}
          </div>
          <div>
            {search
              ? 'Clear the search to see every reported account.'
              : 'Refresh inventory and wait for the agent’s next heartbeat.'}
          </div>
        </div>
      )}

      {visible.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Account</th>
                <th>Status</th>
                <th>Account type</th>
                <th>SID</th>
                <th>Last logon</th>
                <th style={{ textAlign: 'right' }}>Actions</th>
              </tr>
            </thead>
            <tbody>
              {visible.map((u) => (
                <tr key={u.sid}>
                  <td>
                    <div>{u.name}</div>
                    <div className="row-sub">{u.fullName ?? u.description ?? '—'}</div>
                  </td>
                  <td>
                    {u.enabled ? (
                      <span className="badge ok">Enabled</span>
                    ) : (
                      <span className="badge neutral">Disabled</span>
                    )}
                  </td>
                  <td>
                    {/* Administrator is amber, not green: elevation is a fact
                        worth noticing on a list of accounts, not a reassurance. */}
                    {u.isLocalAdministrator ? (
                      <span className="badge warn">Administrator</span>
                    ) : (
                      <span className="badge neutral">Standard User</span>
                    )}
                  </td>
                  <td className="muted" style={{ maxWidth: 220 }}>
                    <span className="truncate mono-sub" title={u.sid}>
                      {u.sid}
                    </span>
                  </td>
                  <td className="muted">
                    {u.lastLogon ? new Date(u.lastLogon).toLocaleDateString() : '—'}
                  </td>
                  <td style={{ textAlign: 'right' }}>
                    <button type="button" className="btn-sm" onClick={() => setSelected(u)}>
                      Details
                    </button>
                  </td>
                </tr>
              ))}
            </tbody>
          </table>
        </div>
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
        <ConfirmDialog
          title={confirm.title}
          onCancel={() => setConfirm(null)}
          onConfirm={() => void confirm.run()}
        >
          {confirm.body}
        </ConfirmDialog>
      )}
    </div>

    {/* A separate card, deliberately below the account list: the accounts are
        the ground truth this panel is read against. It receives the same loaded
        accounts, so eligibility and drift are computed from the snapshot the
        reader is already looking at rather than a second fetch that could
        disagree with it. */}
    <DeviceElevationPanel deviceId={deviceId} accounts={users} onChanged={() => void load()} />
    </>
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
  useDialogDismiss(onClose)

  const memberOf = groups.filter((g) => (g.members ?? []).some((m) => m.sid === user.sid))
  const target = user.isLocalAdministrator ? 'StandardUser' : 'Administrator'
  const targetLabel = user.isLocalAdministrator ? 'Standard User' : 'Administrator'
  const currentLabel = user.isLocalAdministrator ? 'Administrator' : 'Standard User'

  const canGroups = permissions.canManageGroups
  const hasPasswordActions = permissions.canReset || permissions.canForce

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="user-detail-title">
      <div className="dialog" style={{ maxWidth: 640 }}>
        <div className="dialog-header">
          <div className="card-header" style={{ marginBottom: 0 }}>
            <div>
              <h2 id="user-detail-title">{user.name}</h2>
              <div className="sub">{user.fullName ?? '—'}</div>
            </div>
            <button type="button" className="btn-ghost btn-sm" onClick={onClose}>
              Close
            </button>
          </div>
        </div>

        <div className="dialog-body">
          <dl className="kv">
            <dt>SID</dt>
            <dd>
              <code>{user.sid}</code>
            </dd>
            <dt>Status</dt>
            <dd>
              {user.enabled ? (
                <span className="badge ok">Enabled</span>
              ) : (
                <span className="badge neutral">Disabled</span>
              )}
            </dd>
            <dt>Account type</dt>
            <dd>
              {user.isLocalAdministrator ? (
                <span className="badge warn">Administrator</span>
              ) : (
                <span className="badge neutral">Standard User</span>
              )}
            </dd>
            {/* Spelled out because "Administrator" here means a real Windows group
                membership, not a flag in this platform's database. */}
            <dt>Administrators member</dt>
            <dd>
              {user.isLocalAdministrator ? 'Yes — real BUILTIN\\Administrators membership' : 'No'}
            </dd>
            <dt>Groups</dt>
            <dd>{memberOf.length ? memberOf.map((g) => g.name).join(', ') : '—'}</dd>
            <dt>Last logon</dt>
            <dd>{user.lastLogon ? new Date(user.lastLogon).toLocaleString() : '—'}</dd>
            <dt>Description</dt>
            <dd>{user.description ?? '—'}</dd>
          </dl>

          {permissions.canChangeType && (
            <div className="action-group">
              <h3>Privilege</h3>
              <p className="group-note">
                Changes real membership of the local Windows Administrators group on this device.
              </p>
              <div className="btn-row">
                <button
                  type="button"
                  className="btn-warning"
                  onClick={() =>
                    onConfirm({
                      title: `Change "${user.name}" from ${currentLabel} to ${targetLabel}?`,
                      body: user.isLocalAdministrator
                        ? `This will remove the account from the local Windows Administrators group on this device.`
                        : `This will add the account to the local Windows Administrators group on this device.`,
                      run: async () =>
                        onAction(`Change ${user.name} to ${targetLabel}`, () =>
                          changeAccountType(
                            deviceId,
                            user.sid,
                            target as 'Administrator' | 'StandardUser',
                          ),
                        ),
                    })
                  }
                >
                  Change to {targetLabel}
                </button>
              </div>
            </div>
          )}

          {(permissions.canDisable || hasPasswordActions) && (
            <div className="action-group">
              <h3>Access</h3>
              <p className="group-note">
                Controls whether the account can sign in, and what it must do at next sign-in.
              </p>
              <div className="btn-row">
                {permissions.canDisable && (
                  <button
                    type="button"
                    onClick={() =>
                      onConfirm({
                        title: `${user.enabled ? 'Disable' : 'Enable'} "${user.name}"?`,
                        body: user.enabled
                          ? 'The account will not be able to sign in until it is re-enabled.'
                          : 'The account will be able to sign in again.',
                        run: async () =>
                          onAction(`${user.enabled ? 'Disable' : 'Enable'} ${user.name}`, () =>
                            setLocalUserEnabled(deviceId, user.sid, !user.enabled),
                          ),
                      })
                    }
                  >
                    {user.enabled ? 'Disable' : 'Enable'}
                  </button>
                )}

                {permissions.canReset && (
                  <button
                    type="button"
                    onClick={() => {
                      const password = window.prompt(
                        `New password for "${user.name}" (min 8 characters):`,
                      )
                      if (!password) return
                      onAction(`Reset password for ${user.name}`, () =>
                        resetLocalUserPassword(deviceId, user.sid, password),
                      )
                    }}
                  >
                    Reset password
                  </button>
                )}

                {permissions.canForce && (
                  <button
                    type="button"
                    onClick={() =>
                      onAction(`Force password change for ${user.name}`, () =>
                        forceLocalUserPasswordChange(deviceId, user.sid),
                      )
                    }
                  >
                    Force password change
                  </button>
                )}
              </div>
            </div>
          )}

          {canGroups && (
            <div className="action-group">
              <h3>Group membership</h3>
              <p className="group-note">
                Adds or removes membership of a local group that already exists on this device.
              </p>
              <div className="btn-row">
                <button
                  type="button"
                  onClick={() => {
                    const groupName = window.prompt(
                      `Add "${user.name}" to which local group?\n\n${groups.map((g) => g.name).join(', ')}`,
                    )
                    if (!groupName) return
                    const group = groups.find(
                      (g) => g.name.toLowerCase() === groupName.toLowerCase(),
                    )
                    if (!group) return
                    onAction(`Add ${user.name} to ${group.name}`, () =>
                      addLocalGroupMember(deviceId, group.sid, user.sid),
                    )
                  }}
                >
                  Add to group
                </button>

                {memberOf.length > 0 && (
                  <button
                    type="button"
                    onClick={() => {
                      const groupName = window.prompt(
                        `Remove "${user.name}" from which group?\n\n${memberOf.map((g) => g.name).join(', ')}`,
                      )
                      if (!groupName) return
                      const group = memberOf.find(
                        (g) => g.name.toLowerCase() === groupName.toLowerCase(),
                      )
                      if (!group) return
                      onAction(`Remove ${user.name} from ${group.name}`, () =>
                        removeLocalGroupMember(deviceId, group.sid, user.sid),
                      )
                    }}
                  >
                    Remove from group
                  </button>
                )}
              </div>
            </div>
          )}

          {permissions.canDelete && (
            <div className="action-group destructive">
              <h3>Delete account</h3>
              <p className="group-note">
                Permanently removes the local Windows account. This cannot be undone from here — the
                profile directory on disk is left in place.
              </p>
              <button
                type="button"
                className="btn-danger"
                onClick={() =>
                  onConfirm({
                    title: `Delete "${user.name}"?`,
                    body: `This permanently removes the local Windows account${
                      user.isLocalAdministrator ? ' — it is currently an Administrator' : ''
                    }. The profile on disk is not removed.`,
                    run: async () =>
                      onAction(`Delete ${user.name}`, () => deleteLocalUser(deviceId, user.sid)),
                  })
                }
              >
                <Icon name="trash" size={14} />
                Delete
              </button>
            </div>
          )}
        </div>
      </div>
    </div>
  )
}

/**
 * The endpoint's local-administrator posture.
 *
 * Reports; never remediates. Milestone 11b deliberately offers no button here —
 * an administrator account is removed only by an explicit, separately authorized
 * action, never as a side effect of looking at a compliance view.
 *
 * The verdict is shown with the evidence behind it rather than alone. Someone
 * looking at a Compliant machine that visibly has an Administrator account needs
 * to see why it was discounted, or they will not believe the verdict — so the
 * excluded accounts stay on screen with their reason.
 */
function AdminPostureSummary({ posture }: { posture: LocalAdminPosture }) {
  const { compliance, interactiveAdministrators, findings, lastReportedAt } = posture

  // Unknown is a warning, not a neutral: an endpoint we know nothing about is
  // not an endpoint we have cleared.
  const badge =
    compliance === 'Compliant' ? 'ok' : compliance === 'NonCompliant' ? 'crit' : 'warn'

  const label =
    compliance === 'Compliant'
      ? 'Standard user'
      : compliance === 'NonCompliant'
        ? 'Interactive administrator'
        : 'Not yet reported'

  const excluded = findings.filter((f) => f.isAdministrator && !f.countsAgainstCompliance)

  return (
    <div className="form-section" style={{ marginTop: 0 }}>
      <div style={{ display: 'flex', alignItems: 'center', gap: 10, flexWrap: 'wrap' }}>
        <span>Local administrator posture</span>
        <span className={`badge ${badge}`}>{label}</span>
        <span className="row-sub">
          {lastReportedAt
            ? `Last reported ${new Date(lastReportedAt).toLocaleString()}`
            : 'No account inventory has been received from this endpoint yet.'}
        </span>
      </div>

      {compliance === 'NonCompliant' && (
        <div className="warn-banner" style={{ marginTop: 10, marginBottom: 0 }}>
          <div>
            {interactiveAdministrators.length === 1
              ? 'This account is an interactive local administrator:'
              : 'These accounts are interactive local administrators:'}
          </div>
          <div style={{ marginTop: 6 }}>
            {interactiveAdministrators.map((a) => (
              <code key={a.sid} className="row-sub mono-sub" title={a.sid}>
                {a.username}
              </code>
            ))}
          </div>
          <div className="row-sub" style={{ marginTop: 8 }}>
            Nothing has been changed on this endpoint. Removing administrator rights is a
            separate, explicit action.
          </div>
        </div>
      )}

      {excluded.length > 0 && (
        <div className="row-sub" style={{ marginTop: 8 }}>
          Not counted:{' '}
          {excluded.map((f, i) => (
            <span key={f.sid}>
              {i > 0 && '; '}
              <strong>{f.username}</strong> — {f.excludedReason}
            </span>
          ))}
        </div>
      )}

      <div className="row-sub" style={{ marginTop: 8 }}>
        {posture.limitation}
      </div>
    </div>
  )
}
