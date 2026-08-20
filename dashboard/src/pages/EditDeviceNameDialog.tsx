import { useState, type FormEvent } from 'react'
import { ApiError, setDeviceDisplayName } from '../api/client'
import { Icon } from '../components/Icon'
import { useDialogDismiss } from '../components/useDialogDismiss'

const MAX_LENGTH = 128

/**
 * Sets the console display name for a device.
 *
 * The label is management-console naming and nothing else: saving it does not
 * rename Windows, does not touch the machine identifier or the device id, and
 * queues no work for the agent. The dialog says so plainly, because "Edit
 * device name" reads like it might rename the computer, and an administrator
 * should not have to guess which one this is.
 */
export function EditDeviceNameDialog({
  deviceId,
  hostname,
  currentDisplayName,
  onCancel,
  onSaved,
}: {
  deviceId: string
  hostname: string
  currentDisplayName: string | null
  onCancel: () => void
  onSaved: () => void | Promise<void>
}) {
  useDialogDismiss(onCancel)

  const [value, setValue] = useState(currentDisplayName ?? '')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  const trimmed = value.trim()
  const tooLong = trimmed.length > MAX_LENGTH
  const willClear = trimmed.length === 0

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    if (tooLong) return

    setBusy(true)
    setError(null)
    try {
      // Blank sends null, which clears the label server-side and restores the
      // hostname. Sending "" would be asking the server to store an empty name.
      await setDeviceDisplayName(deviceId, willClear ? null : trimmed)
      await onSaved()
    } catch (e) {
      setError(describe(e))
      setBusy(false)
    }
  }

  return (
    <div className="overlay" role="dialog" aria-modal="true" aria-labelledby="edit-name-title">
      <form className="dialog" style={{ maxWidth: 460 }} onSubmit={(e) => void onSubmit(e)}>
        <div className="dialog-header">
          <h2 id="edit-name-title">Edit device name</h2>
          <div className="sub">Console label only — this does not rename Windows.</div>
        </div>

        <div className="dialog-body">
          {error && (
            <div className="error-banner" role="alert">
              <Icon name="alert" size={15} />
              <span>{error}</span>
            </div>
          )}

          <div className="field">
            <label className="field-label" htmlFor="device-display-name">
              Device name
            </label>
            <input
              id="device-display-name"
              value={value}
              autoFocus
              maxLength={MAX_LENGTH + 1}
              placeholder={hostname}
              onChange={(e) => setValue(e.target.value)}
              aria-invalid={tooLong}
              aria-describedby="device-display-name-hint"
            />
            <div className="field-hint" id="device-display-name-hint">
              {willClear ? (
                <>
                  Leave blank to clear the label. The device will be shown as{' '}
                  <strong className="secondary">{hostname}</strong>.
                </>
              ) : (
                <>
                  This is the name shown in the management console. The Windows hostname stays{' '}
                  <strong className="secondary">{hostname}</strong>.
                </>
              )}
            </div>
            {tooLong && (
              <div className="field-message">
                <Icon name="alert" size={13} />
                Use at most {MAX_LENGTH} characters.
              </div>
            )}
          </div>
        </div>

        <div className="dialog-footer">
          <button type="button" onClick={onCancel} disabled={busy}>
            Cancel
          </button>
          <button
            type="submit"
            className={`btn-primary${busy ? ' btn-loading' : ''}`}
            disabled={busy || tooLong}
          >
            {busy ? 'Saving…' : 'Save'}
          </button>
        </div>
      </form>
    </div>
  )
}

function describe(error: unknown): string {
  if (error instanceof ApiError) {
    switch (error.status) {
      case 401:
        return 'Your session has expired. Sign in again.'
      case 403:
        return 'You do not have permission to rename devices (device.rename).'
      case 404:
        return 'That device no longer exists.'
      case 400:
        return 'That name was rejected. Use at most 128 characters.'
      default:
        return `The name could not be saved (HTTP ${error.status}${
          error.correlationId ? `, ref ${error.correlationId}` : ''
        }).`
    }
  }
  return 'The name could not be saved.'
}
