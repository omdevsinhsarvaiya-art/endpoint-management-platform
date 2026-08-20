import { useCallback, useEffect, useState } from 'react'
import { PackagesPanel } from './PackagesPanel'
import {
  getSoftwarePublishers,
  getSoftwareTitles,
  type SoftwareTitlePage,
} from '../api/client'
import { Icon } from '../components/Icon'

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
      {error && (
        <div className="error-banner" role="alert">
          <Icon name="alert" size={15} />
          <span>{error}</span>
        </div>
      )}

      <PackagesPanel />

      <div className="card">
        <h2>Installed software</h2>
        <div className="toolbar">
          <div className="input-search">
            <Icon name="search" size={15} className="search-icon" />
            <input
              type="search"
              placeholder="Search software…"
              aria-label="Search software by name"
              value={search}
              onChange={(e) => {
                setPage(1)
                setSearch(e.target.value)
              }}
            />
          </div>
          <select
            aria-label="Filter by publisher"
            style={{ width: 'auto', minWidth: 180 }}
            value={publisher}
            onChange={(e) => {
              setPage(1)
              setPublisher(e.target.value)
            }}
          >
            <option value="">All publishers</option>
            {publishers.map((p) => (
              <option key={p} value={p}>
                {p}
              </option>
            ))}
          </select>
        </div>

        {loading && !data && <div className="loading">Loading software…</div>}

        {data && data.items.length === 0 && (
          <div className="empty-state">
            <Icon name="software" size={40} strokeWidth={1.25} className="icon" />
            <div className="title">No software found</div>
            <div>
              Software appears here once devices report an inventory that includes installed
              applications.
            </div>
          </div>
        )}

        {data && data.items.length > 0 && (
          <>
            <div className="table-wrap">
              <table className="table">
                <thead>
                  <tr>
                    <th>Application</th>
                    <th>Version</th>
                    <th>Publisher</th>
                    <th>Installs</th>
                  </tr>
                </thead>
                <tbody>
                  {data.items.map((t) => (
                    <tr key={`${t.name}|${t.version}|${t.publisher}`}>
                      <td>{t.name}</td>
                      <td>{t.version ?? '—'}</td>
                      <td>{t.publisher ?? '—'}</td>
                      <td>
                        {/* A count, not a state — square marker keeps it from
                            reading as a status pill. */}
                        <span className="badge neutral plain">{t.installCount}</span>
                      </td>
                    </tr>
                  ))}
                </tbody>
              </table>
            </div>
            <div className="pagination">
              <span>
                {data.totalCount} distinct title{data.totalCount === 1 ? '' : 's'}
              </span>
              <span className="pager">
                <button
                  type="button"
                  className="btn-sm"
                  disabled={page <= 1}
                  onClick={() => setPage(page - 1)}
                >
                  Previous
                </button>
                <span>
                  Page {page} of {totalPages}
                </span>
                <button
                  type="button"
                  className="btn-sm"
                  disabled={page >= totalPages}
                  onClick={() => setPage(page + 1)}
                >
                  Next
                </button>
              </span>
            </div>
          </>
        )}
      </div>
    </>
  )
}
