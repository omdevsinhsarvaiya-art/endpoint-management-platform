import { useCallback, useEffect, useState } from 'react'
import { PackagesPanel } from './PackagesPanel'
import {
  getSoftwarePublishers,
  getSoftwareTitles,
  type SoftwareTitlePage,
} from '../api/client'

const PAGE_SIZE = 30

export function SoftwarePage() {
  const [data, setData] = useState<SoftwareTitlePage | null>(null)
  const [publishers, setPublishers] = useState<string[]>([])
  const [page, setPage] = useState(1)
  const [search, setSearch] = useState('')
  const [publisher, setPublisher] = useState('')
  const [error, setError] = useState<string | null>(null)
  const [loading, setLoading] = useState(true)

  const load = useCallback(async () => {
    setLoading(true)
    try {
      setData(await getSoftwareTitles(page, PAGE_SIZE, search, publisher))
      setError(null)
    } catch {
      setError('Could not load software inventory.')
    } finally {
      setLoading(false)
    }
  }, [page, search, publisher])

  useEffect(() => {
    void load()
  }, [load])

  useEffect(() => {
    getSoftwarePublishers()
      .then(setPublishers)
      .catch(() => setPublishers([]))
  }, [])

  const totalPages = data ? Math.max(1, Math.ceil(data.totalCount / PAGE_SIZE)) : 1

  return (
    <>
      {error && <div className="error-banner">{error}</div>}

      <PackagesPanel />

      <div className="card">
        <div style={{ display: 'flex', gap: 12, marginBottom: 14, flexWrap: 'wrap' }}>
          <input
            type="search"
            placeholder="Search software…"
            value={search}
            onChange={(e) => {
              setPage(1)
              setSearch(e.target.value)
            }}
            style={{ flex: '0 1 300px', padding: '7px 12px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit' }}
          />
          <select
            value={publisher}
            onChange={(e) => {
              setPage(1)
              setPublisher(e.target.value)
            }}
            style={{ padding: '7px 12px', border: '1px solid var(--color-border)', borderRadius: 6, font: 'inherit' }}
          >
            <option value="">All publishers</option>
            {publishers.map((p) => (
              <option key={p} value={p}>{p}</option>
            ))}
          </select>
        </div>

        {loading && !data && <div className="loading">Loading software…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <div className="title">No software found</div>
            <div>Software appears here once devices report an inventory that includes installed applications.</div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <table className="table">
              <thead>
                <tr><th>Application</th><th>Version</th><th>Publisher</th><th>Installs</th></tr>
              </thead>
              <tbody>
                {data.items.map((t) => (
                  <tr key={`${t.name}|${t.version}|${t.publisher}`}>
                    <td style={{ fontWeight: 600 }}>{t.name}</td>
                    <td>{t.version ?? '—'}</td>
                    <td>{t.publisher ?? '—'}</td>
                    <td><span className="badge neutral">{t.installCount}</span></td>
                  </tr>
                ))}
              </tbody>
            </table>
            <div style={{ display: 'flex', justifyContent: 'space-between', alignItems: 'center', marginTop: 14, color: 'var(--color-text-muted)', fontSize: 13 }}>
              <span>{data.totalCount} distinct title{data.totalCount === 1 ? '' : 's'}</span>
              <span style={{ display: 'flex', gap: 8, alignItems: 'center' }}>
                <button type="button" disabled={page <= 1} onClick={() => setPage(page - 1)}>Previous</button>
                <span>Page {page} of {totalPages}</span>
                <button type="button" disabled={page >= totalPages} onClick={() => setPage(page + 1)}>Next</button>
              </span>
            </div>
          </>
        )}
      </div>
    </>
  )
}
