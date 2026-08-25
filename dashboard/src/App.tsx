import { BrowserRouter, Link, Route, Routes } from 'react-router-dom'
import { AuthProvider, useAuth } from './auth/AuthContext'
import { AppShell } from './components/AppShell'
import { DashboardPage } from './pages/DashboardPage'
import { DeviceDetailPage } from './pages/DeviceDetailPage'
import { DevicesPage } from './pages/DevicesPage'
import { PendingEnrollmentsPage } from './pages/PendingEnrollmentsPage'
import { SecurityPage } from './pages/SecurityPage'
import { UsbAccessPage } from './pages/UsbAccessPage'
import { GroupsPage } from './pages/GroupsPage'
import { PoliciesPage } from './pages/PoliciesPage'
import { SoftwarePage } from './pages/SoftwarePage'
import { UpdatesPage } from './pages/UpdatesPage'
import { LoginPage } from './pages/LoginPage'
import { PlaceholderPage } from './pages/PlaceholderPage'
import { TasksPage } from './pages/TasksPage'
import { AgentReleasesPage } from './pages/AgentReleasesPage'

export default function App() {
  return (
    <AuthProvider>
      <AuthGate />
    </AuthProvider>
  )
}

function AuthGate() {
  const { initializing, user } = useAuth()

  if (initializing) {
    return <div className="loading" style={{ padding: 40 }}>Loading…</div>
  }

  if (!user) {
    return <LoginPage />
  }

  return (
    <BrowserRouter>
      <Routes>
        <Route element={<AppShell />}>
          <Route index element={<DashboardPage />} />
          <Route path="devices" element={<DevicesPage />} />
          <Route path="enrollments" element={<PendingEnrollmentsPage />} />
          <Route path="devices/:deviceId" element={<DeviceDetailPage />} />
          <Route
            path="users"
            element={
              <PlaceholderPage
                title="Fleet-wide Users"
                phase="Phase 4 (local user management)"
                // Local accounts are managed per machine today. Saying so turns a
                // dead end into a signpost.
                alternative={
                  <>
                    Local Windows accounts are managed per machine — open a device under{' '}
                    <Link to="/devices">Devices</Link> and use its <strong>Users</strong> tab.
                  </>
                }
              />
            }
          />
          <Route path="groups" element={<GroupsPage />} />
          <Route path="software" element={<SoftwarePage />} />
          <Route path="policies" element={<PoliciesPage />} />
          <Route path="updates" element={<UpdatesPage />} />
          <Route path="agent-releases" element={<AgentReleasesPage />} />
          <Route path="security" element={<SecurityPage />} />
          <Route path="usb-access" element={<UsbAccessPage />} />
          <Route path="tasks" element={<TasksPage />} />
          <Route
            path="audit"
            element={<PlaceholderPage title="Audit Logs" phase="Phase 3 (authentication, RBAC and audit)" />}
          />
          <Route
            path="settings"
            element={<PlaceholderPage title="Settings" phase="Phase 3 (authentication, RBAC and audit)" />}
          />
          <Route
            path="*"
            element={<PlaceholderPage title="This page" phase="a later phase (route not recognised)" />}
          />
        </Route>
      </Routes>
    </BrowserRouter>
  )
}
