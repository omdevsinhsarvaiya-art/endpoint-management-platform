import { useState, type FormEvent } from 'react'
import { changeAdminPassword, ApiError } from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'
import { validateChangePasswordForm, PASSWORD_MINIMUM_LENGTH } from '../auth/passwordPolicy'

/**
 * Changes the signed-in administrator's own password.
 *
 * Two things about this dialog are deliberate.
 *
 * **It signs you out, and says so before you commit.** Changing the password
 * rotates the account's security stamp, which invalidates every session the
 * account has -- including this one. That is the point: if the reason for
 * changing it is that the old password leaked, sessions minted with it must not
 * survive. Telling the reader up front is the difference between a security
 * property and an apparent bug.
 *
 * **Nothing is remembered.** The three values live in component state for the
 * lifetime of the dialog and go nowhere else: not to storage, not to a URL, not
 * to a log. They are cleared the moment the dialog closes, including on success,
 * so a password does not sit in a detached React tree waiting to be read.
 */
export function ChangePasswordDialog({
  onClose,
  onChanged,
}: {
  onClose: () => void
  /** Called after a successful change, so the shell can return to sign-in. */
  onChanged: (sessionsRevoked: number) => void
}) {
  const [currentPassword, setCurrentPassword] = useState('')
  const [newPassword, setNewPassword] = useState('')
  const [confirmPassword, setConfirmPassword] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const validation = validateChangePasswordForm({ currentPassword, newPassword, confirmPassword })

  function close() {
    // Cleared explicitly rather than relying on the component being discarded.
    setCurrentPassword('')
    setNewPassword('')
    setConfirmPassword('')
    onClose()
  }

  useDialogDismiss(close)

  async function submit(event: FormEvent) {
    event.preventDefault()
    if (!validation.canSubmit || busy) return

    setBusy(true)
    setError(null)

    try {
      const result = await changeAdminPassword({ currentPassword, newPassword, confirmPassword })

      // Clear before handing control back: the session is already dead, and the
      // values have no further use.
      setCurrentPassword('')
      setNewPassword('')
      setConfirmPassword('')
      onChanged(result.sessionsRevoked)
    } catch (e) {
      // The server's own wording, which explains what to do about it. The
      // client-side rules are a convenience; the server is the authority, and
      // where they disagree the reader sees the server.
      setError(
        e instanceof ApiError ? e.message : 'The password could not be changed. Please try again.',
      )
      setBusy(false)
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="change-password-title">
      <form className="dialog" style={{ maxWidth: 460 }} onSubmit={(e) => void submit(e)}>
        <div className="dialog-header">
          <h2 id="change-password-title">Change password</h2>
          <div className="sub">For your own account.</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          <div className="warn-banner" style={{ marginTop: 0 }}>
            Changing your password signs out <strong>every session for this account</strong>,
            including this one. You will need to sign in again.
          </div>

          <div className="field">
            <label className="field-label" htmlFor="cp-current">
              Current password
            </label>
            <input
              id="cp-current"
              type="password"
              value={currentPassword}
              onChange={(e) => setCurrentPassword(e.target.value)}
              autoComplete="current-password"
              autoFocus
              disabled={busy}
            />
            <div className="field-hint">
              Required even though you are signed in: a session proves who signed in, not who is
              at the keyboard now.
            </div>
          </div>

          <div className="field">
            <label className="field-label" htmlFor="cp-new">
              New password
            </label>
            <input
              id="cp-new"
              type="password"
              value={newPassword}
              onChange={(e) => setNewPassword(e.target.value)}
              autoComplete="new-password"
              disabled={busy}
              aria-invalid={validation.newPassword !== null}
              aria-describedby={validation.newPassword ? 'cp-new-error' : 'cp-new-hint'}
            />
            {validation.newPassword ? (
              <div className="field-message" id="cp-new-error">
                {validation.newPassword}
              </div>
            ) : (
              <div className="field-hint" id="cp-new-hint">
                At least {PASSWORD_MINIMUM_LENGTH} characters. Length matters more than symbols -
                a memorable passphrase beats a short complicated one.
              </div>
            )}
          </div>

          <div className="field">
            <label className="field-label" htmlFor="cp-confirm">
              Confirm new password
            </label>
            <input
              id="cp-confirm"
              type="password"
              value={confirmPassword}
              onChange={(e) => setConfirmPassword(e.target.value)}
              autoComplete="new-password"
              disabled={busy}
              aria-invalid={validation.confirmPassword !== null}
              aria-describedby={validation.confirmPassword ? 'cp-confirm-error' : undefined}
            />
            {validation.confirmPassword && (
              // Beside the field it is about, not in a banner at the top where
              // the reader has to work out which input failed.
              <div className="field-message" id="cp-confirm-error">
                {validation.confirmPassword}
              </div>
            )}
          </div>
        </div>

        <div className="dialog-footer">
          <button type="button" onClick={close} disabled={busy}>
            Cancel
          </button>
          <button type="submit" className="btn-primary" disabled={!validation.canSubmit || busy}>
            {busy ? 'Changing...' : 'Change password and sign out'}
          </button>
        </div>
      </form>
    </div>
  )
}
