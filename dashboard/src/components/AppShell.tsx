import { NavLink, Outlet, useLocation } from 'react-router-dom'

interface NavItem {
  to: string
  label: string
}

/**
 * Primary navigation. Routes that are planned but not yet implemented still get
 * an entry so the information architecture is visible from day one; their pages
 * render an explicit "not yet implemented" empty state rather than pretending.
 */
const NAV_ITEMS: NavItem[] = [
  { to: '/', label: 'Dashboard' },
  { to: '/devices', label: 'Devices' },
  { to: '/users', label: 'Users' },
  { to: '/groups', label: 'Groups' },
  { to: '/software', label: 'Software' },
  { to: '/policies', label: 'Policies' },
  { to: '/updates', label: 'Updates' },
  { to: '/security', label: 'Security' },
  { to: '/tasks', label: 'Tasks' },
  { to: '/audit', label: 'Audit Logs' },
  { to: '/settings', label: 'Settings' },
]

const TITLES: Record<string, string> = Object.fromEntries(
  NAV_ITEMS.map((item) => [item.to, item.label]),
)

export function AppShell() {
  const location = useLocation()
  const title = TITLES[location.pathname] ?? 'Endpoint Platform'

  return (
    <div className="app-shell">
      <aside className="sidebar">
        <div className="sidebar-brand">
          <div className="product">Endpoint Platform</div>
          <div className="tagline">Endpoint management</div>
        </div>
        <nav className="sidebar-nav" aria-label="Primary">
          {NAV_ITEMS.map((item) => (
            <NavLink key={item.to} to={item.to} end={item.to === '/'}>
              {item.label}
            </NavLink>
          ))}
        </nav>
        <div className="sidebar-footer">v0.1.0 · Phase 0</div>
      </aside>

      <div className="main">
        <header className="topbar">
          <h1>{title}</h1>
        </header>
        <main className="content">
          <Outlet />
        </main>
      </div>
    </div>
  )
}
