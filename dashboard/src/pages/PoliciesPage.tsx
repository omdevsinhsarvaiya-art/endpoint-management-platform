import { useCallback, useEffect, useState } from 'react'
import {
  createScreenLockPolicy,
  getPolicies,
  type PolicyRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'

export function PoliciesPage() {
  const { hasPermission } = useAuth()
  const [policies, setPolicies] = useState<PolicyRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [creating, setCreating] = useState(false)
  const [name, setName] = useState('')
  const [minutes, setMinutes] = useState(10)

  const load = useCallback(async () => {
    try {
      setPolicies(await getPolicies())
      setError(null)
    } catch {
      setError('Could not load policies.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  async function onCreate() {
    try {
      await createScreenLockPolicy(name.trim(), `Lock screen after ${minutes} minutes of inactivity`, minutes * 60)
      setName('')
      setCreating(false)
      await load()
    } catch {
      setError('Could not create the policy (min 30s, max 24h).')
    }
  }

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 16 }}>
        <div style={{ color: 'var(--color-text-muted)', fontSize: 13.5 }}>
          Desired-state policies. Agents evaluate assigned policies and report compliance; the platform never
          silently changes a machine — remediation is an explicit, audited action.
        </div>
        {hasPermission('policy.create') && (
          <button type="button" onClick={() => setCreating(!creating)}>
            {creating ? 'Cancel' : 'New screen-lock policy'}
          </button>
        )}
      </div>

      {creating && (
        <div className="card card-section">
          <h2>New screen-lock timeout policy</h2>
          <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
            <label style={{ fontSize: 13, fontWeight: 600 }}>
              Name
              <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Finance standard lock"
                style={{ display: 'block', marginTop: 4, padding: '7px 10px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit', width: 260 }} />
            </label>
            <label style={{ fontSize: 13, fontWeight: 600 }}>
              Lock after (minutes)
              <input type="number" min={1} max={1440} value={minutes} onChange={(e) => setMinutes(Number(e.target.value))}
                style={{ display: 'block', marginTop: 4, padding: '7px 10px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit', width: 120 }} />
            </label>
            <button type="button" disabled={!name.trim()} onClick={() => void onCreate()}
              style={{ background: 'var(--color-primary)', color: '#fff', border: 'none', borderRadius: 6, padding: '8px 16px', fontWeight: 600, cursor: 'pointer' }}>
              Create
            </button>
          </div>
        </div>
      )}

      <div className="card">
        {policies.length === 0 && (
          <div className="empty-state">
            <div className="title">No policies yet</div>
            <div>Create a screen-lock timeout policy, then assign it to a device from the device's Policies tab.</div>
          </div>
        )}
        {policies.length > 0 && (
          <table className="table">
            <thead>
              <tr><th>Policy</th><th>Type</th><th>Version</th><th>Compliant</th><th>Non-compliant</th><th>Unknown</th></tr>
            </thead>
            <tbody>
              {policies.map((p) => (
                <tr key={p.id}>
                  <td><div style={{ fontWeight: 600 }}>{p.name}</div><div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{p.description}</div></td>
                  <td>{p.type}</td>
                  <td>v{p.currentVersionNumber}</td>
                  <td><span className="badge ok">{p.compliant}</span></td>
                  <td>{p.nonCompliant > 0 ? <span className="badge crit">{p.nonCompliant}</span> : <span className="badge neutral">0</span>}</td>
                  <td>{p.unknown > 0 ? <span className="badge warn">{p.unknown}</span> : <span className="badge neutral">0</span>}</td>
                </tr>
              ))}
            </tbody>
          </table>
        )}
      </div>
    </>
  )
}
