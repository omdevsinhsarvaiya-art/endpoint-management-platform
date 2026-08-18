import {
  createContext,
  useCallback,
  useContext,
  useEffect,
  useMemo,
  useState,
  type ReactNode,
} from 'react'
import {
  getCurrentUser,
  login as apiLogin,
  logout as apiLogout,
  sessionExpiredEvent,
  type CurrentUser,
} from '../api/client'

interface AuthState {
  /** null while the initial session probe is in flight. */
  initializing: boolean
  user: CurrentUser | null
  login: (email: string, password: string) => Promise<void>
  logout: () => Promise<void>
  /** Convenience for hiding UI the user cannot use. NOT security - the server enforces. */
  hasPermission: (permission: string) => boolean
}

const AuthContext = createContext<AuthState | null>(null)

export function AuthProvider({ children }: { children: ReactNode }) {
  const [user, setUser] = useState<CurrentUser | null>(null)
  const [initializing, setInitializing] = useState(true)

  // Probe the session cookie on first load so a refreshed tab stays signed in.
  useEffect(() => {
    let cancelled = false

    getCurrentUser()
      .then((current) => {
        if (!cancelled) setUser(current)
      })
      .catch(() => {
        /* no session - the login page will render */
      })
      .finally(() => {
        if (!cancelled) setInitializing(false)
      })

    return () => {
      cancelled = true
    }
  }, [])

  // Any 401 from any API call drops the local user state.
  useEffect(() => {
    const onExpired = () => setUser(null)
    sessionExpiredEvent.addEventListener('expired', onExpired)
    return () => sessionExpiredEvent.removeEventListener('expired', onExpired)
  }, [])

  const login = useCallback(async (email: string, password: string) => {
    setUser(await apiLogin(email, password))
  }, [])

  const logout = useCallback(async () => {
    await apiLogout()
    setUser(null)
  }, [])

  const hasPermission = useCallback(
    (permission: string) => user?.permissions.includes(permission) ?? false,
    [user],
  )

  const value = useMemo(
    () => ({ initializing, user, login, logout, hasPermission }),
    [initializing, user, login, logout, hasPermission],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}

export function useAuth(): AuthState {
  const context = useContext(AuthContext)
  if (!context) {
    throw new Error('useAuth must be used inside AuthProvider')
  }
  return context
}
