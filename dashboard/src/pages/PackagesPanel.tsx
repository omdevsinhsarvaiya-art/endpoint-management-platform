import { useCallback, useEffect, useState } from 'react'
import {
  deployPackageToDevice,
  getDevices,
  getPackages,
  uploadPackage,
  restorePackage,
  withdrawPackage,
  type PackageRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'

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
    // Says what it does and, as importantly, what it does not: nothing is
    // uninstalled and no device changes. Only future deployment stops.
    if (
      !window.confirm(
        `Disable "${pkg.name} ${pkg.version}"? It can no longer be deployed or downloaded. ` +
          'Devices that already have it are unaffected and nothing is uninstalled.',
      )
    ) {
      return
    }
    try {
      await withdrawPackage(pkg.id)
      await load()
    } catch {
      setError('Could not withdraw the package.')
    }
  }

  async function onRestore(pkg: PackageRow) {
    if (!window.confirm(`Enable "${pkg.name} ${pkg.version}"? It becomes deployable again.`)) return
    try {
      await restorePackage(pkg.id)
      await load()
    } catch {
      setError('Could not enable the package.')
    }
  }

  const ready = !busy && file && name.trim() && version.trim() && productCode.trim()

  return (
    <div className="card card-section">
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <div className="card-header">
        <div>
          <h2>Packages</h2>
          <div className="muted" style={{ fontSize: 12.5, marginTop: 3, maxWidth: 620 }}>
            Approved MSI packages. The agent verifies the content hash and the Authenticode signer
            before installing through the Windows Installer service — it never runs a shell.
          </div>
        </div>
        {canDeploy && (
          <button
            type="button"
            className={showForm ? undefined : 'btn-primary'}
            onClick={() => setShowForm(!showForm)}
          >
            {!showForm && <Icon name="plus" size={14} />}
            {showForm ? 'Cancel' : 'Register package'}
          </button>
        )}
      </div>

      {showForm && canDeploy && (
        <div className="form-grid" style={{ marginBottom: 16 }}>
          <div className="field">
            <label className="field-label" htmlFor="pkg-file">
              MSI file
            </label>
            <input
              id="pkg-file"
              type="file"
              accept=".msi"
              onChange={(e) => setFile(e.target.files?.[0] ?? null)}
            />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="pkg-name">
              Name
            </label>
            <input
              id="pkg-name"
              value={name}
              onChange={(e) => setName(e.target.value)}
              placeholder="Contoso App"
            />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="pkg-version">
              Version
            </label>
            <input
              id="pkg-version"
              value={version}
              onChange={(e) => setVersion(e.target.value)}
              placeholder="1.2.3"
            />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="pkg-publisher">
              Publisher <span className="muted">(optional)</span>
            </label>
            <input
              id="pkg-publisher"
              value={publisher}
              onChange={(e) => setPublisher(e.target.value)}
              placeholder="Contoso Ltd"
            />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="pkg-code">
              MSI ProductCode
            </label>
            <input
              id="pkg-code"
              value={productCode}
              onChange={(e) => setProductCode(e.target.value)}
              placeholder="{GUID}"
            />
          </div>
          <div className="field">
            <label className="field-label" htmlFor="pkg-signer">
              Required signer <span className="muted">(optional)</span>
            </label>
            <input
              id="pkg-signer"
              value={signer}
              onChange={(e) => setSigner(e.target.value)}
              placeholder="CN=Contoso Ltd"
            />
          </div>
          <div className="full btn-row" style={{ marginBottom: 4 }}>
            <button
              type="button"
              className={`btn-primary${busy ? ' btn-loading' : ''}`}
              disabled={!ready}
              onClick={() => void onUpload()}
            >
              {busy ? 'Uploading…' : 'Register'}
            </button>
            <span className="muted" style={{ fontSize: 12 }}>
              The SHA-256 is computed in your browser and pinned server-side.
            </span>
          </div>
        </div>
      )}

      {packages.length === 0 && (
        <div className="empty-state">
          <Icon name="software" size={36} strokeWidth={1.25} className="icon" />
          <div className="title">No packages registered</div>
        </div>
      )}
      {packages.length > 0 && (
        <div className="table-wrap">
          <table className="table">
            <thead>
              <tr>
                <th>Package</th>
                <th>Version</th>
                <th>Product code</th>
                <th>Signer</th>
                <th>Status</th>
                {canDeploy && <th style={{ textAlign: 'right' }}>Actions</th>}
              </tr>
            </thead>
            <tbody>
              {packages.map((p) => (
                <tr key={p.id}>
                  <td>
                    <div>{p.name}</div>
                    <div className="row-sub">
                      {p.publisher ?? '—'} · {p.fileName}
                    </div>
                  </td>
                  <td>{p.version}</td>
                  <td>
                    <code>{p.msiProductCode}</code>
                  </td>
                  <td style={{ fontSize: 12 }}>
                    {p.requiredSignerSubject ?? <span className="muted">any trusted</span>}
                  </td>
                  <td>
                    {p.isWithdrawn ? (
                      <span className="badge crit">Disabled</span>
                    ) : (
                      <span className="badge ok">Available</span>
                    )}
                  </td>
                  {canDeploy && (
                    <td style={{ textAlign: 'right' }}>
                      {p.isWithdrawn ? (
                        <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                          {/* Disabling only removes a package from the catalogue,
                              so it is reversible: nothing was uninstalled and
                              nothing has to be rebuilt to put it back. */}
                          <button
                            type="button"
                            className="btn-sm"
                            onClick={() => void onRestore(p)}
                          >
                            Enable
                          </button>
                        </div>
                      ) : (
                        <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                          <button
                            type="button"
                            className="btn-sm"
                            onClick={() => void onDeploy(p)}
                          >
                            Deploy
                          </button>
                          {/* Stops future deployment only. Existing installs are
                              untouched, which is why this is no longer styled as
                              a destructive action. */}
                          <button
                            type="button"
                            className="btn-sm"
                            onClick={() => void onWithdraw(p)}
                          >
                            Disable
                          </button>
                        </div>
                      )}
                    </td>
                  )}
                </tr>
              ))}
            </tbody>
          </table>
        </div>
      )}
    </div>
  )
}
