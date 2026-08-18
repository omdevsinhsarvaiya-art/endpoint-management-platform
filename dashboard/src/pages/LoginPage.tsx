import { useState, type FormEvent } from 'react'
import { ApiError } from '../api/client'
import { useAuth } from '../auth/AuthContext'

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
    <div
      style={{
        minHeight: '100vh',
        display: 'flex',
        alignItems: 'center',
        justifyContent: 'center',
        background: 'var(--color-sidebar)',
      }}
    >
      <form
        onSubmit={(e) => void onSubmit(e)}
        className="card"
        style={{ width: 360, padding: '28px 30px' }}
      >
        <div style={{ marginBottom: 20 }}>
          <div style={{ fontSize: 18, fontWeight: 650 }}>Endpoint Platform</div>
          <div style={{ color: 'var(--color-text-muted)', fontSize: 13, marginTop: 2 }}>
            Sign in to continue
          </div>
        </div>

        {error && <div className="error-banner">{error}</div>}

        <label style={{ display: 'block', fontSize: 13, fontWeight: 600, marginBottom: 6 }}>
          Email
          <input
            type="email"
            autoComplete="username"
            required
            value={email}
            onChange={(e) => setEmail(e.target.value)}
            style={inputStyle}
          />
        </label>

        <label style={{ display: 'block', fontSize: 13, fontWeight: 600, margin: '12px 0 6px' }}>
          Password
          <input
            type="password"
            autoComplete="current-password"
            required
            value={password}
            onChange={(e) => setPassword(e.target.value)}
            style={inputStyle}
          />
        </label>

        <button
          type="submit"
          disabled={busy}
          style={{
            width: '100%',
            marginTop: 18,
            padding: '9px 0',
            background: 'var(--color-primary)',
            color: '#fff',
            border: 'none',
            borderRadius: 6,
            font: 'inherit',
            fontWeight: 600,
            cursor: busy ? 'wait' : 'pointer',
          }}
        >
          {busy ? 'Signing in…' : 'Sign in'}
        </button>
      </form>
    </div>
  )
}

const inputStyle: React.CSSProperties = {
  display: 'block',
  width: '100%',
  marginTop: 4,
  padding: '8px 10px',
  border: '1px solid var(--color-border)',
  borderRadius: 6,
  font: 'inherit',
  fontWeight: 400,
}
