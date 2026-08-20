import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'

export function LoginPage() {
  const { login } = useAuth()
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)

  async function onSubmit(event: FormEvent) {
    event.preventDefault()
    setBusy(true)
    setError(null)
    try {
      await login(email, password)
    } catch (e) {
      // Deliberately the same message for a wrong address and a wrong password:
      // distinguishing them would confirm which accounts exist.
      setError(
        e instanceof ApiError && e.status === 429
          ? 'Too many sign-in attempts. Wait a minute and try again.'
          : 'Sign-in failed. Check your email address and password.',
      )
    } finally {
      setBusy(false)
    }
  }

  return (
    <div className="login-shell">
      <form onSubmit={(e) => void onSubmit(e)} className="card login-card">
        <div className="login-brand">
          <div className="mark">
            <Icon name="shield-check" size={22} strokeWidth={2} />
          </div>
          <div className="name">Endpoint Platform</div>
          <div className="sub">Sign in to continue</div>
        </div>

        {error && (
          // Announced on appearance: a keyboard user who submitted and stayed on
          // the button would otherwise get no indication the attempt failed.
          <div className="error-banner" role="alert">
            <Icon name="alert" size={15} />
            <span>{error}</span>
          </div>
        )}

        <div className="field">
          <label className="field-label" htmlFor="login-email">
            Email
          </label>
          <input
            id="login-email"
            type="email"
            autoComplete="username"
            autoFocus
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
          />
        </div>

        <div className="field">
          <label className="field-label" htmlFor="login-password">
            Password
          </label>
          <input
            id="login-password"
            type="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
          />
        </div>

        <button
          type="submit"
          className={`btn-primary${busy ? ' btn-loading' : ''}`}
          disabled={busy}
        >
          {busy ? 'Signing in…' : 'Sign in'}
        </button>

        <div className="login-foot">Authorized administrators only. Access is audited.</div>
      </form>
    </div>
  )
}
