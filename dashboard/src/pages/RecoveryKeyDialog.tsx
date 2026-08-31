import { useCallback, useEffect, useRef, useState, type FormEvent } from 'react'
import {
  ApiError,
  escrowRecoveryKey,
  revealRecoveryKey,
  type BitLockerVolumeRow,
  type EscrowRow,
} from '../api/client'
import { useDialogDismiss } from '../components/useDialogDismiss'
import {
  formatRecoveryPasswordInput,
  hasExpired,
  looksLikeRecoveryPassword,
  secondsRemaining,
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
    <div className="dialog-backdrop">
      <div className="dialog" role="dialog" aria-modal="true">
        <h2>Escrow a recovery key</h2>

        <p className="muted">
          Volume {volume.driveLetter ?? volume.deviceIdentifier}. The key is encrypted before it is
          stored and is never shown again unless an administrator explicitly reveals it.
        </p>

        <form onSubmit={submit}>
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

          <div className="dialog-actions">
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
 * administrator submits their own password and a justification, is held in
 * component state alone, and is dropped automatically after
 * its bounded lifetime (60 seconds) or when the dialog closes -- whichever comes first.
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
  const [revealed, setRevealed] = useState<string | null>(null)
  const [revealedAt, setRevealedAt] = useState<number | null>(null)
  const [now, setNow] = useState(() => Date.now())
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [copied, setCopied] = useState(false)

  useDialogDismiss(onClose)
  const timer = useRef<number | undefined>(undefined)

  const forget = useCallback(() => {
    setRevealed(null)
    setRevealedAt(null)
    setCopied(false)
  }, [])

  // Ticks only while a key is on screen, so the countdown is live and the key
  // is dropped the moment its lifetime is up even if nobody touches the page.
  useEffect(() => {
    if (revealedAt === null) return

    timer.current = window.setInterval(() => setNow(Date.now()), 1000)
    return () => window.clearInterval(timer.current)
  }, [revealedAt])

  useEffect(() => {
    if (revealedAt !== null && hasExpired(revealedAt, now)) forget()
  }, [now, revealedAt, forget])

  // Whatever ends this component -- closing, navigating, a route change -- the
  // key does not outlive it.
  useEffect(() => () => forget(), [forget])

  async function submit(event: FormEvent) {
    event.preventDefault()
    setError(null)
    setBusy(true)

    try {
      const result = await revealRecoveryKey(escrow.id, password, justification.trim())

      // The password is cleared the instant it has been used.
      setPassword('')
      setRevealed(result.recoveryPassword)
      setRevealedAt(Date.now())
      setNow(Date.now())
    } catch (e) {
      setError(e instanceof ApiError ? e.message : 'The recovery key could not be revealed.')
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="dialog-backdrop">
      <div className="dialog" role="dialog" aria-modal="true">
        <h2>Reveal recovery key</h2>

        {revealed === null ? (
          <>
            <p className="muted">
              Revealing a recovery key is recorded against your account with the reason you give
              below. Confirm your own password to continue.
            </p>

            <form onSubmit={submit}>
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

              <div className="dialog-actions">
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
          </>
        ) : (
          <>
            <div className="warn-banner">
              This key is on screen for {secondsRemaining(revealedAt!, now)} more second
              {secondsRemaining(revealedAt!, now) === 1 ? '' : 's'}, then it is cleared. Copying it
              places it on your clipboard, where it will stay until something replaces it.
            </div>

            <div className="field">
              <label>Recovery password for {escrow.driveLetter ?? escrow.volumeDeviceIdentifier}</label>
              <code
                style={{
                  display: 'block',
                  padding: '10px',
                  fontSize: '1.05em',
                  letterSpacing: '0.04em',
                  wordBreak: 'break-all',
                }}
              >
                {revealed}
              </code>
            </div>

            <div className="dialog-actions">
              <button
                type="button"
                className="btn-ghost"
                onClick={() => {
                  void navigator.clipboard?.writeText(revealed).then(() => setCopied(true))
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
