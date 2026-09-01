import { useCallback, useEffect, useState, type FormEvent } from 'react'
import {
  ApiError,
  escrowRecoveryKey,
  revealRecoveryKey,
  type BitLockerVolumeRow,
  type EscrowRow,
} from '../api/client'
import { useDialogDismiss } from '../components/useDialogDismiss'
import {
  beginReveal,
  endReveal,
  formatRecoveryPasswordInput,
  isRevealed,
  looksLikeRecoveryPassword,
  noReveal,
  type RevealSession,
} from './escrowView'

/**
 * Files a recovery password against a volume's protector.
 *
 * The value is held in local state only while the form is open, submitted once,
 * and cleared on close. It is never echoed back by the server, never placed in a
 * URL, and never written to browser storage.
 */
export function EscrowKeyDialog({
  deviceId,
  volume,
  onClose,
  onSaved,
}: {
  deviceId: string
  volume: BitLockerVolumeRow
  onClose: () => void
  onSaved: () => void
}) {
  const [password, setPassword] = useState('')
  const [protectorId, setProtectorId] = useState(volume.recoveryProtectorIds?.[0] ?? '')
  const [error, setError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)

  useDialogDismiss(onClose)

  // Belt and braces: if this component unmounts for any reason, the typed key
  // goes with it rather than lingering in a closure somebody else can reach.
  useEffect(() => () => setPassword(''), [])

  const wellFormed = looksLikeRecoveryPassword(password)

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setSaving(true)

    try {
      await escrowRecoveryKey(deviceId, volume.deviceIdentifier, protectorId.trim(), password.trim())
      setPassword('')
      onSaved()
      onClose()
    } catch (e) {
      // The server's message describes the rule that failed and never quotes
      // the value, so it is safe to show.
      setError(e instanceof ApiError ? e.message : 'The recovery key could not be escrowed.')
    } finally {
      setSaving(false)
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="escrow-key-title">
      <div className="dialog" style={{ maxWidth: 520 }}>
        <div className="dialog-header">
          <h2 id="escrow-key-title">Escrow a recovery key</h2>
          <div className="sub">
            Volume {volume.driveLetter ?? volume.deviceIdentifier}. The key is encrypted before it
            is stored and is never shown again unless an administrator explicitly reveals it.
          </div>
        </div>

        <form onSubmit={submit}>
          <div className="dialog-body">
          <div className="field">
            <label htmlFor="protector">Key protector</label>
            {volume.recoveryProtectorIds?.length > 0 ? (
              <select
                id="protector"
                value={protectorId}
                onChange={(e) => setProtectorId(e.target.value)}
              >
                {volume.recoveryProtectorIds.map((id) => (
                  <option key={id} value={id}>
                    {id}
                  </option>
                ))}
              </select>
            ) : (
              <input
                id="protector"
                value={protectorId}
                onChange={(e) => setProtectorId(e.target.value)}
                placeholder="Protector GUID"
                autoComplete="off"
              />
            )}
          </div>

          <div className="field">
            <label htmlFor="recovery-key">Recovery password</label>
            <input
              id="recovery-key"
              className="recovery-input"
              value={password}
              onChange={(e) => setPassword(formatRecoveryPasswordInput(e.target.value))}
              placeholder="000000-000000-000000-000000-000000-000000-000000-000000"
              inputMode="numeric"
              // Never offered to a password manager and never restored by the
              // browser: this value belongs to a machine, not to this account.
              autoComplete="off"
              spellCheck={false}
              required
            />
            <span className="muted">
              48 digits in eight groups of six. Checked here for typos and validated again by the
              server.
            </span>
          </div>

          {password.length > 0 && !wellFormed && (
            <div className="warn-banner">
              That does not look like a BitLocker recovery password yet. Each group is six digits.
            </div>
          )}

          {error && <div className="warn-banner">{error}</div>}
          </div>

          <div className="dialog-footer">
            <button type="button" className="btn-ghost" onClick={onClose}>
              Cancel
            </button>
            <button type="submit" className="btn-primary" disabled={!wellFormed || saving || !protectorId.trim()}>
              {saving ? 'Escrowing…' : 'Escrow key'}
            </button>
          </div>
        </form>
      </div>
    </div>
  )
}

/**
 * Reveals an escrowed recovery key after step-up authentication.
 *
 * Nothing is fetched when this opens. The key is requested only when an
 * administrator submits their own password and a justification, and is held in
 * component state alone -- never in storage, a URL or router state.
 *
 * It stays on screen until the operator presses Done, and is dropped then, or
 * when the dialog unmounts for any other reason. There is no timer: 48 digits
 * read aloud or copied onto a printout takes longer than a countdown allows,
 * and a key cleared mid-recovery is worse than one the operator dismisses when
 * finished.
 */
export function RevealKeyDialog({
  escrow,
  onClose,
}: {
  escrow: EscrowRow
  onClose: () => void
}) {
  const [password, setPassword] = useState('')
  const [justification, setJustification] = useState('')
  const [session, setSession] = useState<RevealSession>(noReveal)
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)

  useDialogDismiss(onClose)

  const forget = useCallback(() => {
    setSession(endReveal())
    setCopied(false)
  }, [])

  // Whatever ends this component -- closing, navigating, a route change -- the
  // key does not outlive it. There is deliberately no timer: the key stays until
  // the operator dismisses it.
  useEffect(() => () => forget(), [forget])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)

    try {
      const result = await revealRecoveryKey(escrow.id, password, justification.trim())

      // The password is cleared the instant it has been used.
      setPassword('')
      setSession(beginReveal(result.recoveryPassword))
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'The recovery key could not be revealed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="reveal-key-title">
      {/* Wider than the other dialogs because of what it has to hold: 48 digits
          in eight groups. The cap is a maximum, not a width -- .dialog is
          width:100% inside a padded overlay, so this shrinks with the viewport
          instead of forcing a horizontal scroll on a phone. */}
      <div className="dialog" style={{ maxWidth: 'min(620px, 100%)' }}>
        <div className="dialog-header">
          <h2 id="reveal-key-title">Reveal recovery key</h2>
          <div className="sub">
            {isRevealed(session)
              ? `Recovery password for ${escrow.driveLetter ?? escrow.volumeDeviceIdentifier}`
              : 'Revealing a recovery key is recorded against your account with the reason you give below.'}
          </div>
        </div>

        {!isRevealed(session) ? (
          <form onSubmit={submit}>
            <div className="dialog-body">
              <div className="field">
                <label htmlFor="reveal-justification">Why do you need this key?</label>
                <input
                  id="reveal-justification"
                  value={justification}
                  onChange={(e) => setJustification(e.target.value)}
                  placeholder="e.g. laptop will not boot after a firmware update"
                  required
                />
              </div>

              <div className="field">
                <label htmlFor="reveal-password">Your password</label>
                <input
                  id="reveal-password"
                  type="password"
                  value={password}
                  onChange={(e) => setPassword(e.target.value)}
                  autoComplete="current-password"
                  required
                />
              </div>

              {error && <div className="warn-banner">{error}</div>}
            </div>

            <div className="dialog-footer">
              <button type="button" className="btn-ghost" onClick={onClose}>
                Cancel
              </button>
              <button
                type="submit"
                className="btn-primary"
                disabled={busy || password.length === 0 || justification.trim().length < 3}
              >
                {busy ? 'Verifying…' : 'Reveal key'}
              </button>
            </div>
          </form>
        ) : (
          <>
            <div className="dialog-body">
              <div className="warn-banner">
                This key stays on screen until you press Done. Copying it places it on your
                clipboard, where it will stay until something replaces it.
              </div>

              {/* Bordered and inset like the escrow cards, so the key reads as a
                  panel rather than as loose text against the dialog edge. The
                  value is rendered verbatim -- the grouping is BitLocker's own,
                  and nothing here reformats, splits or pads it. */}
              <code className="revealed-key">{session.key}</code>
            </div>

            <div className="dialog-footer">
              <button
                type="button"
                className="btn-ghost"
                onClick={() => {
                  void navigator.clipboard?.writeText(session.key!).then(() => setCopied(true))
                }}
              >
                {copied ? 'Copied' : 'Copy'}
              </button>
              <button
                type="button"
                className="btn-primary"
                onClick={() => {
                  forget()
                  onClose()
                }}
              >
                Done
              </button>
            </div>
          </>
        )}
      </div>
    </div>
  )
}
