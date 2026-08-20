import { useEffect, useMemo, useState } from 'react'
import {
  getUserProfiles,
  type CreateLocalUserBody,
  type ProfileCatalog,
  type UserConfigurationProfile,
} from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'

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
  useDialogDismiss(onCancel)

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
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="create-user-title">
      <div className="dialog" style={{ maxWidth: 640 }}>
        <div className="dialog-header">
          <div className="step-badge">Step {reviewing ? 2 : 1} of 2</div>
          <h2 id="create-user-title">
            {reviewing ? 'Review Windows account changes' : 'Create local Windows user'}
          </h2>
          <div className="sub">on {deviceName}</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          {!reviewing && (
            <>
              <div className="field">
                <label className="field-label" htmlFor="cu-profile">
                  Configuration profile
                </label>
                <select
                  id="cu-profile"
                  value={profileKey}
                  onChange={(e) => {
                    const p = catalog?.profiles.find((x) => x.key === e.target.value)
                    if (p) applyProfile(p)
                  }}
                >
                  {catalog?.profiles.map((p) => (
                    <option
                      key={p.key}
                      value={p.key}
                      disabled={p.grantsAdministrator && !canGrantAdmin}
                    >
                      {p.displayName}
                      {p.grantsAdministrator && !canGrantAdmin
                        ? ' (needs change-type permission)'
                        : ''}
                    </option>
                  ))}
                </select>
              </div>

              {selected && (
                <div className="summary">
                  <div className="summary-title">{selected.displayName}</div>
                  <div className="summary-note">{selected.description}</div>
                  <dl className="kv">
                    <dt>Account type</dt>
                    <dd>
                      {selected.accountType === 'Administrator'
                        ? 'Administrator'
                        : 'Standard User'}
                    </dd>
                    <dt>Status</dt>
                    <dd>{selected.enabled ? 'Enabled' : 'Disabled'}</dd>
                    <dt>Administrator membership</dt>
                    <dd>{selected.grantsAdministrator ? 'Yes' : 'No'}</dd>
                    <dt>Password expiration</dt>
                    <dd>Windows default policy</dd>
                    <dt>Force password change</dt>
                    <dd>{selected.mustChangePasswordAtNextLogon ? 'Yes' : 'No'}</dd>
                    <dt>Additional groups</dt>
                    <dd>{selected.additionalGroups.join(', ') || 'None'}</dd>
                  </dl>
                </div>
              )}

              <div className="form-section">Identity</div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label" htmlFor="cu-username">
                    Username
                  </label>
                  <input
                    id="cu-username"
                    value={username}
                    onChange={(e) => setUsername(e.target.value)}
                    autoComplete="off"
                  />
                </div>
                <div className="field">
                  <label className="field-label" htmlFor="cu-fullname">
                    Display name
                  </label>
                  <input
                    id="cu-fullname"
                    value={fullName}
                    onChange={(e) => setFullName(e.target.value)}
                    autoComplete="off"
                  />
                </div>
                <div className="field full">
                  <label className="field-label" htmlFor="cu-description">
                    Description
                  </label>
                  <input
                    id="cu-description"
                    value={description}
                    onChange={(e) => setDescription(e.target.value)}
                    autoComplete="off"
                  />
                </div>
              </div>

              <div className="form-section">Password</div>
              <div className="form-grid">
                <div className="field">
                  <label className="field-label" htmlFor="cu-password">
                    Password
                  </label>
                  <input
                    id="cu-password"
                    type="password"
                    value={password}
                    onChange={(e) => setPassword(e.target.value)}
                    autoComplete="new-password"
                  />
                  <div className="field-hint">At least 8 characters.</div>
                </div>
                <div className="field">
                  <label className="field-label" htmlFor="cu-confirm">
                    Confirm password
                  </label>
                  <input
                    id="cu-confirm"
                    type="password"
                    value={confirmPassword}
                    onChange={(e) => setConfirmPassword(e.target.value)}
                    autoComplete="new-password"
                    aria-invalid={passwordMismatch}
                    aria-describedby={passwordMismatch ? 'cu-confirm-error' : undefined}
                  />
                  {/* The message sits with the field it is about, not in a banner
                      at the top where the reader has to work out which input failed. */}
                  {passwordMismatch && (
                    <div className="field-message" id="cu-confirm-error">
                      <Icon name="alert" size={13} />
                      Passwords do not match.
                    </div>
                  )}
                </div>
              </div>
              <label className="check-row" style={{ marginTop: 4 }}>
                <input
                  type="checkbox"
                  checked={mustChange}
                  onChange={(e) => setMustChange(e.target.checked)}
                />
                User must change password at next logon
              </label>

              <div className="form-section">Account type</div>
              <div style={{ display: 'flex', gap: 20, flexWrap: 'wrap' }}>
                <label className="check-row">
                  <input
                    type="radio"
                    name="cu-accounttype"
                    checked={accountType === 'StandardUser'}
                    onChange={() => setAccountType('StandardUser')}
                  />
                  Standard User
                </label>
                <label className="check-row" style={{ opacity: canGrantAdmin ? 1 : 0.5 }}>
                  <input
                    type="radio"
                    name="cu-accounttype"
                    disabled={!canGrantAdmin}
                    checked={accountType === 'Administrator'}
                    onChange={() => setAccountType('Administrator')}
                  />
                  Administrator
                </label>
              </div>
              {accountType === 'Administrator' && (
                <div className="warn-banner" style={{ marginTop: 10, marginBottom: 0 }}>
                  This account will be added to the local <strong>Administrators</strong> group and
                  will have full control of this device.
                </div>
              )}

              <div className="form-section">Account status</div>
              <div style={{ display: 'flex', gap: 20, flexWrap: 'wrap' }}>
                <label className="check-row">
                  <input
                    type="radio"
                    name="cu-status"
                    checked={enabled}
                    onChange={() => setEnabled(true)}
                  />
                  Enabled
                </label>
                <label className="check-row">
                  <input
                    type="radio"
                    name="cu-status"
                    checked={!enabled}
                    onChange={() => setEnabled(false)}
                  />
                  Disabled
                </label>
              </div>

              {catalog && (
                <>
                  <div className="form-section">Additional groups</div>
                  {catalog.permittedAdditionalGroups.length > 0 ? (
                    <div style={{ display: 'flex', gap: 16, flexWrap: 'wrap' }}>
                      {catalog.permittedAdditionalGroups.map((g) => (
                        <label key={g} className="check-row">
                          <input
                            type="checkbox"
                            checked={groups.includes(g)}
                            onChange={(e) =>
                              setGroups(
                                e.target.checked ? [...groups, g] : groups.filter((x) => x !== g),
                              )
                            }
                          />
                          {g}
                        </label>
                      ))}
                    </div>
                  ) : (
                    <div className="field-hint">
                      None of the assignable groups exist on {deviceName}.
                    </div>
                  )}

                  {/* Say which policy groups this machine does not have, rather than
                      omitting them silently — an operator who expected one should learn
                      that the device lacks it, not that the platform forgot it. */}
                  {unavailableGroups.length > 0 && (
                    <div className="field-hint" style={{ marginTop: 8 }}>
                      Not offered because {deviceName} does not have{' '}
                      {unavailableGroups.length === 1 ? 'this group' : 'these groups'}:{' '}
                      {unavailableGroups.join(', ')}.
                    </div>
                  )}

                  {!catalog.deviceGroupsKnown && (
                    <div className="field-hint" style={{ marginTop: 8 }}>
                      {deviceName} has not reported its local groups yet, so all assignable groups
                      are shown. Any the device turns out not to have are skipped and reported back.
                    </div>
                  )}

                  <div className="field-hint" style={{ marginTop: 8 }}>
                    Administrator rights are granted through the account type above, not here.
                  </div>
                </>
              )}
            </>
          )}

          {reviewing && (
            <>
              <div className="summary" style={{ marginTop: 0 }}>
                <dl className="kv">
                  <dt>Device</dt>
                  <dd>{deviceName}</dd>
                  <dt>Username</dt>
                  <dd>{body.username}</dd>
                  <dt>Display name</dt>
                  <dd>{body.fullName ?? '—'}</dd>
                  <dt>Account type</dt>
                  <dd>{accountType === 'Administrator' ? 'Administrator' : 'Standard User'}</dd>
                  <dt>Status</dt>
                  <dd>{enabled ? 'Enabled' : 'Disabled'}</dd>
                  <dt>Administrator</dt>
                  <dd>{accountType === 'Administrator' ? 'Yes' : 'No'}</dd>
                  <dt>Password policy</dt>
                  <dd>{mustChange ? 'Force change at next logon' : 'Windows default'}</dd>
                  <dt>Additional groups</dt>
                  <dd>{groups.join(', ') || 'None'}</dd>
                </dl>
              </div>

              {/* The password itself is never restated here. It is submitted once and
                  is not stored anywhere this dialog could read it back from. */}
              {accountType === 'Administrator' && (
                <div className="warn-banner">
                  <strong>{body.username}</strong> will be added to{' '}
                  <strong>BUILTIN\Administrators</strong> on <strong>{deviceName}</strong>, giving
                  it full control of the device.
                </div>
              )}

              <div className="field-hint">
                The account is created on the endpoint itself. It appears in this list only once the
                device reports it back, so what you see afterwards is the machine's real state.
              </div>
            </>
          )}
        </div>

        <div className="dialog-footer">
          {!reviewing && (
            <>
              <button type="button" onClick={onCancel}>
                Cancel
              </button>
              <button
                type="button"
                className="btn-primary"
                disabled={!valid}
                onClick={() => setReviewing(true)}
              >
                Review
              </button>
            </>
          )}
          {reviewing && (
            <>
              <button type="button" onClick={() => setReviewing(false)}>
                Back
              </button>
              <button type="button" className="btn-primary" onClick={() => void onSubmit(body)}>
                Confirm &amp; create
              </button>
            </>
          )}
        </div>
      </div>
    </div>
  )
}
