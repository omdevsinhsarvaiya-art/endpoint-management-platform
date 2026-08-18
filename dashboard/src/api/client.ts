/**
 * Minimal typed client for the Admin API.
 *
 * All requests go through the Vite dev proxy (`/api` -> Admin API), so the
 * browser sees one origin. Authentication headers are added here in Phase 3;
 * nothing in this module ever persists credentials to localStorage - the
 * session token will live in memory / an HttpOnly cookie, not in script-readable
 * storage.
 */

export interface ServiceInfo {
  service: string
  version: string
  environment: string
}

export interface HealthCheckEntry {
  name: string
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  durationMs: number
}

export interface HealthReport {
  status: 'Healthy' | 'Degraded' | 'Unhealthy'
  totalDurationMs: number
  checks: HealthCheckEntry[]
}

export class ApiError extends Error {
  readonly status: number
  readonly correlationId: string | null

  constructor(status: number, message: string, correlationId: string | null) {
    super(message)
    this.name = 'ApiError'
    this.status = status
    this.correlationId = correlationId
  }
}

/**
 * Fired when any API call returns 401 - the session has expired or been
 * revoked. The auth provider listens and returns the user to the login page.
 */
export const sessionExpiredEvent = new EventTarget()

async function request<T>(path: string, init?: RequestInit): Promise<T> {
  const response = await fetch(`/api${path}`, {
    ...init,
    headers: {
      Accept: 'application/json',
      // Anti-CSRF: the API refuses cookie-authenticated mutations without this.
      'X-Requested-With': 'XMLHttpRequest',
      ...init?.headers,
    },
  })

  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }

  if (!response.ok) {
    // Surface the correlation id so a user can quote it to an operator.
    const correlationId = response.headers.get('X-Correlation-Id')
    throw new ApiError(
      response.status,
      `Request to ${path} failed with HTTP ${response.status}`,
      correlationId,
    )
  }

  return (await response.json()) as T
}

export interface DeviceListItem {
  id: string
  hostname: string
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  isOnline: boolean
  enrolledAt: string
}

export interface DevicePage {
  items: DeviceListItem[]
  totalCount: number
  page: number
  pageSize: number
}

export interface DeviceCounts {
  total: number
  online: number
  offline: number
  retired: number
}

export function getServiceInfo(): Promise<ServiceInfo> {
  return request<ServiceInfo>('/')
}

export function getDevices(page: number, pageSize: number, search: string): Promise<DevicePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim().length > 0) {
    params.set('search', search.trim())
  }
  return request<DevicePage>(`/admin/v1/devices?${params}`)
}

export function getDeviceCounts(): Promise<DeviceCounts> {
  return request<DeviceCounts>('/admin/v1/devices/counts')
}

export interface DeviceDisk {
  name: string
  fileSystem: string | null
  sizeBytes: number
  freeBytes: number
}

export interface DeviceHardware {
  serialNumber: string | null
  manufacturer: string | null
  model: string | null
  cpuName: string | null
  cpuPhysicalCores: number | null
  cpuLogicalProcessors: number | null
  totalMemoryBytes: number | null
  disks: DeviceDisk[] | null
  collectedAt: string
}

export interface DeviceNetworkInterface {
  name: string
  macAddress: string | null
  ipAddresses: string[] | null
  isUp: boolean
}

export interface DeviceLocalUser {
  sid: string
  name: string
  fullName: string | null
  description: string | null
  enabled: boolean
  passwordRequired: boolean
  passwordExpires: boolean
  lastLogon: string | null
  isLocalAdministrator: boolean
}

export interface DeviceLocalGroupMember {
  name: string
  sid: string | null
  memberType: string
}

export interface DeviceLocalGroup {
  sid: string
  name: string
  description: string | null
  memberCount: number
  isAdministrators: boolean
  members: DeviceLocalGroupMember[] | null
}

export interface DeviceDetail {
  id: string
  hostname: string
  operatingSystem: string | null
  agentVersion: string
  status: 'Active' | 'Retired'
  lastSeenAt: string | null
  enrolledAt: string
  machineIdentifier: string
  loggedOnUser: string | null
  inventoryCollectedAt: string | null
  inventoryRefreshPending: boolean
  hardware: DeviceHardware | null
  networkInterfaces: DeviceNetworkInterface[]
  localUsers: DeviceLocalUser[]
  localGroups: DeviceLocalGroup[]
  software: DeviceSoftwareItem[]
  securityPosture: SecurityPosture | null
  services: DeviceServiceRow[]
  processes: DeviceProcessRow[]
}

export interface DeviceServiceRow {
  name: string
  displayName: string
  status: string
  startMode: string
}
export interface DeviceProcessRow {
  processId: number
  name: string
  workingSetBytes: number
  executablePath: string | null
  collectedAt: string
}

export interface DeviceTaskItem {
  id: string
  type: string
  status: string
  createdByDisplay: string
  createdAt: string
  deliveredAt: string | null
  completedAt: string | null
  resultMessage: string | null
}

export async function controlService(deviceId: string, serviceName: string, action: 'Start' | 'Stop' | 'Restart'): Promise<void> {
  const r = await fetch(`/api/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/control-service`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ serviceName, action }),
  })
  if (r.status === 401) sessionExpiredEvent.dispatchEvent(new Event('expired'))
  if (!r.ok) throw new ApiError(r.status, 'Service control failed', r.headers.get('X-Correlation-Id'))
}

export function getDeviceTasks(deviceId: string): Promise<DeviceTaskItem[]> {
  return request<DeviceTaskItem[]>(`/admin/v1/devices/${encodeURIComponent(deviceId)}/tasks`)
}

export async function queueDeviceAction(
  deviceId: string,
  action: 'restart' | 'shutdown' | 'lock' | 'signout',
): Promise<void> {
  const response = await fetch(
    `/api/admin/v1/devices/${encodeURIComponent(deviceId)}/actions/${action}`,
    { method: 'POST', headers: { 'X-Requested-With': 'XMLHttpRequest' } },
  )
  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }
  if (!response.ok) {
    throw new ApiError(response.status, `${action} failed`, response.headers.get('X-Correlation-Id'))
  }
}

export interface SoftwareTitle {
  name: string
  version: string | null
  publisher: string | null
  installCount: number
}
export interface SoftwareTitlePage {
  items: SoftwareTitle[]
  totalCount: number
  page: number
  pageSize: number
}
export interface DeviceSoftwareItem {
  name: string
  version: string | null
  publisher: string | null
  installDate: string | null
  architecture: string | null
}

export function getSoftwareTitles(page: number, pageSize: number, search: string, publisher: string): Promise<SoftwareTitlePage> {
  const params = new URLSearchParams({ page: String(page), pageSize: String(pageSize) })
  if (search.trim()) params.set('search', search.trim())
  if (publisher) params.set('publisher', publisher)
  return request<SoftwareTitlePage>(`/admin/v1/software?${params}`)
}
export function getSoftwarePublishers(): Promise<string[]> {
  return request<string[]>('/admin/v1/software/publishers')
}

export interface SecurityPosture {
  defenderAntivirusEnabled: boolean | null
  defenderRealtimeProtectionEnabled: boolean | null
  defenderSignatureAgeDays: number | null
  firewallDomainEnabled: boolean | null
  firewallPrivateEnabled: boolean | null
  firewallPublicEnabled: boolean | null
  secureBootEnabled: boolean | null
  tpmPresent: boolean | null
  tpmEnabled: boolean | null
  tpmSpecVersion: string | null
  bitLockerSystemDriveStatus: string | null
  localAdministratorCount: number | null
  collectedAt: string
  complianceScore: number | null
}
export interface DeviceSecuritySummary {
  deviceId: string
  hostname: string
  complianceScore: number | null
  defenderEnabled: boolean | null
  firewallEnabled: boolean | null
  secureBootEnabled: boolean | null
  tpmEnabled: boolean | null
  bitLockerSystemDriveStatus: string | null
  localAdministratorCount: number | null
  collectedAt: string
}
export interface SecurityOverview {
  summary: {
    devicesReporting: number
    averageScore: number | null
    healthy: number
    needsAttention: number
    critical: number
  }
  devices: DeviceSecuritySummary[]
}
export function getSecurityOverview(): Promise<SecurityOverview> {
  return request<SecurityOverview>('/admin/v1/security/overview')
}

export function getDevice(deviceId: string): Promise<DeviceDetail> {
  return request<DeviceDetail>(`/admin/v1/devices/${encodeURIComponent(deviceId)}`)
}

export async function requestInventoryRefresh(deviceId: string): Promise<void> {
  const response = await fetch(`/api/admin/v1/devices/${encodeURIComponent(deviceId)}/refresh-inventory`, {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
  if (response.status === 401) {
    sessionExpiredEvent.dispatchEvent(new Event('expired'))
  }
  if (!response.ok) {
    throw new ApiError(response.status, 'Inventory refresh request failed', response.headers.get('X-Correlation-Id'))
  }
}

// ---------------------------------------------------------------- auth

export interface CurrentUser {
  userId: string
  email: string
  displayName: string
  permissions: string[]
}

export async function login(email: string, password: string): Promise<CurrentUser> {
  const response = await fetch('/api/admin/v1/auth/login', {
    method: 'POST',
    headers: { 'Content-Type': 'application/json', 'X-Requested-With': 'XMLHttpRequest' },
    body: JSON.stringify({ email, password }),
  })

  if (!response.ok) {
    throw new ApiError(response.status, 'Sign-in failed', response.headers.get('X-Correlation-Id'))
  }

  // The HttpOnly session cookie was set by this response; the sessionToken field
  // in the body exists for non-browser clients and is deliberately not stored.
  const body = (await response.json()) as {
    userId: string
    email: string
    displayName: string
    permissions: string[]
  }

  return {
    userId: body.userId,
    email: body.email,
    displayName: body.displayName,
    permissions: body.permissions,
  }
}

export async function logout(): Promise<void> {
  await fetch('/api/admin/v1/auth/logout', {
    method: 'POST',
    headers: { 'X-Requested-With': 'XMLHttpRequest' },
  })
}

export function getCurrentUser(): Promise<CurrentUser> {
  return request<CurrentUser>('/admin/v1/auth/me')
}

export function getReadiness(): Promise<HealthReport> {
  // /health/ready returns 503 when unhealthy, which `request` would treat as an
  // error; a health page wants the body either way, so fetch it directly.
  return fetch('/api/health/ready', { headers: { Accept: 'application/json' } }).then(
    async (response) => (await response.json()) as HealthReport,
  )
}
