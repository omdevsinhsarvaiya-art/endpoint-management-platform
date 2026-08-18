import { useCallback, useEffect, useState } from 'react'
import {
  deployPackageToDevice,
  getDevices,
  getPackages,
  uploadPackage,
  withdrawPackage,
  type PackageRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'

/** Software packages: register signed MSIs and deploy them to devices (software.deploy). */
export function PackagesPanel() {
  const { hasPermission } = useAuth()
  const canDeploy = hasPermission('software.deploy')
  const [packages, setPackages] = useState<PackageRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(false)
  const [showForm, setShowForm] = useState(false)

  const [file, setFile] = useState<File | null>(null)
  const [name, setName] = useState('')
  const [version, setVersion] = useState('')
  const [publisher, setPublisher] = useState('')
  const [productCode, setProductCode] = useState('')
  const [signer, setSigner] = useState('')

  const load = useCallback(async () => {
    try {
      setPackages(await getPackages())
      setError(null)
    } catch {
      setError('Could not load packages.')
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  async function onUpload() {
    if (!file) return
    setBusy(true)
    try {
      await uploadPackage(file, {
        name: name.trim(),
        version: version.trim(),
        publisher: publisher.trim() || undefined,
        msiProductCode: productCode.trim(),
        requiredSignerSubject: signer.trim() || undefined,
      })
      setFile(null)
      setName('')
      setVersion('')
      setPublisher('')
      setProductCode('')
      setSigner('')
      setShowForm(false)
      await load()
    } catch {
      setError('Upload failed. Check the product code and that the file is a .msi; a duplicate is rejected.')
    } finally {
      setBusy(false)
    }
  }

  async function onDeploy(pkg: PackageRow) {
    const hostname = window.prompt(`Deploy "${pkg.name} ${pkg.version}" to which device hostname?`)
    if (!hostname) return
    try {
      const page = await getDevices(1, 50, hostname)
      const device = page.items.find((d) => d.hostname.toLowerCase() === hostname.toLowerCase())
      if (!device) {
        setError(`No device named "${hostname}".`)
        return
      }
      await deployPackageToDevice(pkg.id, device.id)
      setError(null)
      window.alert(`Queued install of "${pkg.name}" on ${device.hostname}. The agent installs it on its next check-in.`)
    } catch {
      setError('Could not queue the deployment.')
    }
  }

  async function onWithdraw(pkg: PackageRow) {
    if (!window.confirm(`Withdraw "${pkg.name} ${pkg.version}"? It can no longer be deployed or downloaded.`)) return
    try {
      await withdrawPackage(pkg.id)
      await load()
    } catch {
      setError('Could not withdraw the package.')
    }
  }

  return (
    <div className="card card-section">
      {error && <div className="error-banner">{error}</div>}

      <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginBottom: 12 }}>
        <div>
          <h2 style={{ margin: 0 }}>Packages</h2>
          <div style={{ color: 'var(--color-text-muted)', fontSize: 13 }}>
            Approved MSI packages. The agent verifies the content hash and the Authenticode signer before
            installing through the Windows Installer service — it never runs a shell.
          </div>
        </div>
        {canDeploy && (
          <button type="button" onClick={() => setShowForm(!showForm)}>
            {showForm ? 'Cancel' : 'Register package'}
          </button>
        )}
      </div>

      {showForm && canDeploy && (
        <div style={{ display: 'grid', gridTemplateColumns: '1fr 1fr', gap: 10, marginBottom: 16, maxWidth: 720 }}>
          <label style={label}>MSI file
            <input type="file" accept=".msi" onChange={(e) => setFile(e.target.files?.[0] ?? null)} style={input} />
          </label>
          <label style={label}>Name
            <input value={name} onChange={(e) => setName(e.target.value)} placeholder="Contoso App" style={input} />
          </label>
          <label style={label}>Version
            <input value={version} onChange={(e) => setVersion(e.target.value)} placeholder="1.2.3" style={input} />
          </label>
          <label style={label}>Publisher (optional)
            <input value={publisher} onChange={(e) => setPublisher(e.target.value)} placeholder="Contoso Ltd" style={input} />
          </label>
          <label style={label}>MSI ProductCode
            <input value={productCode} onChange={(e) => setProductCode(e.target.value)} placeholder="{GUID}" style={input} />
          </label>
          <label style={label}>Required signer (optional)
            <input value={signer} onChange={(e) => setSigner(e.target.value)} placeholder="CN=Contoso Ltd" style={input} />
          </label>
          <div style={{ gridColumn: '1 / -1', display: 'flex', gap: 10, alignItems: 'center' }}>
            <button type="button" disabled={busy || !file || !name.trim() || !version.trim() || !productCode.trim()}
              onClick={() => void onUpload()}
              style={{ background: 'var(--color-primary)', color: '#fff', border: 'none', borderRadius: 6, padding: '8px 16px', fontWeight: 600, cursor: 'pointer' }}>
              {busy ? 'Uploading…' : 'Register'}
            </button>
            <span style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>
              The SHA-256 is computed in your browser and pinned server-side.
            </span>
          </div>
        </div>
      )}

      {packages.length === 0 && (
        <div className="empty-state"><div className="title">No packages registered</div></div>
      )}
      {packages.length > 0 && (
        <table className="table">
          <thead>
            <tr><th>Package</th><th>Version</th><th>Product code</th><th>Signer</th><th>Status</th>{canDeploy && <th></th>}</tr>
          </thead>
          <tbody>
            {packages.map((p) => (
              <tr key={p.id}>
                <td><div style={{ fontWeight: 600 }}>{p.name}</div><div style={{ color: 'var(--color-text-muted)', fontSize: 12 }}>{p.publisher ?? '—'} · {p.fileName}</div></td>
                <td>{p.version}</td>
                <td style={{ fontFamily: 'monospace', fontSize: 12 }}>{p.msiProductCode}</td>
                <td style={{ fontSize: 12 }}>{p.requiredSignerSubject ?? <span style={{ color: 'var(--color-text-muted)' }}>any trusted</span>}</td>
                <td>{p.isWithdrawn ? <span className="badge crit">Withdrawn</span> : <span className="badge ok">Active</span>}</td>
                {canDeploy && (
                  <td style={{ whiteSpace: 'nowrap' }}>
                    {!p.isWithdrawn && (
                      <>
                        <button type="button" onClick={() => void onDeploy(p)}>Deploy</button>{' '}
                        <button type="button" onClick={() => void onWithdraw(p)}>Withdraw</button>
                      </>
                    )}
                  </td>
                )}
              </tr>
            ))}
          </tbody>
        </table>
      )}
    </div>
  )
}

const label: React.CSSProperties = { fontSize: 13, fontWeight: 600, display: 'flex', flexDirection: 'column', gap: 4 }
const input: React.CSSProperties = { padding: '7px 10px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit' }
