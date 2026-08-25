import { useEffect, useState } from 'react'
import { NavLink, Outlet, useLocation } from 'react-router-dom'
import { useAuth } from '../auth/AuthContext'
import { getPendingEnrollments } from '../api/client'
import { Icon, type IconName } from './Icon'

interface NavItem {
  to: string
  label: string
  icon: IconName
}

interface NavSection {
  /** Omitted for the first section — a heading above "Dashboard" is noise. */
  heading?: string
  items: NavItem[]
}

/**
 * Primary navigation.
 *
 * Grouped into four sections because a flat list of twelve links makes an
 * administrator read every entry to find one. The groups follow how the work is
 * actually done — get machines in, decide who may use them, decide what runs on
 * them, then look at what happened.
 *
 * Routes that are planned but not yet implemented still get an entry so the
 * information architecture is visible from day one; their pages render an
 * explicit "not yet implemented" empty state rather than pretending.
 */
const NAV_SECTIONS: NavSection[] = [
  {
    items: [{ to: '/', label: 'Dashboard', icon: 'dashboard' }],
  },
  {
    heading: 'Estate',
    items: [
      { to: '/devices', label: 'Devices', icon: 'devices' },
      // Directly under Devices: an administrator looking for a machine that has
      // not appeared yet is already in this part of the navigation.
      { to: '/enrollments', label: 'Pending Enrollments', icon: 'enrollments' },
      { to: '/groups', label: 'Groups', icon: 'groups' },
    ],
  },
  {
    heading: 'Access',
    items: [
      { to: '/users', label: 'Users', icon: 'users' },
      { to: '/security', label: 'Security', icon: 'security' },
      // Under Access rather than Configuration: this page is the ledger of who
      // currently has a data path open off a managed machine, which is an
      // access question, not a settings one.
      { to: '/usb-access', label: 'USB Access', icon: 'usb' },
    ],
  },
  {
    heading: 'Configuration',
    items: [
      { to: '/software', label: 'Software', icon: 'software' },
      { to: '/policies', label: 'Policies', icon: 'policies' },
      { to: '/updates', label: 'Updates', icon: 'updates' },
      // /agent-releases, not /agent: nginx proxies the /agent/ prefix to the
      // Agent API for enrolled endpoints, so an /agent SPA route breaks on
      // refresh and trailing slashes by landing in the API's namespace.
      { to: '/agent-releases', label: 'Agent', icon: 'shield-check' },
    ],
  },
  {
    heading: 'Operations',
    items: [
      { to: '/tasks', label: 'Tasks', icon: 'tasks' },
      { to: '/audit', label: 'Audit Logs', icon: 'audit' },
      { to: '/settings', label: 'Settings', icon: 'settings' },
    ],
  },
]

const NAV_ITEMS = NAV_SECTIONS.flatMap((section) => section.items)

const TITLES: Record<string, string> = Object.fromEntries(
  NAV_ITEMS.map((item) => [item.to, item.label]),
)

/** Slow poll — this is an ambient count, not the Pending Enrollments page itself. */
const PENDING_POLL_MS = 30_000

export function AppShell() {
  const location = useLocation()
  const { user, logout, hasPermission } = useAuth()
  const title = TITLES[location.pathname] ?? 'Endpoint Platform'
  const pendingCount = usePendingEnrollmentCount(hasPermission('device.enroll'))

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="sidebar-brand">
          <div className="mark">
            <Icon name="shield-check" size={17} strokeWidth={2} />
          </div>
          <div>
            <div className="product">Endpoint Platform</div>
            <div className="tagline">Endpoint management</div>
          </div>
        </div>

        <nav className="sidebar-nav" aria-label="Primary">
          {NAV_SECTIONS.map((section, index) => (
            <div key={section.heading ?? `section-${index}`}>
              {section.heading && <div className="nav-group">{section.heading}</div>}
              {section.items.map((item) => (
                <NavLink key={item.to} to={item.to} end={item.to === '/'} title={item.label}>
                  <Icon name={item.icon} className="nav-icon" />
                  <span className="nav-label">{item.label}</span>
                  {item.to === '/enrollments' && pendingCount > 0 && (
                    // The number alone is ambiguous out of context, so the count
                    // is announced in words for anyone not reading the badge.
                    <span className="nav-count">
                      <span aria-hidden="true">{pendingCount}</span>
                      <span className="sr-only">
                        {pendingCount} awaiting approval
                      </span>
                    </span>
                  )}
                </NavLink>
              ))}
            </div>
          ))}
        </nav>

        <div className="sidebar-footer">v0.1.0 · Phase 0</div>
      </aside>

      <div className="main">
        <header className="topbar">
          <h1>{title}</h1>
          <div className="topbar-user">
            <span className="avatar" aria-hidden="true">
              {initialsOf(user?.email)}
            </span>
            <span className="who">{user?.email}</span>
            <button type="button" className="btn-ghost btn-sm" onClick={() => void logout()}>
              <Icon name="logout" size={14} />
              Sign out
            </button>
          </div>
        </header>
        <main className="content">
          <Outlet />
        </main>
      </div>
    </div>
  )
}

/**
 * Count of machines waiting on an approval decision, for the sidebar badge.
 *
 * Deliberately silent on failure. This is a decoration on a navigation link: if
 * the request fails the badge simply does not appear, because turning a
 * background poll into a visible error would put an error banner on every page
 * in the application. The Pending Enrollments page reports its own failures,
 * where the administrator can act on them.
 *
 * Not polled at all without `device.enroll` — an administrator who cannot decide
 * these requests should not be nagged by a count of them, and it avoids a
 * predictable 403 every thirty seconds.
 */
function usePendingEnrollmentCount(enabled: boolean): number {
  const [count, setCount] = useState(0)

  useEffect(() => {
    if (!enabled) {
      setCount(0)
      return
    }

    let cancelled = false

    async function refresh() {
      try {
        const requests = await getPendingEnrollments()
        if (cancelled) return
        const now = Date.now()
        setCount(
          requests.filter((r) => r.status === 'Pending' && new Date(r.expiresAt).getTime() > now)
            .length,
        )
      } catch {
        // Intentionally ignored — see the note above.
      }
    }

    void refresh()
    const timer = setInterval(() => void refresh(), PENDING_POLL_MS)
    return () => {
      cancelled = true
      clearInterval(timer)
    }
  }, [enabled])

  return count
}

/** "om.sarvaiya@x.com" → "OS". Falls back to a single letter, then to nothing. */
function initialsOf(email: string | undefined): string {
  if (!email) return '?'
  const [local] = email.split('@')
  const parts = local.split(/[._-]+/).filter(Boolean)
  if (parts.length >= 2) return (parts[0][0] + parts[1][0]).toUpperCase()
  return local.slice(0, 2).toUpperCase()
}
