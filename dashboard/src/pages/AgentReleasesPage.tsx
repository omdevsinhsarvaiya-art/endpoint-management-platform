import { useCallback, useEffect, useState } from 'react'
import {
  createAgentRelease,
  downloadAgentRelease,
  getAgentReleases,
  publishAgentRelease,
  revokeAgentRelease,
  type AgentReleaseRow,
} from '../api/client'
import { useAuth } from '../auth/AuthContext'
import { Icon } from '../components/Icon'
import { ConfirmDialog } from '../components/ConfirmDialog'

/**
 * Windows Agent distribution: which build is current, downloading the MSI, and
 * the release lifecycle (upload as Draft → publish → revoke).
 *
 * The page never claims more than the platform knows: the "latest" card is the
 * newest published release, per-device installed versions live on the Devices
 * page, and an unsigned release says so in the open rather than hiding the
 * absence of a signature.
 */
export function AgentReleasesPage() {
  const { hasPermission } = useAuth()
  const canManage = hasPermission('software.deploy')

  const [releases, setReleases] = useState<AgentReleaseRow[]>([])
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)
  const [showUpload, setShowUpload] = useState(false)
  const [confirmRevoke, setConfirmRevoke] = useState<AgentReleaseRow | null>(null)

  const [file, setFile] = useState<File | null>(null)
  const [version, setVersion] = useState('')
  const [notes, setNotes] = useState('')
  const [signer, setSigner] = useState('')
  const [busy, setBusy] = useState(false)

  const load = useCallback(async () => {
    try {
      setReleases(await getAgentReleases())
      setError(null)
    } catch {
      setError('Could not load agent releases.')
    } finally {
      setLoading(false)
    }
  }, [])

  useEffect(() => {
    void load()
  }, [load])

  const latest = releases.find((r) => r.status === 'Published')

  async function onUpload() {
    if (!file) return
    setBusy(true)
    setError(null)
    setNotice(null)
    try {
      await createAgentRelease(file, {
        version: version.trim(),
        releaseNotes: notes.trim() || undefined,
        signerSubject: signer.trim() || undefined,
      })
      setNotice(`Release ${version.trim()} uploaded as a draft. Publish it to make it available.`)
      setFile(null)
      setVersion('')
      setNotes('')
      setSigner('')
      setShowUpload(false)
      await load()
    } catch {
      setError('Upload failed. Check the version is unique three-part (e.g. 1.1.0) and the file is the MSI.')
    } finally {
      setBusy(false)
    }
  }

  async function onPublish(release: AgentReleaseRow) {
    setError(null)
    setNotice(null)
    try {
      await publishAgentRelease(release.id)
      setNotice(`Release ${release.version} is now published and available to devices.`)
      await load()
    } catch {
      setError('The release could not be published.')
    }
  }

  async function onRevoke(release: AgentReleaseRow) {
    setConfirmRevoke(null)
    setError(null)
    setNotice(null)
    try {
      await revokeAgentRelease(release.id)
      setNotice(`Release ${release.version} revoked. Nothing can download or install it any more.`)
      await load()
    } catch {
      setError('The release could not be revoked.')
    }
  }

  async function onDownload(release: AgentReleaseRow) {
    setError(null)
    try {
      await downloadAgentRelease(release.id, release.fileName)
    } catch {
      setError('The download failed.')
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
      {notice && (
        <div className="notice-banner" role="status">
          <Icon name="check" size={15} />
          <span>{notice}</span>
        </div>
      )}

      <div className="page-header">
        <div className="lede">
          The Windows agent installer, versioned. Devices report the version they run; a published
          release here is what they can be updated to — after the agent itself has verified the
          download's hash and signature.
        </div>
        {canManage && (
          <button
            type="button"
            className={showUpload ? undefined : 'btn-primary'}
            onClick={() => setShowUpload(!showUpload)}
          >
            {!showUpload && <Icon name="plus" size={14} />}
            {showUpload ? 'Cancel' : 'Upload release'}
          </button>
        )}
      </div>

      {showUpload && canManage && (
        <div className="card card-section">
          <h2>New agent release</h2>
          <div className="form-grid">
            <div className="field">
              <label className="field-label" htmlFor="rel-file">
                Agent MSI
              </label>
              <input
                id="rel-file"
                type="file"
                accept=".msi"
                onChange={(e) => setFile(e.target.files?.[0] ?? null)}
              />
            </div>
            <div className="field">
              <label className="field-label" htmlFor="rel-version">
                Version
              </label>
              <input
                id="rel-version"
                value={version}
                onChange={(e) => setVersion(e.target.value)}
                placeholder="1.1.0"
              />
              <div className="field-hint">Three numeric parts. Must match the MSI's product version.</div>
            </div>
            <div className="field">
              <label className="field-label" htmlFor="rel-signer">
                Authenticode signer <span className="muted">(optional)</span>
              </label>
              <input
                id="rel-signer"
                value={signer}
                onChange={(e) => setSigner(e.target.value)}
                placeholder="CN=Your Company"
              />
              <div className="field-hint">
                Leave blank only for an unsigned development build — agents will then install on
                hash verification alone, and the release is marked unsigned.
              </div>
            </div>
            <div className="field full">
              <label className="field-label" htmlFor="rel-notes">
                Release notes <span className="muted">(optional)</span>
              </label>
              <textarea
                id="rel-notes"
                rows={3}
                value={notes}
                onChange={(e) => setNotes(e.target.value)}
                placeholder="What changed in this build."
              />
            </div>
            <div className="full btn-row" style={{ marginBottom: 4 }}>
              <button
                type="button"
                className={`btn-primary${busy ? ' btn-loading' : ''}`}
                disabled={busy || !file || !version.trim()}
                onClick={() => void onUpload()}
              >
                {busy ? 'Uploading…' : 'Upload as draft'}
              </button>
              <span className="muted" style={{ fontSize: 12 }}>
                The SHA-256 is computed in your browser and re-computed by the server as it stores
                the bytes.
              </span>
            </div>
          </div>
        </div>
      )}

      {latest && (
        <div className="card card-section">
          <div className="card-header">
            <h2>Windows Agent — latest release</h2>
            <span className="badge ok">Published</span>
          </div>
          <div className="detail-title" style={{ marginBottom: 10 }}>
            <h1 style={{ fontSize: 26, margin: 0 }}>{latest.version}</h1>
            <span className="badge neutral plain">windows / {latest.architecture}</span>
            {latest.signerSubject ? (
              <span className="badge info">Signed: {latest.signerSubject}</span>
            ) : (
              // The absence of a signature is a fact worth a badge, not a blank.
              <span className="badge warn">Unsigned build</span>
            )}
            <span className="spacer" />
            <button type="button" className="btn-primary" onClick={() => void onDownload(latest)}>
              Download MSI
            </button>
          </div>
          <dl className="kv">
            <dt>File</dt>
            <dd>{latest.fileName} ({formatBytes(latest.contentSizeBytes)})</dd>
            <dt>SHA-256</dt>
            <dd>
              <code style={{ overflowWrap: 'anywhere' }}>{latest.sha256}</code>{' '}
              <button
                type="button"
                className="btn-ghost btn-sm"
                onClick={() => void navigator.clipboard.writeText(latest.sha256)}
              >
                Copy
              </button>
            </dd>
            <dt>Released</dt>
            <dd>{latest.publishedAt ? new Date(latest.publishedAt).toLocaleString() : '—'}</dd>
            {latest.releaseNotes && (
              <>
                <dt>Release notes</dt>
                <dd style={{ whiteSpace: 'pre-wrap' }}>{latest.releaseNotes}</dd>
              </>
            )}
          </dl>
        </div>
      )}

      <div className="card">
        <h2>All releases</h2>
        {loading && <div className="loading">Loading releases…</div>}
        {!loading && releases.length === 0 && (
          <div className="empty-state">
            <Icon name="updates" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No agent releases yet</div>
            <div>Upload the built MSI to create the first release.</div>
          </div>
        )}
        {releases.length > 0 && (
          <div className="table-wrap">
            <table className="table">
              <thead>
                <tr>
                  <th>Version</th>
                  <th>Target</th>
                  <th>Status</th>
                  <th>Signer</th>
                  <th>Uploaded by</th>
                  <th>Created</th>
                  <th style={{ textAlign: 'right' }}>Actions</th>
                </tr>
              </thead>
              <tbody>
                {releases.map((r) => (
                  <tr key={r.id}>
                    <td>
                      <div>{r.version}</div>
                      <div className="row-sub">{r.fileName}</div>
                    </td>
                    <td>
                      {r.platform}/{r.architecture}
                    </td>
                    <td>
                      <span
                        className={`badge ${
                          r.status === 'Published' ? 'ok' : r.status === 'Revoked' ? 'crit' : 'neutral'
                        }`}
                      >
                        {r.status}
                      </span>
                    </td>
                    <td style={{ fontSize: 12 }}>
                      {r.signerSubject ?? <span className="muted">unsigned</span>}
                    </td>
                    <td>{r.createdByDisplay}</td>
                    <td>{new Date(r.createdAt).toLocaleDateString()}</td>
                    <td style={{ textAlign: 'right' }}>
                      <div className="btn-row" style={{ justifyContent: 'flex-end' }}>
                        {r.status === 'Published' && (
                          <button type="button" className="btn-sm" onClick={() => void onDownload(r)}>
                            Download
                          </button>
                        )}
                        {canManage && r.status === 'Draft' && (
                          <button
                            type="button"
                            className="btn-primary btn-sm"
                            onClick={() => void onPublish(r)}
                          >
                            Publish
                          </button>
                        )}
                        {canManage && r.status !== 'Revoked' && (
                          <button
                            type="button"
                            className="btn-danger btn-sm"
                            onClick={() => setConfirmRevoke(r)}
                          >
                            Revoke
                          </button>
                        )}
                      </div>
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>
        )}
      </div>

      {confirmRevoke && (
        <ConfirmDialog
          title={`Revoke agent release ${confirmRevoke.version}?`}
          confirmLabel="Yes, revoke"
          onCancel={() => setConfirmRevoke(null)}
          onConfirm={() => void onRevoke(confirmRevoke)}
        >
          <>
            Nothing will be able to download or install this build any more, and a revoked release
            cannot be re-published — upload a fresh one instead. Agents already running it are
            unaffected. This action is audited.
          </>
        </ConfirmDialog>
      )}
    </>
  )
}

function formatBytes(bytes: number): string {
  const units = ['B', 'KB', 'MB', 'GB']
  let value = bytes
  let unit = 0
  while (value >= 1024 && unit < units.length - 1) {
    value /= 1024
    unit++
  }
  return `${value.toFixed(value >= 100 ? 0 : 1)} ${units[unit]}`
}
