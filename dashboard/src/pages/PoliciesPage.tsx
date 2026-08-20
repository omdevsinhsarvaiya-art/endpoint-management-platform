import { useCallback, useEffect, useState } from 'react'
import {
  createScreenLockPolicy,
  getPolicies,
  type PolicyRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'

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
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          Desired-state policies. Agents evaluate assigned policies and report compliance; the
          platform never silently changes a machine — remediation is an explicit, audited action.
        </div>
        {hasPermission('policy.create') && (
          <button
            type="button"
            className={creating ? undefined : 'btn-primary'}
            onClick={() => setCreating(!creating)}
          >
            {!creating && <Icon name="plus" size={14} />}
            {creating ? 'Cancel' : 'New screen-lock policy'}
          </button>
        )}
      </div>

      {creating && (
        <div className="card card-section">
          <h2>New screen-lock timeout policy</h2>
          <div style={{ display: 'flex', gap: 12, alignItems: 'flex-end', flexWrap: 'wrap' }}>
            <div className="field" style={{ marginBottom: 0, width: 260 }}>
              <label className="field-label" htmlFor="policy-name">
                Name
              </label>
              <input
                id="policy-name"
                value={name}
                onChange={(e) => setName(e.target.value)}
                placeholder="Finance standard lock"
              />
            </div>
            <div className="field" style={{ marginBottom: 0, width: 140 }}>
              <label className="field-label" htmlFor="policy-minutes">
                Lock after (minutes)
              </label>
              <input
                id="policy-minutes"
                type="number"
                min={1}
                max={1440}
                value={minutes}
                onChange={(e) => setMinutes(Number(e.target.value))}
              />
            </div>
            <button
              type="button"
              className="btn-primary"
              disabled={!name.trim()}
              onClick={() => void onCreate()}
            >
              Create
            </button>
          </div>
        </div>
      )}

      <div className="card">
        {policies.length === 0 && (
          <div className="empty-state">
            <Icon name="policies" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No policies yet</div>
            <div>
              Create a screen-lock timeout policy, then assign it to a device from the device's
              Policies tab.
            </div>
          </div>
        )}
        {policies.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Policy</th>
                  <th>Type</th>
                  <th>Version</th>
                  <th>Compliant</th>
                  <th>Non-compliant</th>
                  <th>Unknown</th>
                </tr>
              </thead>
              <tbody>
                {policies.map((p) => (
                  <tr key={p.id}>
                    <td>
                      <div>{p.name}</div>
                      <div className="row-sub">{p.description}</div>
                    </td>
                    <td>{p.type}</td>
                    <td>v{p.currentVersionNumber}</td>
                    {/* Counts are tinted only when non-zero: a row of coloured
                        zeroes says "look here" about nothing. */}
                    <td>
                      <span className={`badge plain ${p.compliant > 0 ? 'ok' : 'neutral'}`}>
                        {p.compliant}
                      </span>
                    </td>
                    <td>
                      <span className={`badge plain ${p.nonCompliant > 0 ? 'crit' : 'neutral'}`}>
                        {p.nonCompliant}
                      </span>
                    </td>
                    <td>
                      <span className={`badge plain ${p.unknown > 0 ? 'warn' : 'neutral'}`}>
                        {p.unknown}
                      </span>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>
    </>
  )
}
