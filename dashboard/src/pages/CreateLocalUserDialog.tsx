import { useEffect, useMemo, useState } from 'react'
import {
  getUserProfiles,
  type CreateLocalUserBody,
  type ProfileCatalog,
  type UserConfigurationProfile,
} from '../api/client'

/**
 * Create Local Windows User.
 *
 * A configuration profile fills in a baseline; the operator may then override the
 * parts they are allowed to. The final step restates exactly what will change on the
 * machine, because "create a user" is not self-explanatory once account type,
 * password policy and group membership are involved.
 *
 * Nothing here is authoritative — the server re-validates the profile, the account
 * type and every group, and re-checks permissions. This form only avoids offering
 * choices that would certainly be refused.
 */
export function CreateLocalUserDialog({
  deviceId,
  deviceName,
  onCancel,
  onSubmit,
}: {
  deviceId: string
  deviceName: string
  onCancel: () => void
  onSubmit: (body: CreateLocalUserBody) => Promise<void>
}) {
  const [catalog, setCatalog] = useState<ProfileCatalog | null>(null)
  const [profileKey, setProfileKey] = useState<string>('')
  const [reviewing, setReviewing] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const [username, setUsername] = useState('')
  const [fullName, setFullName] = useState('')
  const [description, setDescription] = useState('')
  const [password, setPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [accountType, setAccountType] = useState<'StandardUser' | 'Administrator'>('StandardUser')
  const [enabled, setEnabled] = useState(true)
  const [mustChange, setMustChange] = useState(true)
  const [groups, setGroups] = useState<string[]>([])

  useEffect(() => {
    getUserProfiles(deviceId)
      .then((c) => {
        setCatalog(c)
        const first = c.profiles.find((p) => !p.grantsAdministrator) ?? c.profiles[0]
        if (first) applyProfile(first)
      })
      .catch(() => setError('Could not load configuration profiles.'))
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [deviceId])

  function applyProfile(profile: UserConfigurationProfile) {
    setProfileKey(profile.key)
    setAccountType(profile.accountType)
    setEnabled(profile.enabled)
    setMustChange(profile.mustChangePasswordAtNextLogon)
    setGroups(profile.additionalGroups)
  }

  const selected = useMemo(
    () => catalog?.profiles.find((p) => p.key === profileKey) ?? null,
    [catalog, profileKey],
  )

  // Only offer administrator if the server says this operator could get it. The
  // server still enforces it; hiding it just avoids a guaranteed rejection.
  const canGrantAdmin = catalog?.canGrantAdministrator ?? false

  // Policy allows these, but this machine does not have them. Named explicitly so a
  // missing group reads as a fact about the device, not an unexplained gap.
  const unavailableGroups = useMemo(() => {
    if (!catalog?.deviceGroupsKnown) return []
    const offered = new Set(catalog.permittedAdditionalGroups.map((g) => g.toLowerCase()))
    return (catalog.policyAdditionalGroups ?? []).filter((g) => !offered.has(g.toLowerCase()))
  }, [catalog])

  const passwordMismatch =
    password.length > 0 && confirmPassword.length > 0 && password !== confirmPassword
  const valid =
    username.trim().length > 0 && password.length >= 8 && password === confirmPassword

  const body: CreateLocalUserBody = {
    username: username.trim(),
    fullName: fullName.trim() || undefined,
    description: description.trim() || undefined,
    password,
    enabled,
    mustChangePasswordAtNextLogon: mustChange,
    accountType,
    additionalGroups: groups,
    profileKey: profileKey || undefined,
  }

  return (
    <div style={overlay}>
      <div className="card" style={{ width: 620, maxHeight: '88vh', overflowY: 'auto' }}>
        <h2 style={{ marginTop: 0 }}>
          {reviewing ? 'Review Windows account changes' : 'Create local Windows user'}
        </h2>

        {error && <div className="error-banner">{error}</div>}

        {!reviewing && (
          <>
            <label style={label}>
              Configuration profile
              <select
                value={profileKey}
                onChange={(e) => {
                  const p = catalog?.profiles.find((x) => x.key === e.target.value)
                  if (p) applyProfile(p)
                }}
                style={input}
              >
                {catalog?.profiles.map((p) => (
                  <option key={p.key} value={p.key} disabled={p.grantsAdministrator && !canGrantAdmin}>
                    {p.displayName}
                    {p.grantsAdministrator && !canGrantAdmin ? ' (needs change-type permission)' : ''}
                  </option>
                ))}
              </select>
            </label>

            {selected && (
              <div style={preview}>
                <div style={{ fontWeight: 600, marginBottom: 6 }}>{selected.displayName}</div>
                <div style={{ color: 'var(--color-text-muted)', fontSize: 12.5, marginBottom: 8 }}>
                  {selected.description}
                </div>
                <Row k="Account type" v={selected.accountType === 'Administrator' ? 'Administrator' : 'Standard User'} />
                <Row k="Status" v={selected.enabled ? 'Enabled' : 'Disabled'} />
                <Row k="Administrator membership" v={selected.grantsAdministrator ? 'Yes' : 'No'} />
                <Row k="Password expiration" v="Windows default policy" />
                <Row k="Force password change" v={selected.mustChangePasswordAtNextLogon ? 'Yes' : 'No'} />
                <Row k="Additional groups" v={selected.additionalGroups.join(', ') || 'None'} />
              </div>
            )}

            <Section title="Identity" />
            <div style={grid}>
              <label style={label}>Username
                <input value={username} onChange={(e) => setUsername(e.target.value)} style={input} autoComplete="off" />
              </label>
              <label style={label}>Display name
                <input value={fullName} onChange={(e) => setFullName(e.target.value)} style={input} autoComplete="off" />
              </label>
              <label style={{ ...label, gridColumn: '1 / -1' }}>Description
                <input value={description} onChange={(e) => setDescription(e.target.value)} style={input} autoComplete="off" />
              </label>
            </div>

            <Section title="Password" />
            <div style={grid}>
              <label style={label}>Password
                <input type="password" value={password} onChange={(e) => setPassword(e.target.value)}
                  style={input} autoComplete="new-password" />
              </label>
              <label style={label}>Confirm password
                <input type="password" value={confirmPassword} onChange={(e) => setConfirmPassword(e.target.value)}
                  style={input} autoComplete="new-password" />
              </label>
            </div>
            {passwordMismatch && (
              <div style={{ color: 'var(--color-crit)', fontSize: 13 }}>Passwords do not match.</div>
            )}
            <label style={{ ...label, flexDirection: 'row', alignItems: 'center', gap: 8, marginTop: 8 }}>
              <input type="checkbox" checked={mustChange} onChange={(e) => setMustChange(e.target.checked)} />
              User must change password at next logon
            </label>

            <Section title="Account type" />
            <div style={{ display: 'flex', gap: 18 }}>
              <label style={radio}>
                <input type="radio" checked={accountType === 'StandardUser'}
                  onChange={() => setAccountType('StandardUser')} />
                Standard User
              </label>
              <label style={{ ...radio, opacity: canGrantAdmin ? 1 : 0.5 }}>
                <input type="radio" disabled={!canGrantAdmin} checked={accountType === 'Administrator'}
                  onChange={() => setAccountType('Administrator')} />
                Administrator
              </label>
            </div>
            {accountType === 'Administrator' && (
              <div style={{ ...warn, marginTop: 8 }}>
                This account will be added to the local <strong>Administrators</strong> group and will have full
                control of this device.
              </div>
            )}

            <Section title="Account status" />
            <div style={{ display: 'flex', gap: 18 }}>
              <label style={radio}>
                <input type="radio" checked={enabled} onChange={() => setEnabled(true)} /> Enabled
              </label>
              <label style={radio}>
                <input type="radio" checked={!enabled} onChange={() => setEnabled(false)} /> Disabled
              </label>
            </div>

            {catalog && (
              <>
                <Section title="Additional groups" />
                {catalog.permittedAdditionalGroups.length > 0 ? (
                  <div style={{ display: 'flex', gap: 14, flexWrap: 'wrap' }}>
                    {catalog.permittedAdditionalGroups.map((g) => (
                      <label key={g} style={radio}>
                        <input
                          type="checkbox"
                          checked={groups.includes(g)}
                          onChange={(e) =>
                            setGroups(e.target.checked ? [...groups, g] : groups.filter((x) => x !== g))
                          }
                        />
                        {g}
                      </label>
                    ))}
                  </div>
                ) : (
                  <div style={{ color: 'var(--color-text-muted)', fontSize: 12.5 }}>
                    None of the assignable groups exist on {deviceName}.
                  </div>
                )}

                {/* Say which policy groups this machine does not have, rather than
                    omitting them silently — an operator who expected one should learn
                    that the device lacks it, not that the platform forgot it. */}
                {unavailableGroups.length > 0 && (
                  <div style={{ color: 'var(--color-text-muted)', fontSize: 12, marginTop: 6 }}>
                    Not offered because {deviceName} does not have{' '}
                    {unavailableGroups.length === 1 ? 'this group' : 'these groups'}:{' '}
                    {unavailableGroups.join(', ')}.
                  </div>
                )}

                {!catalog.deviceGroupsKnown && (
                  <div style={{ color: 'var(--color-text-muted)', fontSize: 12, marginTop: 6 }}>
                    {deviceName} has not reported its local groups yet, so all assignable groups are shown.
                    Any the device turns out not to have are skipped and reported back.
                  </div>
                )}

                <div style={{ color: 'var(--color-text-muted)', fontSize: 12, marginTop: 6 }}>
                  Administrator rights are granted through the account type above, not here.
                </div>
              </>
            )}

            <div style={actions}>
              <button type="button" onClick={onCancel}>Cancel</button>
              <button type="button" disabled={!valid} onClick={() => setReviewing(true)} style={primary}>
                Review
              </button>
            </div>
          </>
        )}

        {reviewing && (
          <>
            <div style={preview}>
              <Row k="Device" v={deviceName} />
              <Row k="Username" v={body.username} />
              <Row k="Display name" v={body.fullName ?? '—'} />
              <Row k="Account type" v={accountType === 'Administrator' ? 'Administrator' : 'Standard User'} />
              <Row k="Status" v={enabled ? 'Enabled' : 'Disabled'} />
              <Row k="Administrator" v={accountType === 'Administrator' ? 'Yes' : 'No'} />
              <Row k="Password policy" v={mustChange ? 'Force change at next logon' : 'Windows default'} />
              <Row k="Additional groups" v={groups.join(', ') || 'None'} />
            </div>

            {accountType === 'Administrator' && (
              <div style={warn}>
                <strong>{body.username}</strong> will be added to <strong>BUILTIN\Administrators</strong> on{' '}
                <strong>{deviceName}</strong>, giving it full control of the device.
              </div>
            )}

            <div style={{ color: 'var(--color-text-muted)', fontSize: 12.5, marginTop: 10 }}>
              The account is created on the endpoint itself. It appears in this list only once the device
              reports it back, so what you see afterwards is the machine's real state.
            </div>

            <div style={actions}>
              <button type="button" onClick={() => setReviewing(false)}>Back</button>
              <button type="button" style={primary} onClick={() => void onSubmit(body)}>
                Confirm &amp; create
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}

function Row({ k, v }: { k: string; v: string }) {
  return (
    <div style={{ display: 'flex', gap: 12, padding: '3px 0', fontSize: 13.5 }}>
      <span style={{ color: 'var(--color-text-muted)', width: 200, flexShrink: 0 }}>{k}</span>
      <span style={{ fontWeight: 500 }}>{v}</span>
    </div>
  )
}

function Section({ title }: { title: string }) {
  return (
    <div style={{ fontWeight: 600, fontSize: 13, margin: '18px 0 8px', color: 'var(--color-text-muted)' }}>
      {title.toUpperCase()}
    </div>
  )
}

const overlay: React.CSSProperties = {
  position: 'fixed', inset: 0, background: 'rgba(0,0,0,0.45)', zIndex: 60,
  display: 'grid', placeItems: 'center',
}
const grid: React.CSSProperties = { display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10 }
const label: React.CSSProperties = { fontSize: 13, fontWeight: 600, display: 'flex', flexDirection: 'column', gap: 4 }
const input: React.CSSProperties = {
  padding: '7px 10px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit',
}
const radio: React.CSSProperties = { display: 'flex', alignItems: 'center', gap: 6, fontSize: 13.5 }
const preview: React.CSSProperties = {
  background: 'var(--color-neutral-bg)', border: '1px solid var(--color-border)',
  borderRadius: 8, padding: 12, marginTop: 10,
}
const warn: React.CSSProperties = {
  background: '#fffbeb', border: '1px solid #fde68a', color: '#92400e',
  borderRadius: 8, padding: 10, fontSize: 13, marginTop: 10,
}
const actions: React.CSSProperties = {
  display: 'flex', gap: 10, justifyContent: 'flex-end', marginTop: 20,
}
const primary: React.CSSProperties = {
  background: 'var(--color-primary)', color: '#fff', border: 'none',
  borderRadius: 6, padding: '8px 16px', fontWeight: 600, cursor: 'pointer',
}
